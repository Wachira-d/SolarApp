using Hangfire;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.AlertRules;
using SolarApp.Services.LineApi;
using SolarApp.Services.Notifications;
using SolarApp.Services.PredictiveDiagnostics;

namespace SolarApp.BackgroundJobs;

/// <summary>
/// Hangfire recurring job: monitors system health, detects offline systems,
/// checks cleaning/warranty due, evaluates custom alert rules, sends LINE notifications.
/// Scheduled: every 2 minutes.
/// </summary>
public class SystemHealthMonitorJob
{
    private readonly ApplicationDbContext _context;
    private readonly ILineMessagingService _lineService;
    private readonly ILogger<SystemHealthMonitorJob> _logger;
    private readonly IConfiguration _configuration;
    private readonly IAlertRulesEngine _rulesEngine;
    private readonly AlertDiagnosticsEnricher _diagnosticsEnricher;
    private readonly INotificationRouteService _routes;

    public SystemHealthMonitorJob(
        ApplicationDbContext context,
        ILineMessagingService lineService,
        ILogger<SystemHealthMonitorJob> logger,
        IConfiguration configuration,
        IAlertRulesEngine rulesEngine,
        AlertDiagnosticsEnricher diagnosticsEnricher,
        INotificationRouteService routes)
    {
        _context = context;
        _lineService = lineService;
        _logger = logger;
        _configuration = configuration;
        _rulesEngine = rulesEngine;
        _diagnosticsEnricher = diagnosticsEnricher;
        _routes = routes;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public async Task ExecuteAsync()
    {
        await CheckSystemHealthAsync();
        await CheckMaintenanceDueAsync();
        await CheckWarrantyExpiryAsync();

        try
        {
            await _rulesEngine.EvaluateAllAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Alert rules engine evaluation failed");
        }

        await SendPendingNotificationsAsync();
        await SendRecoveryNotificationsAsync();
        await EscalateUnresolvedAlertsAsync();
    }

    private async Task CheckSystemHealthAsync()
    {
        var offlineThreshold = _configuration.GetValue("InverterApis:OfflineThresholdMinutes", 15);
        var threshold = DateTime.UtcNow.AddMinutes(-offlineThreshold);
        var minFailedCalls = _configuration.GetValue("InverterApis:MinFailedCallsForAlert", 2);

        // ─── Part A: API connection lost (LastOnlineAt is our "last successful
        // cloud contact" marker; if stale, the cloud is unreachable) ───
        // Skip non-monitored sites: we have no cloud account for them, so a
        // stale LastOnlineAt is expected and must not raise alerts.
        var offlineInstallations = await _context.Installations
            .Where(i => i.Status == InstallationStatus.Active
                && i.IncludesMonitoring
                && i.LastOnlineAt != null
                && i.LastOnlineAt < threshold)
            .ToListAsync();

        // ─── Part B: Device offline during daytime (API still works, but
        // device hasn't produced anything for 3+ hours of daytime) ───
        await CheckDeviceOfflineDuringDaytimeAsync();

        foreach (var installation in offlineInstallations)
        {
            installation.Status = InstallationStatus.Offline;

            // Gate alert by consecutive API failures in recent window (noise reduction).
            // A single failed call shouldn't page on-call — wait for sustained failure.
            // Exclude RATE-LIMIT-DEFERRED entries — those are our scheduling, not a cloud outage.
            var recentFailures = await _context.InverterApiLogs
                .Where(l => l.InstallationId == installation.Id
                            && !l.IsSuccess
                            && l.RequestTime >= threshold
                            && (l.ErrorMessage == null
                                || (!l.ErrorMessage.Contains("[RATE LIMIT-DEFERRED]")
                                    && !l.ErrorMessage.Contains("[RATE LIMITED]"))))
                .CountAsync();

            if (recentFailures < minFailedCalls)
            {
                _logger.LogInformation(
                    "Installation {Id} offline but only {Count} recent failure(s) (< {Min}) — skipping alert",
                    installation.Id, recentFailures, minFailedCalls);
                continue;
            }

            var recentLogs = await _context.InverterApiLogs
                .Where(l => l.InstallationId == installation.Id && !l.IsSuccess)
                .OrderByDescending(l => l.RequestTime)
                .Take(5)
                .ToListAsync();

            var rootCause = AnalyzeRootCause(recentLogs);

            var existingAlert = await _context.SystemAlerts
                .AnyAsync(a => a.InstallationId == installation.Id
                    && a.AlertType == AlertType.Offline
                    && a.ResolvedAt == null);

            if (existingAlert) continue;

            // ─── Nighttime detection (solar-specific quiet hours) ───
            // If it's nighttime at the installation's location, device offline is
            // EXPECTED — inverters shut down with no sun. Downgrade severity and
            // avoid LINE pager spam at 3 AM.
            var isNighttime = IsLikelyNighttime(installation);

            var offlineDuration = installation.LastOnlineAt.HasValue
                ? DateTime.UtcNow - installation.LastOnlineAt.Value
                : (TimeSpan?)null;
            var durationStr = offlineDuration.HasValue
                ? offlineDuration.Value.TotalHours >= 1
                    ? $"{offlineDuration.Value.TotalHours:0.0} hours"
                    : $"{offlineDuration.Value.TotalMinutes:0} minutes"
                : "unknown duration";

            // NOTE: RATE LIMITED removed intentionally — it's our own scheduling
            // (Huawei NBI 1 req/min budget), not an outage. The HuaweiRateGate
            // defers calls without firing failure logs, so RATE LIMITED here
            // would only mean a one-off 407 from the server, not a sustained
            // problem worth waking ops up over.
            var isApiIssue = rootCause.Contains("API CONNECTION") || rootCause.Contains("TIMEOUT")
                || rootCause.Contains("NETWORK") || rootCause.Contains("AUTHENTICATION")
                || rootCause.Contains("CLOUD SERVER");

            // ─── Severity determination ───
            // API connectivity issues: Critical (our infrastructure problem)
            // Device offline during daytime: Critical (real fault)
            // Device offline during nighttime: Low (expected, no-action)
            // Short offline (< 1h) during night: skip entirely to avoid noise
            AlertSeverity severity;
            if (isApiIssue)
                severity = AlertSeverity.Critical;
            else if (isNighttime)
            {
                if (offlineDuration.HasValue && offlineDuration.Value.TotalHours < 2)
                {
                    _logger.LogInformation(
                        "Installation {Id} offline at night ({Dur}) — suppressing alert until morning check",
                        installation.Id, durationStr);
                    continue;
                }
                severity = AlertSeverity.Low; // log for the record, but no page
            }
            else
            {
                // Daytime device offline — escalate by duration
                severity = offlineDuration.HasValue && offlineDuration.Value.TotalHours >= 1
                    ? AlertSeverity.Critical
                    : AlertSeverity.High;
            }

            var nightNote = isNighttime ? " (NIGHTTIME — normal if sunset passed)" : "";

            _context.SystemAlerts.Add(new SystemAlert
            {
                InstallationId = installation.Id,
                AlertType = AlertType.Offline,
                Severity = severity,
                Title = isApiIssue
                    ? $"API Connection Lost: {installation.SiteName}"
                    : $"Device Offline: {installation.SiteName}{nightNote}",
                Message = isApiIssue
                    ? $"[{installation.InverterBrand}] {installation.SiteName}: " +
                      $"Cannot reach cloud API for {durationStr}. " +
                      "The inverter may still be working — this is a data connection issue, not necessarily an inverter problem. " +
                      $"Last successful contact: {installation.LastOnlineAt:dd/MM/yyyy HH:mm}."
                    : $"[{installation.InverterBrand}] {installation.SiteName}: " +
                      $"Inverter reports offline for {durationStr}. " +
                      "API connection is working — the inverter itself is not reporting data. " +
                      $"Last online: {installation.LastOnlineAt:dd/MM/yyyy HH:mm}. " +
                      (isNighttime
                        ? "It's currently nighttime at the site — this is normal when solar production stops. " +
                          "Will re-check at sunrise; a separate alert will fire if still offline then."
                        : "This is DAYTIME at the site — check inverter hardware, WiFi, and DC isolator."),
                RootCauseAnalysis = rootCause,
                IsNotified = false
            });

            _logger.LogWarning(
                "Installation {Id} ({Site}) marked offline — severity {Sev}, night={Night}, failures={Count}",
                installation.Id, installation.SiteName, severity, isNighttime, recentFailures);
        }

        // Auto-resolve offline alerts for installations that came back online
        var resolvedAlerts = await _context.SystemAlerts
            .Include(a => a.Installation)
            .Where(a => a.AlertType == AlertType.Offline
                && a.ResolvedAt == null
                && a.Installation.Status == InstallationStatus.Active)
            .ToListAsync();

        foreach (var alert in resolvedAlerts)
        {
            alert.ResolvedAt = DateTime.UtcNow;
        }

        // Auto-resolve LEGACY rate-limit-caused offline alerts. These were
        // created before we treated RATE LIMITED as scheduling (not an outage).
        // The user already has the dashboard visibility — no need to keep them open.
        var staleRateLimitAlerts = await _context.SystemAlerts
            .Where(a => a.AlertType == AlertType.Offline
                && a.ResolvedAt == null
                && a.RootCauseAnalysis != null
                && (a.RootCauseAnalysis.Contains("RATE LIMITED")
                    || a.RootCauseAnalysis.Contains("ACCESS_FREQUENCY")
                    || a.RootCauseAnalysis.Contains("failCode=407")))
            .ToListAsync();
        foreach (var alert in staleRateLimitAlerts)
        {
            alert.ResolvedAt = DateTime.UtcNow;
            alert.AcknowledgedAt ??= DateTime.UtcNow;
        }
        if (staleRateLimitAlerts.Count > 0)
            _logger.LogInformation("Auto-resolved {Count} stale rate-limit-caused offline alert(s)", staleRateLimitAlerts.Count);

        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Detects installations whose cloud API is reachable but whose inverter
    /// has not reported online for 3+ hours during daytime. This catches the
    /// case where the inverter itself is disconnected (WiFi lost, powered off,
    /// DC isolator tripped) even while the cloud portal is up.
    ///
    /// Runs every 2 minutes; uses InverterApiLogs with [DEVICE OFFLINE] tag
    /// to count consecutive offline readings.
    /// </summary>
    private async Task CheckDeviceOfflineDuringDaytimeAsync()
    {
        // Configurable: set Monitoring:DeviceOfflineHoursThreshold in appsettings.json
        // Default 3h matches typical inverter cloud reporting frequency
        var offlineHoursThreshold = _configuration.GetValue("Monitoring:DeviceOfflineHoursThreshold", 3);
        var windowStart = DateTime.UtcNow.AddHours(-offlineHoursThreshold);

        var candidates = await _context.Installations
            .Where(i => i.Status == InstallationStatus.Active && i.IncludesMonitoring)
            .ToListAsync();

        // Look back 7 days to distinguish real outages from "never worked" config errors
        var sanityWindowStart = DateTime.UtcNow.AddDays(-7);

        foreach (var inst in candidates)
        {
            // Skip at night — inverters correctly offline when no sun
            if (IsLikelyNighttime(inst)) continue;

            // Count recent [DEVICE OFFLINE]-tagged successful API calls
            var offlineCount = await _context.InverterApiLogs
                .Where(l => l.InstallationId == inst.Id
                            && l.RequestTime >= windowStart
                            && l.IsSuccess
                            && l.ErrorMessage != null
                            && l.ErrorMessage.Contains("[DEVICE OFFLINE]"))
                .CountAsync();

            // Count total successful API calls in the window
            var totalCount = await _context.InverterApiLogs
                .Where(l => l.InstallationId == inst.Id
                            && l.RequestTime >= windowStart
                            && l.IsSuccess)
                .CountAsync();

            // Need enough data and ALL calls should be device-offline
            if (totalCount < 3) continue; // not enough syncs to be sure
            if (offlineCount < totalCount) continue; // inverter reported at least once — skip

            // ─── Sanity check: has this site EVER been online in the last 7 days? ───
            // If there's no [ONLINE]-tagged log in 7 days, this is NOT a real outage —
            // it's a configuration error (wrong PlantId, bad credentials, etc.).
            // Fire a different alert asking the user to verify config instead of
            // sending someone on-site.
            var everOnlineRecently = await _context.InverterApiLogs
                .AnyAsync(l => l.InstallationId == inst.Id
                               && l.RequestTime >= sanityWindowStart
                               && l.IsSuccess
                               && l.ErrorMessage != null
                               && l.ErrorMessage.Contains("[ONLINE]"));

            // De-dup: only one alert per installation per day for each category
            var sinceToday = DateTime.UtcNow.Date;

            if (!everOnlineRecently)
            {
                var existingConfigAlert = await _context.SystemAlerts
                    .AnyAsync(a => a.InstallationId == inst.Id
                        && a.Title.StartsWith("Configuration Issue")
                        && a.OccurredAt >= sinceToday);
                if (existingConfigAlert) continue;

                _context.SystemAlerts.Add(new SystemAlert
                {
                    InstallationId = inst.Id,
                    AlertType = AlertType.Offline,
                    Severity = AlertSeverity.Medium,
                    Title = $"Configuration Issue: {inst.SiteName}",
                    Message = $"[{inst.InverterBrand}] {inst.SiteName}: " +
                        "Cloud API is reachable but we have NEVER seen this site report as online in the last 7 days. " +
                        "This is most likely a configuration problem — NOT an inverter fault. " +
                        "CHECK THESE FIRST (do not dispatch a technician yet):\n" +
                        "  1. Plant / Station ID is correct (must match exactly as shown in the vendor's app)\n" +
                        "  2. API credentials are for the account that OWNS this station\n" +
                        "  3. Run 'Diagnose' from the Installation page to see available stations on your account",
                    RootCauseAnalysis = $"⚠ LIKELY CONFIGURATION ERROR\n" +
                        $"Sanity check: no [ONLINE] log found in the last 7 days.\n" +
                        $"Observed {offlineCount}/{totalCount} [DEVICE OFFLINE] responses in last {offlineHoursThreshold}h.\n" +
                        $"If this were a real inverter fault, we'd expect to have seen it produce at some point in the past week.\n" +
                        $"Most common cause: PlantId doesn't match any station on the configured Huawei/GoodWe/etc. account.",
                    OccurredAt = DateTime.UtcNow,
                    IsNotified = false
                });

                _logger.LogWarning(
                    "Installation {Id} ({Site}): config-issue alert (no [ONLINE] in 7 days, {Count}/{Total} offline)",
                    inst.Id, inst.SiteName, offlineCount, totalCount);
                continue;
            }

            var existingAlert = await _context.SystemAlerts
                .AnyAsync(a => a.InstallationId == inst.Id
                    && a.Title.StartsWith("Inverter Silent (Daytime)")
                    && a.OccurredAt >= sinceToday);
            if (existingAlert) continue;

            _context.SystemAlerts.Add(new SystemAlert
            {
                InstallationId = inst.Id,
                AlertType = AlertType.Offline,
                Severity = AlertSeverity.High,
                Title = $"Inverter Silent (Daytime): {inst.SiteName}",
                Message = $"[{inst.InverterBrand}] {inst.SiteName}: " +
                          $"Cloud API is reachable, but the inverter has not reported production for {offlineHoursThreshold}+ hours " +
                          "during daytime. " +
                          "THIS IS A REAL PROBLEM — check on-site:\n" +
                          "  • Inverter power (AC breaker tripped?)\n" +
                          "  • DC isolator position\n" +
                          "  • Data-logger (WiFi stick, DTSU) is plugged in and its LEDs are normal\n" +
                          "  • Inverter display for error codes",
                RootCauseAnalysis = $"⚠ DEVICE OFFLINE DURING DAYTIME\n" +
                    $"Observed {offlineCount} consecutive [DEVICE OFFLINE] API responses in the last {offlineHoursThreshold} hours. " +
                    $"This site HAS been online in the past 7 days, so configuration is likely correct. " +
                    $"The cloud API is healthy, but the inverter stopped reporting data — strongly suggests a physical problem at the site.",
                OccurredAt = DateTime.UtcNow,
                IsNotified = false
            });

            _logger.LogWarning(
                "Installation {Id} ({Site}) daytime-silent: {Count}/{Total} offline API responses over {Hours}h",
                inst.Id, inst.SiteName, offlineCount, totalCount, offlineHoursThreshold);
        }

        await _context.SaveChangesAsync();
    }

    private async Task CheckMaintenanceDueAsync()
    {
        // Only raise "cleaning due" alerts for sites we actually clean.
        var installations = await _context.Installations
            .Where(i => i.IncludesCleaning
                        && i.NextCleaningDueDate != null
                        && i.NextCleaningDueDate <= DateTime.UtcNow)
            .ToListAsync();

        foreach (var installation in installations)
        {
            var existingAlert = await _context.SystemAlerts
                .AnyAsync(a => a.InstallationId == installation.Id
                    && a.AlertType == AlertType.MaintenanceDue
                    && a.ResolvedAt == null);

            if (!existingAlert)
            {
                _context.SystemAlerts.Add(new SystemAlert
                {
                    InstallationId = installation.Id,
                    AlertType = AlertType.MaintenanceDue,
                    Severity = AlertSeverity.Medium,
                    Title = $"Panel Cleaning Due: {installation.SiteName}",
                    Message = $"Panel cleaning is due for '{installation.SiteName}'. " +
                              $"Remaining: {installation.RemainingCleanings}/{installation.TotalCleaningCount}",
                    IsNotified = false
                });
            }
        }

        // Auto-resolve cleaning alerts whose site is no longer due (cleaning
        // recorded → NextCleaningDueDate pushed to the future, or the site
        // stopped being a cleaning site).  Without this the alert stayed open
        // forever AND the dedup guard above then blocked the NEXT cycle from
        // ever alerting again.
        var dueIds = installations.Select(i => i.Id).ToHashSet();
        var openCleaning = await _context.SystemAlerts
            .Where(a => a.AlertType == AlertType.MaintenanceDue && a.ResolvedAt == null)
            .ToListAsync();
        foreach (var a in openCleaning)
            if (!dueIds.Contains(a.InstallationId))
                a.ResolvedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
    }

    private async Task CheckWarrantyExpiryAsync()
    {
        // Warranty-expiry reminders are only relevant for sites where we
        // handle warranty service. Customer-managed warranty sites get no
        // reminder from us.
        var threshold = DateTime.UtcNow.AddDays(90);
        var installations = await _context.Installations
            .Where(i => i.IncludesWarrantyService
                && i.WarrantyExpiryDate <= threshold
                && i.WarrantyExpiryDate > DateTime.UtcNow)
            .ToListAsync();

        foreach (var installation in installations)
        {
            var existingAlert = await _context.SystemAlerts
                .AnyAsync(a => a.InstallationId == installation.Id
                    && a.AlertType == AlertType.WarrantyExpiring
                    && a.ResolvedAt == null);

            if (!existingAlert)
            {
                var daysLeft = (installation.WarrantyExpiryDate - DateTime.UtcNow).Days;
                _context.SystemAlerts.Add(new SystemAlert
                {
                    InstallationId = installation.Id,
                    AlertType = AlertType.WarrantyExpiring,
                    Severity = AlertSeverity.Low,
                    Title = $"Warranty Expiring: {installation.SiteName}",
                    Message = $"Warranty expires in {daysLeft} days ({installation.WarrantyExpiryDate:dd/MM/yyyy})",
                    IsNotified = false
                });
            }
        }

        // Auto-resolve warranty alerts no longer in the expiring window
        // (warranty renewed/extended → expiry pushed out, already expired, or
        // warranty service dropped).  Same rationale as cleaning above.
        var warnIds = installations.Select(i => i.Id).ToHashSet();
        var openWarranty = await _context.SystemAlerts
            .Where(a => a.AlertType == AlertType.WarrantyExpiring && a.ResolvedAt == null)
            .ToListAsync();
        foreach (var a in openWarranty)
            if (!warnIds.Contains(a.InstallationId))
                a.ResolvedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
    }

    private async Task SendPendingNotificationsAsync()
    {
        var pendingAlerts = await _context.SystemAlerts
            .Include(a => a.Installation)
                .ThenInclude(i => i.Customer)
            .Include(a => a.Installation)
                .ThenInclude(i => i.Partner)
            .Where(a => !a.IsNotified)
            .ToListAsync();

        if (pendingAlerts.Count == 0) return;

        // Load alert group ID once (not per alert)
        var alertGroupId = await _context.SystemSettings
            .Where(s => s.Key == "Line.AlertGroupId")
            .Select(s => s.Value)
            .FirstOrDefaultAsync();

        // Preload partner users (fixes N+1 query)
        var partnerIds = pendingAlerts
            .Select(a => a.Installation.PartnerId)
            .Where(pid => pid.HasValue)
            .Distinct()
            .ToList();

        var partnerUsersLookup = new Dictionary<int, List<string>>();
        if (partnerIds.Count > 0)
        {
            var partnerUsers = await _context.Users
                .Where(u => u.PartnerId != null && partnerIds.Contains(u.PartnerId.Value)
                    && u.LineUserId != null)
                .Select(u => new { u.PartnerId, u.LineUserId })
                .ToListAsync();

            foreach (var pu in partnerUsers)
            {
                if (!pu.PartnerId.HasValue || pu.LineUserId == null) continue;
                if (!partnerUsersLookup.TryGetValue(pu.PartnerId.Value, out var list))
                    partnerUsersLookup[pu.PartnerId.Value] = list = new();
                list.Add(pu.LineUserId);
            }
        }

        // ── Per-cycle saturation cap ──
        // Inter-message spacing is enforced inside LineMessagingService itself;
        // here we just cap how many we'll fire in one health-monitor pass so a
        // surge of alerts doesn't tie up the cycle for too long. Remaining
        // alerts stay IsNotified=false and pick up next cycle.
        const int maxSendsPerCycle = 200;
        var sentInThisCycle = 0;

        foreach (var alert in pendingAlerts)
        {
            if (sentInThisCycle >= maxSendsPerCycle)
            {
                _logger.LogInformation(
                    "LINE notify cap hit ({Cap}). Remaining {Count} alert(s) deferred to next cycle.",
                    maxSendsPerCycle, pendingAlerts.Count(a => !a.IsNotified));
                break;
            }

            var severity = alert.Severity.ToString();

            // Defensive: Installation should always be present (Included),
            // but guard against bad data to prevent killing the loop
            if (alert.Installation == null)
            {
                _logger.LogWarning("Alert {Id} has no Installation, skipping", alert.Id);
                alert.IsNotified = true;
                continue;
            }

            // ── Enrich alert with brand-specific diagnostics ──
            AlertDiagnostics? diag = null;
            try
            {
                diag = await _diagnosticsEnricher.EnrichAsync(alert, alert.Installation);

                // Append diagnostics to RootCauseAnalysis for dashboard display
                if (diag.HasDiagnostics)
                {
                    var appendix = diag.ToRootCauseAppendix();
                    if (!string.IsNullOrEmpty(appendix))
                    {
                        alert.RootCauseAnalysis = string.IsNullOrEmpty(alert.RootCauseAnalysis)
                            ? appendix
                            : alert.RootCauseAnalysis + "\n\n═══ Diagnostics ═══\n" + appendix;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Diagnostics enrichment failed for alert {Id}", alert.Id);
            }

            int attempted = 0;
            int succeeded = 0;
            // Track every LINE id we reach via the built-in routing so the
            // operator-configured NotificationRoute fan-out below can skip
            // them (no double messages).
            var reachedLineIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            async Task SendOneAsync(string toLineId, string label)
            {
                if (string.IsNullOrWhiteSpace(toLineId)) return;
                if (sentInThisCycle >= maxSendsPerCycle) return;
                reachedLineIds.Add(toLineId.Trim());
                attempted++;
                try
                {
                    if (diag != null && diag.HasDiagnostics)
                    {
                        await _lineService.SendDiagnosticAlertAsync(
                            toLineId, alert.Installation!.SiteName,
                            alert.Title, alert.Message, severity,
                            alert.RootCauseAnalysis, diag.PredictedCause,
                            diag.CheckSteps.ToArray(), diag.FixSteps.ToArray(),
                            diag.KnownCaseRef, diag.SafetyWarning,
                            diag.ContextNotes.ToArray());
                    }
                    else
                    {
                        await _lineService.SendFlexAlertAsync(
                            toLineId, alert.Installation!.SiteName,
                            alert.Title, alert.Message,
                            severity, alert.RootCauseAnalysis);
                    }
                    succeeded++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed LINE {Label} notify for alert {Id}", label, alert.Id);
                }
                sentInThisCycle++;
            }

            // 1. Send to Alert Group (technical message — for operator staff)
            await SendOneAsync(alertGroupId ?? "", "group");

            // 2. Notify customer via LINE.  Uses installation.LineGroupId if
            //    configured (per-installation routing), else falls back to
            //    Customer.LineUserId.  Customer-facing message is plain Thai
            //    (see CustomerAlertMessageBuilder), and we only send the
            //    alert types that the operator enabled for this site
            //    (NotifyOnGridLoss / NotifyOnLowProduction / ...).
            var customerLineId = SolarApp.Services.LineApi.CustomerAlertMessageBuilder
                .PickRecipient(alert.Installation);
            var customerNotifyAllowed = SolarApp.Services.LineApi.CustomerAlertMessageBuilder
                .ShouldNotifyCustomer(alert.Installation, alert.AlertType);
            var inQuietHours = SolarApp.Services.LineApi.CustomerAlertMessageBuilder
                .IsInCustomerQuietHours(alert.Installation, DateTime.UtcNow, alert.Severity);
            if (!string.IsNullOrWhiteSpace(customerLineId) && customerNotifyAllowed && !inQuietHours
                && sentInThisCycle < maxSendsPerCycle)
            {
                reachedLineIds.Add(customerLineId.Trim());
                attempted++;
                try
                {
                    var customerMsg = SolarApp.Services.LineApi.CustomerAlertMessageBuilder
                        .Build(alert, alert.Installation);
                    await _lineService.SendMessageAsync(customerLineId, customerMsg);
                    succeeded++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed LINE customer notify for alert {Id}", alert.Id);
                }
                sentInThisCycle++;
            }

            // 3. Notify partner users via LINE (from preloaded dict, no N+1)
            if (alert.Installation.PartnerId.HasValue
                && partnerUsersLookup.TryGetValue(alert.Installation.PartnerId.Value, out var partnerLineIds))
            {
                foreach (var lineId in partnerLineIds)
                {
                    await SendOneAsync(lineId, "partner");
                }
            }

            // 4. Operator-configured extra recipients (NotificationRoute).
            //    Fans out the SAME alert to any global / per-site routes the
            //    operator set up for this event category, skipping anyone
            //    already reached above.  Plain-text technical message so it
            //    works for both groups and personal user ids.
            try
            {
                var evt = _routes.FromAlertType(alert.AlertType, alert.Title);
                var routeMsg =
                    $"[{severity}] {alert.Installation.SiteName}\n{alert.Title}\n{alert.Message}";
                var routeSent = await _routes.SendEventAsync(
                    evt, alert.InstallationId, alert.Title, routeMsg, reachedLineIds);
                if (routeSent > 0)
                {
                    succeeded += routeSent;
                    sentInThisCycle += routeSent;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NotificationRoute fan-out failed for alert {Id}", alert.Id);
            }

            // Only mark as notified if at least one recipient was successfully reached
            // OR no recipients were configured at all (nobody to notify).
            // If we ATTEMPTED sends and ALL FAILED, leave IsNotified=false so next cycle retries.
            if (attempted == 0 || succeeded > 0)
            {
                alert.IsNotified = true;
            }
            else
            {
                _logger.LogWarning(
                    "Alert {Id} ({Title}): all {Count} LINE recipients failed — will retry next cycle",
                    alert.Id, alert.Title, attempted);
            }
        }

        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Sends LINE recovery notifications for alerts that were auto-resolved
    /// since the last check but haven't been notified about recovery yet.
    /// </summary>
    private async Task SendRecoveryNotificationsAsync()
    {
        // Widened from the original 10-minute window to 24 hours because
        // SolarDataSyncJob can resolve an alert at any sync (every 5-15
        // min depending on brand) but this monitor job runs on its own
        // schedule.  A 10-min window meant any resolution that happened
        // just after a monitor pass was permanently silenced — customers
        // saw "system down" alerts that never got an "✅ ระบบกลับมาแล้ว"
        // closure even though our DB knew the alert was resolved.
        //
        // De-dup is already handled below via NotificationLogs lookup —
        // we never re-fire a recovery for the same alert twice, so
        // widening the window costs nothing.
        var recentWindow = DateTime.UtcNow.AddHours(-24);
        var resolvedAlerts = await _context.SystemAlerts
            .Include(a => a.Installation)
                .ThenInclude(i => i.Customer)
            .Include(a => a.Installation)
                .ThenInclude(i => i.Partner)
            .Where(a => a.ResolvedAt != null
                && a.ResolvedAt >= recentWindow
                && a.IsNotified
                && (a.AlertType == AlertType.Offline || a.AlertType == AlertType.Error))
            .ToListAsync();

        if (resolvedAlerts.Count == 0) return;

        // Check if we already sent a recovery notification for these (de-dup)
        var alertIds = resolvedAlerts.Select(a => a.Id).ToList();
        var alreadyNotifiedRecovery = await _context.NotificationLogs
            .Where(n => n.RelatedEntityType == "SystemAlert"
                && n.Title != null && n.Title.StartsWith("[RECOVERED]")
                && n.CreatedAt >= recentWindow)
            .Select(n => n.Title)
            .ToListAsync();

        var alertGroupId = await _context.SystemSettings
            .Where(s => s.Key == "Line.AlertGroupId")
            .Select(s => s.Value)
            .FirstOrDefaultAsync();

        var partnerIds = resolvedAlerts
            .Select(a => a.Installation.PartnerId)
            .Where(pid => pid.HasValue)
            .Distinct()
            .ToList();

        var partnerUsersLookup = new Dictionary<int, List<string>>();
        if (partnerIds.Count > 0)
        {
            var partnerUsers = await _context.Users
                .Where(u => u.PartnerId != null && partnerIds.Contains(u.PartnerId.Value) && u.LineUserId != null)
                .Select(u => new { u.PartnerId, u.LineUserId })
                .ToListAsync();
            foreach (var pu in partnerUsers)
            {
                if (!pu.PartnerId.HasValue || pu.LineUserId == null) continue;
                if (!partnerUsersLookup.TryGetValue(pu.PartnerId.Value, out var list))
                    partnerUsersLookup[pu.PartnerId.Value] = list = new();
                list.Add(pu.LineUserId);
            }
        }

        // ── Pre-fetch the "actual cause" alarms per installation ──
        // For each resolved alert we want to tell the customer WHAT actually
        // went wrong (e.g. "Communication interrupted" / "Grid voltage abnormal")
        // rather than the generic "system was down".  Those signals come from
        // per-alarm SystemAlert rows that the sync job created from
        // GetAlarmsAsync (Huawei getAlarmList / Solis stationAlarm / …) during
        // the outage window.  Pull them all in one query keyed by installation
        // so the inner loop has zero extra round-trips.
        var installationIds = resolvedAlerts.Select(a => a.InstallationId).Distinct().ToList();
        var earliestOccurred = resolvedAlerts.Min(a => a.OccurredAt);
        var siblingAlarms = await _context.SystemAlerts
            .Where(x => installationIds.Contains(x.InstallationId)
                && x.AlertType == AlertType.Error
                && x.OccurredAt >= earliestOccurred
                && !x.Title.StartsWith("Station Faulty:")        // exclude our own generic alert
                && !x.Title.StartsWith("Station Abnormal:"))     // (same, post-rename)
            .Select(x => new { x.InstallationId, x.Title, x.OccurredAt, x.ResolvedAt })
            .ToListAsync();

        int sent = 0;
        foreach (var alert in resolvedAlerts)
        {
            if (alert.Installation == null) continue;

            var recoveryTitle = $"[RECOVERED] {alert.Title}";
            if (alreadyNotifiedRecovery.Any(t => t == recoveryTitle)) continue;

            var downtime = alert.ResolvedAt!.Value - alert.OccurredAt;
            var downtimeStr = downtime.TotalHours >= 1
                ? $"{downtime.TotalHours:0.0} ชั่วโมง"
                : $"{downtime.TotalMinutes:0} นาที";

            // Causes: per-alarm rows that overlapped this alert's downtime
            // window — those are the actual vendor alarm names, not our
            // guesses.  Take up to 5 distinct titles to keep the message
            // short.
            var causes = siblingAlarms
                .Where(s => s.InstallationId == alert.InstallationId
                    && s.OccurredAt <= alert.ResolvedAt!.Value
                    && (s.OccurredAt >= alert.OccurredAt
                        || (s.ResolvedAt == null || s.ResolvedAt >= alert.OccurredAt)))
                .Select(s => s.Title)
                .Distinct()
                .Take(5)
                .ToList();

            // Technical recovery message — for the operator alert group and
            // partner staff.  Uses the existing flex-card formatter so the
            // dashboard summary stays consistent with what it always was.
            var techMsg = $"{alert.Installation.SiteName} กลับมาทำงานปกติแล้ว หลังจาก offline {downtimeStr}";
            if (causes.Count > 0)
                techMsg += "\nสาเหตุที่พบ: " + string.Join(", ", causes);

            async Task SendTechRecoveryToAsync(string lineId)
            {
                if (string.IsNullOrWhiteSpace(lineId) || sent >= 100) return;
                try
                {
                    await _lineService.SendRecoveryNotificationAsync(
                        lineId, alert.Installation.SiteName, alert.Title, techMsg, downtime);
                    sent++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed recovery LINE to {Id} for alert {AlertId}", lineId, alert.Id);
                }
            }

            // 1. Operator alert group (technical)
            await SendTechRecoveryToAsync(alertGroupId ?? "");

            // 2. Customer — plain-Thai recovery message that names the actual
            //    cause when we found one.  Only sent if the customer would have
            //    received the original alert (same gating as the down-message)
            //    so we don't surprise customers who opted out.
            var customerLineId = SolarApp.Services.LineApi.CustomerAlertMessageBuilder
                .PickRecipient(alert.Installation);
            // Recovery messages also honour the customer toggle (so opting
            // out of "down" alerts opts out of recovery too) AND the new
            // NotifyOnRecovery flag (some customers want down-alerts but
            // not closure messages).  Quiet hours never block recovery
            // messages — the "all clear" is short and welcome.
            var customerNotifyAllowed = SolarApp.Services.LineApi.CustomerAlertMessageBuilder
                .ShouldNotifyCustomer(alert.Installation, alert.AlertType)
                && alert.Installation.NotifyOnRecovery;
            if (!string.IsNullOrWhiteSpace(customerLineId) && customerNotifyAllowed && sent < 100)
            {
                try
                {
                    var customerMsg = SolarApp.Services.LineApi.CustomerAlertMessageBuilder
                        .BuildRecovery(alert, alert.Installation, causes);
                    await _lineService.SendMessageAsync(customerLineId, customerMsg);
                    sent++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed customer recovery LINE for alert {AlertId}", alert.Id);
                }
            }

            // 3. Partner staff (technical, same as alert group)
            if (alert.Installation.PartnerId.HasValue
                && partnerUsersLookup.TryGetValue(alert.Installation.PartnerId.Value, out var pIds))
            {
                foreach (var lineId in pIds) await SendTechRecoveryToAsync(lineId);
            }

            // 4. Operator-configured recovery routes — anyone the operator
            //    wired to the Recovery event for this site (or globally).
            //    Exclude the alert group + customer + partner ids already
            //    reached so they don't get a duplicate "all clear".
            try
            {
                var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(alertGroupId)) exclude.Add(alertGroupId.Trim());
                if (!string.IsNullOrWhiteSpace(customerLineId)) exclude.Add(customerLineId!.Trim());
                if (alert.Installation.PartnerId.HasValue
                    && partnerUsersLookup.TryGetValue(alert.Installation.PartnerId.Value, out var pIds2))
                    foreach (var lid in pIds2) exclude.Add(lid.Trim());

                var routeSent = await _routes.SendEventAsync(
                    Models.Enums.NotificationEventType.Recovery,
                    alert.InstallationId,
                    $"[RECOVERED] {alert.Title}",
                    $"✅ {techMsg}",
                    exclude);
                sent += routeSent;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Recovery NotificationRoute fan-out failed for alert {Id}", alert.Id);
            }
        }

        if (sent > 0)
            _logger.LogInformation("Sent {Count} recovery notification(s) for {Alerts} resolved alert(s)",
                sent, resolvedAlerts.Count);
    }

    /// <summary>
    /// Re-notifies for Critical/High alerts that have been open too long without resolution.
    /// Escalation levels:
    ///   L1: 2 hours  (Critical) / 6 hours  (High) — re-notify same recipients
    ///   L2: 6 hours  (Critical) / 24 hours (High) — re-notify with escalation tag
    ///   L3: 24 hours (Critical) / 72 hours (High) — re-notify with urgent tag
    /// </summary>
    private async Task EscalateUnresolvedAlertsAsync()
    {
        var unresolvedAlerts = await _context.SystemAlerts
            .Include(a => a.Installation)
                .ThenInclude(i => i.Customer)
            .Include(a => a.Installation)
                .ThenInclude(i => i.Partner)
            .Where(a => a.ResolvedAt == null
                && a.IsNotified
                && (a.Severity == AlertSeverity.Critical || a.Severity == AlertSeverity.High))
            .ToListAsync();

        if (unresolvedAlerts.Count == 0) return;

        var alertGroupId = await _context.SystemSettings
            .Where(s => s.Key == "Line.AlertGroupId")
            .Select(s => s.Value)
            .FirstOrDefaultAsync();

        int escalated = 0;

        foreach (var alert in unresolvedAlerts)
        {
            if (alert.Installation == null) continue;

            var openDuration = DateTime.UtcNow - alert.OccurredAt;

            // Escalation thresholds configurable via Monitoring:Escalation:{Critical|High}:LevelN
            // Defaults preserve original behavior
            int level;
            TimeSpan escalationInterval;
            if (alert.Severity == AlertSeverity.Critical)
            {
                var l3 = _configuration.GetValue("Monitoring:Escalation:Critical:Level3Hours", 24);
                var l2 = _configuration.GetValue("Monitoring:Escalation:Critical:Level2Hours", 6);
                var l1 = _configuration.GetValue("Monitoring:Escalation:Critical:Level1Hours", 2);
                if (openDuration.TotalHours >= l3) { level = 3; escalationInterval = TimeSpan.FromHours(12); }
                else if (openDuration.TotalHours >= l2) { level = 2; escalationInterval = TimeSpan.FromHours(6); }
                else if (openDuration.TotalHours >= l1) { level = 1; escalationInterval = TimeSpan.FromHours(2); }
                else continue;
            }
            else // High
            {
                var l3 = _configuration.GetValue("Monitoring:Escalation:High:Level3Hours", 72);
                var l2 = _configuration.GetValue("Monitoring:Escalation:High:Level2Hours", 24);
                var l1 = _configuration.GetValue("Monitoring:Escalation:High:Level1Hours", 6);
                if (openDuration.TotalHours >= l3) { level = 3; escalationInterval = TimeSpan.FromHours(24); }
                else if (openDuration.TotalHours >= l2) { level = 2; escalationInterval = TimeSpan.FromHours(12); }
                else if (openDuration.TotalHours >= l1) { level = 1; escalationInterval = TimeSpan.FromHours(6); }
                else continue;
            }

            // Check last escalation time — don't spam faster than the interval
            var escalationPrefix = $"[ESCALATION L";
            var lastEscalation = await _context.NotificationLogs
                .Where(n => n.RelatedEntityType == "SystemAlert"
                    && n.Title != null && n.Title.StartsWith(escalationPrefix)
                    && n.Title.Contains(alert.Title)
                    && n.Status == NotificationStatus.Sent)
                .OrderByDescending(n => n.SentAt)
                .Select(n => n.SentAt)
                .FirstOrDefaultAsync();

            if (lastEscalation.HasValue && DateTime.UtcNow - lastEscalation.Value < escalationInterval)
                continue;

            // Send escalation
            if (!string.IsNullOrWhiteSpace(alertGroupId))
            {
                try
                {
                    await _lineService.SendEscalationAsync(
                        alertGroupId, alert.Installation.SiteName, alert.Title,
                        alert.Message, alert.Severity.ToString(), openDuration, level);
                    escalated++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed escalation L{Level} for alert {Id}", level, alert.Id);
                }
            }
        }

        if (escalated > 0)
            _logger.LogInformation("Escalated {Count} unresolved alert(s)", escalated);
    }

    /// <summary>
    /// Approximates whether it is currently night at the installation's location.
    /// Uses GPS (lat/lng) if present via a NOAA-style sunrise/sunset approximation;
    /// otherwise falls back to local-time 19:00-06:00 (Thailand default TZ).
    /// Solar inverters stop reporting when there is no DC, so night-time "offline"
    /// is normal and must NOT page the operator at 3 AM.
    /// </summary>
    private static bool IsLikelyNighttime(Installation inst)
    {
        var nowUtc = DateTime.UtcNow;

        if (inst.Latitude.HasValue && inst.Longitude.HasValue
            && (Math.Abs(inst.Latitude.Value) > 0.0001 || Math.Abs(inst.Longitude.Value) > 0.0001))
        {
            try
            {
                var (sunriseUtc, sunsetUtc) = ApproximateSunriseSunsetUtc(
                    nowUtc.Date, inst.Latitude.Value, inst.Longitude.Value);
                // 30-min buffer before sunrise / after sunset — inverters stop producing
                // in low-light conditions before the exact astronomical sunset
                return nowUtc < sunriseUtc.AddMinutes(30)
                       || nowUtc > sunsetUtc.AddMinutes(-30);
            }
            catch { /* fall through to time-based heuristic */ }
        }

        // Fallback: Thailand UTC+7 (most installations). 19:00-06:00 local = 12:00-23:00 UTC
        var thHour = (nowUtc.Hour + 7) % 24;
        return thHour >= 19 || thHour < 6;
    }

    /// <summary>
    /// NOAA Solar Calculator simplified — returns UTC sunrise/sunset for given date
    /// and lat/lng. Accurate within ~5 minutes for most locations.
    /// </summary>
    private static (DateTime Sunrise, DateTime Sunset) ApproximateSunriseSunsetUtc(
        DateTime dateUtc, double lat, double lng)
    {
        var dayOfYear = dateUtc.DayOfYear;
        // Solar declination (radians)
        var gamma = 2.0 * Math.PI / 365.0 * (dayOfYear - 1);
        var declination = 0.006918
            - 0.399912 * Math.Cos(gamma)
            + 0.070257 * Math.Sin(gamma)
            - 0.006758 * Math.Cos(2 * gamma)
            + 0.000907 * Math.Sin(2 * gamma)
            - 0.002697 * Math.Cos(3 * gamma)
            + 0.001480 * Math.Sin(3 * gamma);

        var latRad = lat * Math.PI / 180.0;
        var cosH = (Math.Cos(90.833 * Math.PI / 180.0)
                    - Math.Sin(latRad) * Math.Sin(declination))
                   / (Math.Cos(latRad) * Math.Cos(declination));

        // Polar day/night edge cases — treat as daytime for safety
        if (cosH < -1 || cosH > 1)
            return (dateUtc.AddHours(6), dateUtc.AddHours(18));

        var hourAngle = Math.Acos(cosH) * 180.0 / Math.PI;
        var solarNoonUtc = 12.0 - lng / 15.0;
        var sunriseUtcHours = solarNoonUtc - hourAngle / 15.0;
        var sunsetUtcHours = solarNoonUtc + hourAngle / 15.0;

        return (dateUtc.AddHours(sunriseUtcHours), dateUtc.AddHours(sunsetUtcHours));
    }

    private static string AnalyzeRootCause(List<InverterApiLog> recentLogs)
    {
        if (!recentLogs.Any())
            return "No recent API call logs found.\n" +
                   "POSSIBLE CAUSE: Sync job hasn't run yet, or installation was just added.\n" +
                   "ACTION: Check Hangfire dashboard to confirm sync is scheduled, then wait for next sync cycle.";

        var errors = recentLogs
            .Where(l => !string.IsNullOrEmpty(l.ErrorMessage))
            .Select(l => l.ErrorMessage!)
            .ToList();

        var latestError = errors.FirstOrDefault() ?? "No error message";
        var latestTime = recentLogs.First().RequestTime;

        // ─── Check for [DEVICE OFFLINE] tag (set by SolarDataSyncJob) ───
        if (errors.Any(e => e.Contains("[DEVICE OFFLINE]")))
            return "⚠ DEVICE OFFLINE (not an API issue)\n" +
                   "The cloud API is working correctly, but the inverter itself reports offline.\n" +
                   "POSSIBLE CAUSES:\n" +
                   "  • Nighttime / no sunlight (normal)\n" +
                   "  • Inverter lost WiFi/LAN connection to its cloud\n" +
                   "  • Inverter is powered off or in error state\n" +
                   "  • Data logger (e.g. DTSU/WiFi stick) disconnected\n" +
                   "ACTION: If daytime, physically check the inverter and its network connection.\n" +
                   $"Last contact: {latestTime:dd/MM/yyyy HH:mm:ss}";

        // ─── Check for [API FAILURE] tag ───
        if (errors.Any(e => e.Contains("[API FAILURE]")))
            return "✕ API CONNECTION FAILED (not an inverter issue)\n" +
                   "SolarApp cannot reach the cloud API. The inverter might be working fine.\n" +
                   "POSSIBLE CAUSES:\n" +
                   "  • SolarApp server has no internet\n" +
                   "  • Cloud vendor API is down (check vendor status page)\n" +
                   "  • API credentials expired or revoked\n" +
                   "  • Rate limiting (too many API calls)\n" +
                   $"ACTION: Check SolarApp server internet, then run Diagnostics.\n" +
                   $"Latest error: {latestError}\n" +
                   $"Failed at: {latestTime:dd/MM/yyyy HH:mm:ss}";

        // ─── Specific error pattern matching ───
        if (errors.Any(e => e.Contains("[TIMEOUT]")))
            return "✕ API TIMEOUT\n" +
                   "Cloud server did not respond within the time limit.\n" +
                   "POSSIBLE CAUSES:\n" +
                   "  • Cloud vendor experiencing high load\n" +
                   "  • SolarApp server internet is slow\n" +
                   "  • DNS resolution failure\n" +
                   $"ACTION: Wait 15 min and retry. If persistent, check SolarApp server network.\n" +
                   $"Latest: {latestError}";

        if (errors.Any(e => e.Contains("[NETWORK ERROR]")))
            return "✕ NETWORK ERROR\n" +
                   "Cannot establish connection to cloud server.\n" +
                   "POSSIBLE CAUSES:\n" +
                   "  • SolarApp server has no internet\n" +
                   "  • DNS failure\n" +
                   "  • Firewall blocking outbound HTTPS\n" +
                   $"ACTION: Check SolarApp server connectivity. Try: ping the cloud domain.\n" +
                   $"Latest: {latestError}";

        if (errors.Any(e => e.Contains("401") || e.Contains("403") || e.Contains("credential", StringComparison.OrdinalIgnoreCase)
                            || e.Contains("auth", StringComparison.OrdinalIgnoreCase)))
            return "✕ AUTHENTICATION FAILED\n" +
                   "Cloud API rejected the credentials.\n" +
                   "POSSIBLE CAUSES:\n" +
                   "  • API key/token expired (some expire every 3–6 months)\n" +
                   "  • Password changed on cloud portal\n" +
                   "  • Account deactivated by vendor\n" +
                   $"ACTION: Re-enter API credentials in Installation settings.\n" +
                   $"Latest: {latestError}";

        if (errors.Any(e => e.Contains("429") || e.Contains("rate limit", StringComparison.OrdinalIgnoreCase)))
            return "✕ RATE LIMITED\n" +
                   "Too many API calls — cloud vendor blocked requests.\n" +
                   "ACTION: Increase sync interval in Admin > Sync Schedule (recommend 15+ minutes).\n" +
                   $"Latest: {latestError}";

        if (errors.Any(e => e.Contains("500") || e.Contains("server error", StringComparison.OrdinalIgnoreCase)))
            return "✕ CLOUD SERVER ERROR\n" +
                   "The cloud platform returned an internal error (5xx).\n" +
                   "This is the vendor's issue, not SolarApp or the inverter.\n" +
                   "ACTION: Wait and retry. Check vendor's status page for outage announcements.\n" +
                   $"Latest: {latestError}";

        // ─── Generic "no data" — most commonly means device offline ───
        // e.g. GoodWe / Solis returns empty response when inverter hasn't reported
        if (errors.Any(e => e.Contains("No data", StringComparison.OrdinalIgnoreCase)
                            || e.Contains("returned no data", StringComparison.OrdinalIgnoreCase)
                            || e.Contains("empty response", StringComparison.OrdinalIgnoreCase)))
            return "⚠ DEVICE OFFLINE (likely)\n" +
                   "The cloud API responded successfully but returned no inverter data.\n" +
                   "This usually means the inverter itself is offline.\n" +
                   "POSSIBLE CAUSES:\n" +
                   "  • Nighttime / no solar production (normal)\n" +
                   "  • Inverter lost WiFi/LAN connection to its cloud\n" +
                   "  • Inverter / data logger powered off\n" +
                   "ACTION: If daytime, check inverter WiFi + DC isolator.\n" +
                   $"Latest error: {latestError}\n" +
                   $"Failed at: {latestTime:dd/MM/yyyy HH:mm:ss}";

        // ─── Rate-limited specific: Huawei failCode=407 ───
        if (errors.Any(e => e.Contains("failCode=407") || e.Contains("Rate limit")))
            return "✕ RATE LIMITED (Huawei 1 req/min)\n" +
                   "The cloud API is blocking requests due to too many calls.\n" +
                   "ACTION: Increase sync interval to at least 5 minutes in Admin > Sync Schedule.\n" +
                   $"Latest: {latestError}";

        // ─── PlantId not found: e.g. Huawei failCode=20002 ───
        if (errors.Any(e => e.Contains("failCode=20002") || e.Contains("PlantNotFound")
                            || e.Contains("not found", StringComparison.OrdinalIgnoreCase)))
            return "✕ PLANT / STATION NOT FOUND\n" +
                   "The configured Plant ID / Station Code does not exist on this cloud account.\n" +
                   "POSSIBLE CAUSES:\n" +
                   "  • Typo in the Plant ID\n" +
                   "  • Site was transferred to a different account\n" +
                   "  • Wrong credentials (different account owner)\n" +
                   $"ACTION: Verify Plant ID in vendor portal, then update it in Installation settings.\n" +
                   $"Latest: {latestError}";

        return $"✕ UNKNOWN ISSUE\n" +
               $"Failed {recentLogs.Count} recent API call(s).\n" +
               $"Latest error: {latestError}\n" +
               $"Failed at: {latestTime:dd/MM/yyyy HH:mm:ss}\n" +
               "ACTION: Run Diagnostics from the installation detail page for a full connection check.";
    }
}
