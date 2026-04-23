using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.LineApi;

namespace SolarApp.BackgroundJobs;

/// <summary>
/// Hangfire recurring job: monitors system health, detects offline systems,
/// checks cleaning/warranty due, sends LINE notifications.
/// Scheduled: every 2 minutes.
/// </summary>
public class SystemHealthMonitorJob
{
    private readonly ApplicationDbContext _context;
    private readonly ILineMessagingService _lineService;
    private readonly ILogger<SystemHealthMonitorJob> _logger;
    private readonly IConfiguration _configuration;

    public SystemHealthMonitorJob(
        ApplicationDbContext context,
        ILineMessagingService lineService,
        ILogger<SystemHealthMonitorJob> logger,
        IConfiguration configuration)
    {
        _context = context;
        _lineService = lineService;
        _logger = logger;
        _configuration = configuration;
    }

    public async Task ExecuteAsync()
    {
        await CheckSystemHealthAsync();
        await CheckMaintenanceDueAsync();
        await CheckWarrantyExpiryAsync();
        await SendPendingNotificationsAsync();
    }

    private async Task CheckSystemHealthAsync()
    {
        var offlineThreshold = _configuration.GetValue("InverterApis:OfflineThresholdMinutes", 15);
        var threshold = DateTime.UtcNow.AddMinutes(-offlineThreshold);

        var offlineInstallations = await _context.Installations
            .Where(i => i.Status == InstallationStatus.Active
                && i.LastOnlineAt != null
                && i.LastOnlineAt < threshold)
            .ToListAsync();

        foreach (var installation in offlineInstallations)
        {
            installation.Status = InstallationStatus.Offline;

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

            if (!existingAlert)
            {
                _context.SystemAlerts.Add(new SystemAlert
                {
                    InstallationId = installation.Id,
                    AlertType = AlertType.Offline,
                    Severity = AlertSeverity.Critical,
                    Title = $"System Offline: {installation.SiteName}",
                    Message = $"Installation '{installation.SiteName}' has been offline since {installation.LastOnlineAt:dd/MM/yyyy HH:mm}",
                    RootCauseAnalysis = rootCause,
                    IsNotified = false
                });

                _logger.LogWarning("Installation {Id} ({Site}) marked as offline",
                    installation.Id, installation.SiteName);
            }
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

        foreach (var alert in pendingAlerts)
        {
            var severity = alert.Severity.ToString();

            // 1. Send to Alert Group (if configured)
            var alertGroupId = await _context.SystemSettings
                .Where(s => s.Key == "Line.AlertGroupId")
                .Select(s => s.Value)
                .FirstOrDefaultAsync();

            if (!string.IsNullOrWhiteSpace(alertGroupId))
            {
                await _lineService.SendFlexAlertAsync(
                    alertGroupId,
                    alert.Installation.SiteName,
                    alert.Title, alert.Message,
                    severity, alert.RootCauseAnalysis);
            }

            // 2. Notify customer via LINE
            if (!string.IsNullOrEmpty(alert.Installation.Customer.LineUserId))
            {
                await _lineService.SendFlexAlertAsync(
                    alert.Installation.Customer.LineUserId,
                    alert.Installation.SiteName,
                    alert.Title, alert.Message,
                    severity, alert.RootCauseAnalysis);
            }

            // 3. Notify partner users via LINE
            if (alert.Installation.Partner != null)
            {
                var partnerUsers = await _context.Users
                    .Where(u => u.PartnerId == alert.Installation.PartnerId
                        && u.LineUserId != null)
                    .ToListAsync();

                foreach (var pu in partnerUsers)
                {
                    await _lineService.SendFlexAlertAsync(
                        pu.LineUserId!,
                        alert.Installation.SiteName,
                        alert.Title, alert.Message,
                        severity, alert.RootCauseAnalysis);
                }
            }

            alert.IsNotified = true;
        }

        await _context.SaveChangesAsync();
    }

    private static string AnalyzeRootCause(List<InverterApiLog> recentLogs)
    {
        if (!recentLogs.Any())
            return "No recent API logs available. Possible causes: internet connection issue, inverter powered off, or cloud service outage.";

        var errorMessages = recentLogs
            .Where(l => !string.IsNullOrEmpty(l.ErrorMessage))
            .Select(l => l.ErrorMessage!)
            .Distinct()
            .ToList();

        if (errorMessages.Any(e => e.Contains("timeout", StringComparison.OrdinalIgnoreCase)))
            return "API request timeouts detected. Possible causes: slow internet connection at site, or cloud service experiencing high load.";

        if (errorMessages.Any(e => e.Contains("401") || e.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)))
            return "Authentication failure. API credentials may have expired or been revoked. Please update the cloud API credentials.";

        if (errorMessages.Any(e => e.Contains("500") || e.Contains("internal server", StringComparison.OrdinalIgnoreCase)))
            return "Cloud service internal error. The inverter cloud platform may be experiencing issues. Please check the vendor's status page.";

        if (errorMessages.Any(e => e.Contains("connection", StringComparison.OrdinalIgnoreCase)))
            return "Connection errors detected. The inverter or its data logger may have lost internet connectivity. Check the site's network.";

        return $"System has failed {recentLogs.Count} consecutive API calls. " +
               $"Latest error: {errorMessages.FirstOrDefault() ?? "Unknown"}. " +
               "Possible causes: hardware issue, network problem, or cloud API changes.";
    }
}
