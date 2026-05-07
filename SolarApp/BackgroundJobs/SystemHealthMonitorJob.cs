using Hangfire;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.AlertRules;
using SolarApp.Services.LineApi;

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

    public SystemHealthMonitorJob(
        ApplicationDbContext context,
        ILineMessagingService lineService,
        ILogger<SystemHealthMonitorJob> logger,
        IConfiguration configuration,
        IAlertRulesEngine rulesEngine)
    {
        _context = context;
        _lineService = lineService;
        _logger = logger;
        _configuration = configuration;
        _rulesEngine = rulesEngine;
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
    }

    private async Task CheckSystemHealthAsync()
    {
        var offlineThreshold = _configuration.GetValue("InverterApis:OfflineThresholdMinutes", 15);
        var threshold = DateTime.UtcNow.AddMinutes(-offlineThreshold);
        var minFailedCalls = _configuration.GetValue("InverterApis:MinFailedCallsForAlert", 2);

        // ─── Part A: API connection lost (LastOnlineAt is our "last successful
        // cloud contact" marker; if stale, the cloud is unreachable) ───
        var offlineInstallations = await _context.Installations
            .Where(i => i.Status == InstallationStatus.Active
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
        const int OfflineHoursThreshold = 3;
        var windowStart = DateTime.UtcNow.AddHours(-OfflineHoursThreshold);

        var candidates = await _context.Installations
            .Where(i => i.Status == InstallationStatus.Active)
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
                        $"Observed {offlineCount}/{totalCount} [DEVICE OFFLINE] responses in last {OfflineHoursThreshold}h.\n" +
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
                          $"Cloud API is reachable, but the inverter has not reported production for {OfflineHoursThreshold}+ hours " +
                          "during daytime. " +
                          "THIS IS A REAL PROBLEM — check on-site:\n" +
                          "  • Inverter power (AC breaker tripped?)\n" +
                          "  • DC isolator position\n" +
                          "  • Data-logger (WiFi stick, DTSU) is plugged in and its LEDs are normal\n" +
                          "  • Inverter display for error codes",
                RootCauseAnalysis = $"⚠ DEVICE OFFLINE DURING DAYTIME\n" +
                    $"Observed {offlineCount} consecutive [DEVICE OFFLINE] API responses in the last {OfflineHoursThreshold} hours. " +
                    $"This site HAS been online in the past 7 days, so configuration is likely correct. " +
                    $"The cloud API is healthy, but the inverter stopped reporting data — strongly suggests a physical problem at the site.",
                OccurredAt = DateTime.UtcNow,
                IsNotified = false
            });

            _logger.LogWarning(
                "Installation {Id} ({Site}) daytime-silent: {Count}/{Total} offline API responses over {Hours}h",
                inst.Id, inst.SiteName, offlineCount, totalCount, OfflineHoursThreshold);
        }

        await _context.SaveChangesAsync();
    }

    private async Task CheckMaintenanceDueAsync()
    {
        var installations = await _context.Installations
            .Where(i => i.NextCleaningDueDate != null && i.NextCleaningDueDate <= DateTime.UtcNow)
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

        await _context.SaveChangesAsync();
    }

    private async Task CheckWarrantyExpiryAsync()
    {
        var threshold = DateTime.UtcNow.AddDays(90);
        var installations = await _context.Installations
            .Where(i => i.WarrantyExpiryDate <= threshold
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

        foreach (var alert in pendingAlerts)
        {
            var severity = alert.Severity.ToString();

            // Defensive: Installation should always be present (Included),
            // but guard against bad data to prevent killing the loop
            if (alert.Installation == null)
            {
                _logger.LogWarning("Alert {Id} has no Installation, skipping", alert.Id);
                alert.IsNotified = true;
                continue;
            }

            // 1. Send to Alert Group (if configured)
            if (!string.IsNullOrWhiteSpace(alertGroupId))
            {
                try
                {
                    await _lineService.SendFlexAlertAsync(
                        alertGroupId,
                        alert.Installation.SiteName,
                        alert.Title, alert.Message,
                        severity, alert.RootCauseAnalysis);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed LINE group notify for alert {Id}", alert.Id);
                }
            }

            // 2. Notify customer via LINE (Customer + LineUserId must exist)
            var customerLineId = alert.Installation.Customer?.LineUserId;
            if (!string.IsNullOrEmpty(customerLineId))
            {
                try
                {
                    await _lineService.SendFlexAlertAsync(
                        customerLineId,
                        alert.Installation.SiteName,
                        alert.Title, alert.Message,
                        severity, alert.RootCauseAnalysis);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed LINE customer notify for alert {Id}", alert.Id);
                }
            }

            // 3. Notify partner users via LINE (from preloaded dict, no N+1)
            if (alert.Installation.PartnerId.HasValue
                && partnerUsersLookup.TryGetValue(alert.Installation.PartnerId.Value, out var partnerLineIds))
            {
                foreach (var lineId in partnerLineIds)
                {
                    try
                    {
                        await _lineService.SendFlexAlertAsync(
                            lineId,
                            alert.Installation.SiteName,
                            alert.Title, alert.Message,
                            severity, alert.RootCauseAnalysis);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed LINE partner notify for alert {Id}", alert.Id);
                    }
                }
            }

            alert.IsNotified = true;
        }

        await _context.SaveChangesAsync();
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
