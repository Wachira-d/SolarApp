using Hangfire;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models.Enums;
using SolarApp.Services.LineApi;

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

    public DailySummaryJob(
        ApplicationDbContext context,
        ILineMessagingService lineService,
        ILogger<DailySummaryJob> logger)
    {
        _context = context;
        _lineService = lineService;
        _logger = logger;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task ExecuteAsync()
    {
        var todayStart = DateTime.UtcNow.Date;
        var todayEnd = todayStart.AddDays(1);

        var installations = await _context.Installations
            .Where(i => i.Status != InstallationStatus.Inactive)
            .ToListAsync();

        var totalSites = installations.Count;
        var onlineSites = installations.Count(i => i.Status == InstallationStatus.Active);
        var offlineSites = installations.Count(i => i.Status == InstallationStatus.Offline);

        var todayEnergy = await _context.EnergyData
            .Where(r => r.Timestamp >= todayStart && r.Timestamp < todayEnd)
            .SumAsync(r => (decimal?)r.EnergyGenerationKwh) ?? 0;

        var activeAlerts = await _context.SystemAlerts
            .CountAsync(a => a.ResolvedAt == null);

        var newAlertsToday = await _context.SystemAlerts
            .CountAsync(a => a.OccurredAt >= todayStart && a.OccurredAt < todayEnd);

        var resolvedToday = await _context.SystemAlerts
            .CountAsync(a => a.ResolvedAt >= todayStart && a.ResolvedAt < todayEnd);

        var pendingWo = await _context.WorkOrders
            .CountAsync(wo => wo.Status != WorkOrderStatus.Completed && wo.Status != WorkOrderStatus.Cancelled);

        var completedWoToday = await _context.WorkOrders
            .CountAsync(wo => wo.Status == WorkOrderStatus.Completed
                && wo.UpdatedAt >= todayStart && wo.UpdatedAt < todayEnd);

        var criticalSites = await _context.SystemAlerts
            .Include(a => a.Installation)
            .Where(a => a.ResolvedAt == null && a.Severity == AlertSeverity.Critical)
            .Select(a => a.Installation.SiteName)
            .Distinct()
            .Take(10)
            .ToListAsync();

        var recoveredSites = await _context.SystemAlerts
            .Include(a => a.Installation)
            .Where(a => a.ResolvedAt >= todayStart && a.ResolvedAt < todayEnd
                && (a.AlertType == AlertType.Offline || a.AlertType == AlertType.Error))
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
    }
}
