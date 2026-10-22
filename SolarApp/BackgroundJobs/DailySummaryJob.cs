using Hangfire;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.LineApi;
using SolarApp.Services.Notifications;

namespace SolarApp.BackgroundJobs;

/// <summary>
/// Sends a daily summary report via LINE at a configured time (default: 18:00 local / 11:00 UTC).
/// Includes: online/offline sites, energy production, alert count, work order status, recovered sites.
/// </summary>
public class DailySummaryJob
{
    private readonly ApplicationDbContext _context;
    private readonly ILineMessagingService _lineService;
    private readonly ILogger<DailySummaryJob> _logger;
    private readonly INotificationRouteService _routes;

    public DailySummaryJob(
        ApplicationDbContext context,
        ILineMessagingService lineService,
        ILogger<DailySummaryJob> logger,
        INotificationRouteService routes)
    {
        _context = context;
        _lineService = lineService;
        _logger = logger;
        _routes = routes;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task ExecuteAsync()
    {
        // The site's day as a UTC window — alert and work-order timestamps are
        // UTC. UtcNow.Date began "today" at 07:00 Thailand time, so the 18:00
        // digest silently left out everything from midnight to 07:00.
        var todayStart = SolarApp.Services.SiteTime.TodayStartUtc;
        var todayEnd = todayStart.AddDays(1);

        // Only count sites we actually MONITOR.  Cleaning-only / partner-
        // monitored sites have no cloud account on our side, so their
        // online/offline status and energy production are not ours to
        // report — including them in "9/9 sites online" misleads the
        // operator because it lumps in sites we never poll.
        var monitoredInstalls = await _context.Installations
            .Where(i => i.Status != InstallationStatus.Inactive && i.IncludesMonitoring)
            .Select(i => new { i.Id, i.Status, i.SiteName })
            .ToListAsync();

        var monitoredIds = monitoredInstalls.Select(i => i.Id).ToList();
        var totalSites = monitoredInstalls.Count;
        var onlineSites = monitoredInstalls.Count(i => i.Status == InstallationStatus.Active);
        var offlineSites = monitoredInstalls.Count(i => i.Status == InstallationStatus.Offline);

        // Energy "Today" — sum the DAILY-granularity rows only.  The old
        // query had NO Granularity filter, so it also summed every Realtime
        // row (each of which carries the running TodayEnergyKwh accumulator),
        // inflating the total by ~ the number of syncs that day — that was
        // the "101,341 kWh" the operator saw.  Daily rows are keyed by the
        // site-local date (SiteTime.Today), not UtcNow.Date, so match on that
        // to stay correct during the 00:00–07:00 Thailand window.
        var siteToday = SolarApp.Services.SiteTime.Today;
        var todayEnergy = await _context.EnergyData
            .Where(r => r.Granularity == DataGranularity.Daily
                        && r.Timestamp.Date == siteToday
                        && monitoredIds.Contains(r.InstallationId))
            .SumAsync(r => (decimal?)r.EnergyGenerationKwh) ?? 0;

        // Alerts / work orders: same scope.  An alert on a non-monitored
        // site is either operator-entered (rare) or noise — exclude both
        // so the customer-facing summary stays meaningful.
        //
        // Split unresolved alerts into SYSTEM PROBLEMS vs SCHEDULED
        // REMINDERS so the report doesn't report a routine cleaning-due as
        // a plant fault.  MaintenanceDue = cleaning; WarrantyExpiring =
        // warranty; everything else = a real system problem.
        var activeAlerts = await _context.SystemAlerts
            .CountAsync(a => a.ResolvedAt == null && monitoredIds.Contains(a.InstallationId)
                             && a.AlertType != AlertType.MaintenanceDue
                             && a.AlertType != AlertType.WarrantyExpiring);

        // Cleaning-due / warranty reminders span ALL owned sites we service,
        // not just monitored ones — a cleaning-only (unmonitored) site still
        // needs its cleaning reminder counted.  Gate on the broader owned set.
        //
        // Counted from the installations, not from the alerts. Alerts are the
        // notification artefact: one gets raised per due site, but an operator
        // who resolves one by hand, or a health-monitor cycle that hasn't run
        // yet, makes the alert count drift from the number of sites actually
        // waiting — and the daily summary then disagrees with the maintenance
        // page for reasons nobody can see. The due date and the cleaning
        // contract flag are the facts; everything else is derived from them.
        //
        // Same rule as GetInstallationsNeedingCleaningAsync, the dashboard
        // tile, the map pins and CheckMaintenanceDueAsync.
        var cleaningDue = await _context.Installations
            .Where(Installation.CleaningDueAsOf(siteToday))
            .OrderBy(i => i.NextCleaningDueDate)
            .Select(i => i.SiteName)
            .ToListAsync();

        var cleaningDueCount = cleaningDue.Count;
        // Most overdue first, so the ten that fit are the ten that matter.
        var cleaningDueSites = cleaningDue.Take(10).ToList();

        // Counted from the installations for the same reason as cleaning: the
        // alert count drifts from reality whenever one is resolved by hand or
        // the health monitor hasn't run yet. It also can't tell warranty from
        // contract — ContractExpiryJob files under WarrantyExpiring too, so an
        // alert count would report an expiring CONTRACT as an expiring
        // WARRANTY. The dates are the facts.
        var warrantyThreshold = siteToday.AddDays(90);
        var warrantyExpiringCount = await _context.Installations
            .CountAsync(i => i.IncludesWarrantyService
                             && i.WarrantyExpiryDate.Year > 1900
                             && i.WarrantyExpiryDate >= siteToday
                             && i.WarrantyExpiryDate <= warrantyThreshold);

        // "New alerts today" = new SYSTEM PROBLEMS today (not reminders).
        var newAlertsToday = await _context.SystemAlerts
            .CountAsync(a => a.OccurredAt >= todayStart && a.OccurredAt < todayEnd
                             && monitoredIds.Contains(a.InstallationId)
                             && a.AlertType != AlertType.MaintenanceDue
                             && a.AlertType != AlertType.WarrantyExpiring);

        var resolvedToday = await _context.SystemAlerts
            .CountAsync(a => a.ResolvedAt >= todayStart && a.ResolvedAt < todayEnd
                             && monitoredIds.Contains(a.InstallationId)
                             && a.AlertType != AlertType.MaintenanceDue
                             && a.AlertType != AlertType.WarrantyExpiring);

        // Work orders include both monitored and external sites (we still
        // dispatch crews for cleaning at partner-monitored sites), so the
        // WO counts intentionally span the broader installation set —
        // gated only on WorkOrders.InstallationId being one of ours.
        var allOwnedIds = await _context.Installations
            .Where(i => i.Status != InstallationStatus.Inactive)
            .Select(i => i.Id)
            .ToListAsync();
        var pendingWo = await _context.WorkOrders
            // Still to be done: anything before Completed. Verified, Invoiced
            // and Closed are past the work itself and were counted as pending.
            .CountAsync(wo => wo.Status < WorkOrderStatus.Completed
                              && (wo.InstallationId == null || allOwnedIds.Contains(wo.InstallationId.Value)));

        var completedWoToday = await _context.WorkOrders
            .CountAsync(wo => wo.Status >= WorkOrderStatus.Completed
                              && wo.Status != WorkOrderStatus.Cancelled
                              && wo.UpdatedAt >= todayStart && wo.UpdatedAt < todayEnd
                              && (wo.InstallationId == null || allOwnedIds.Contains(wo.InstallationId.Value)));

        var criticalSites = await _context.SystemAlerts
            .Include(a => a.Installation)
            .Where(a => a.ResolvedAt == null && a.Severity == AlertSeverity.Critical
                        && monitoredIds.Contains(a.InstallationId))
            .Select(a => a.Installation.SiteName)
            .Distinct()
            .Take(10)
            .ToListAsync();

        var recoveredSites = await _context.SystemAlerts
            .Include(a => a.Installation)
            .Where(a => a.ResolvedAt >= todayStart && a.ResolvedAt < todayEnd
                && (a.AlertType == AlertType.Offline || a.AlertType == AlertType.Error)
                && monitoredIds.Contains(a.InstallationId))
            .Select(a => a.Installation.SiteName)
            .Distinct()
            .Take(10)
            .ToListAsync();

        var summary = new DailySummaryData
        {
            TotalSites = totalSites,
            OnlineSites = onlineSites,
            OfflineSites = offlineSites,
            TotalEnergyTodayKwh = todayEnergy,
            ActiveAlerts = activeAlerts,
            NewAlertsToday = newAlertsToday,
            ResolvedTodayAlerts = resolvedToday,
            CleaningDueCount = cleaningDueCount,
            WarrantyExpiringCount = warrantyExpiringCount,
            CleaningDueSites = cleaningDueSites,
            PendingWorkOrders = pendingWo,
            CompletedWorkOrdersToday = completedWoToday,
            CriticalSites = criticalSites,
            RecoveredSites = recoveredSites,
        };

        // Send to alert group
        var alertGroupId = await _context.SystemSettings
            .Where(s => s.Key == "Line.AlertGroupId")
            .Select(s => s.Value)
            .FirstOrDefaultAsync();

        if (!string.IsNullOrWhiteSpace(alertGroupId))
        {
            try
            {
                await _lineService.SendDailySummaryAsync(alertGroupId, summary);
                _logger.LogInformation(
                    "Daily summary sent: {Online}/{Total} online, {Alerts} alerts, {Energy:N0} kWh",
                    onlineSites, totalSites, activeAlerts, todayEnergy);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send daily summary");
            }
        }
        else
        {
            _logger.LogWarning("Daily summary skipped — no Line.AlertGroupId configured");
        }

        // Operator-configured DailySummary routes — extra group/user ids
        // that should also get the summary, as a plain-text digest (these
        // recipients may not be in the alert group).  Global-only: a daily
        // summary isn't site-specific, so installationId is null.
        try
        {
            // Append a tappable link to the pending-alerts page when the
            // public base URL is configured (same setting the flex button
            // uses).  Plain text, so it works for personal-user recipients
            // that don't get the rich flex card.
            var baseUrl = await _context.SystemSettings
                .Where(s => s.Key == "App.BaseUrl")
                .Select(s => s.Value)
                .FirstOrDefaultAsync();
            var linkLine = !string.IsNullOrWhiteSpace(baseUrl)
                    && baseUrl.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? $"\n🔔 ดู Alert ค้าง: {baseUrl.Trim().TrimEnd('/')}/Admin/Alerts"
                : "";
            // Cleaning link (Maintenance page) when there are cleaning-due
            // reminders and the base URL is set.
            var normalizedBase = !string.IsNullOrWhiteSpace(baseUrl)
                    && baseUrl.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? baseUrl.Trim().TrimEnd('/')
                : null;
            var cleaningLine = cleaningDueCount > 0
                ? $"\n🧽 ครบ/เลยกำหนดล้างแผง: {cleaningDueCount}"
                  + (normalizedBase != null ? $" → {normalizedBase}/Maintenance" : "")
                : "";
            var warrantyLine = warrantyExpiringCount > 0
                ? $"\n🛡️ ประกันใกล้หมด: {warrantyExpiringCount}"
                : "";
            var digest =
                $"📊 Daily Summary {SolarApp.Services.SiteTime.Today:dd/MM/yyyy}\n" +
                $"Online/Monitored: {onlineSites}/{totalSites} ไซต์\n" +
                $"Offline: {offlineSites}\n" +
                $"Energy Today: {todayEnergy:N1} kWh\n" +
                $"🔴 ปัญหาระบบ (Active Alerts): {activeAlerts} (ใหม่วันนี้ {newAlertsToday})" +
                cleaningLine + warrantyLine + "\n" +
                $"Work Orders: {pendingWo} ค้าง, {completedWoToday} เสร็จวันนี้" +
                linkLine;
            var exclude = string.IsNullOrWhiteSpace(alertGroupId)
                ? null
                : new[] { alertGroupId! };
            var routeSent = await _routes.SendEventAsync(
                NotificationEventType.DailySummary, null, "Daily Summary", digest, exclude);
            if (routeSent > 0)
                _logger.LogInformation("Daily summary fanned out to {Count} extra route(s)", routeSent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Daily summary NotificationRoute fan-out failed");
        }
    }
}
