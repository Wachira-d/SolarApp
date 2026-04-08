using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.LineApi;

namespace SolarApp.BackgroundJobs;

/// <summary>
/// Background job that monitors system health, detects offline systems,
/// and sends LINE notifications for alerts.
/// </summary>
public class SystemHealthMonitorJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SystemHealthMonitorJob> _logger;
    private readonly IConfiguration _configuration;

    public SystemHealthMonitorJob(
        IServiceScopeFactory scopeFactory,
        ILogger<SystemHealthMonitorJob> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = _configuration.GetValue("InverterApis:HealthCheckIntervalMinutes", 5);

        // Wait initial delay to let data sync run first
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckSystemHealthAsync(stoppingToken);
                await CheckMaintenanceDueAsync(stoppingToken);
                await CheckWarrantyExpiryAsync(stoppingToken);
                await SendPendingNotificationsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in system health monitor job");
            }

            await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
        }
    }

    private async Task CheckSystemHealthAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var offlineThreshold = _configuration.GetValue("InverterApis:OfflineThresholdMinutes", 30);
        var threshold = DateTime.UtcNow.AddMinutes(-offlineThreshold);

        // Find installations that haven't reported data within the threshold
        var offlineInstallations = await context.Installations
            .Where(i => i.Status == InstallationStatus.Active
                && i.LastOnlineAt != null
                && i.LastOnlineAt < threshold)
            .ToListAsync(ct);

        foreach (var installation in offlineInstallations)
        {
            installation.Status = InstallationStatus.Offline;

            // Check recent API logs for root cause analysis
            var recentLogs = await context.InverterApiLogs
                .Where(l => l.InstallationId == installation.Id && !l.IsSuccess)
                .OrderByDescending(l => l.RequestTime)
                .Take(5)
                .ToListAsync(ct);

            var rootCause = AnalyzeRootCause(recentLogs);

            // Create alert if not already exists
            var existingAlert = await context.SystemAlerts
                .AnyAsync(a => a.InstallationId == installation.Id
                    && a.AlertType == AlertType.Offline
                    && a.ResolvedAt == null, ct);

            if (!existingAlert)
            {
                context.SystemAlerts.Add(new SystemAlert
                {
                    InstallationId = installation.Id,
                    AlertType = AlertType.Offline,
                    Severity = AlertSeverity.Critical,
                    Title = $"System Offline: {installation.SiteName}",
                    Message = $"Installation '{installation.SiteName}' has been offline since {installation.LastOnlineAt:dd/MM/yyyy HH:mm}",
                    RootCauseAnalysis = rootCause,
                    OccurredAt = DateTime.UtcNow,
                    IsNotified = false
                });

                _logger.LogWarning("Installation {Id} ({Site}) marked as offline",
                    installation.Id, installation.SiteName);
            }
        }

        await context.SaveChangesAsync(ct);
    }

    private async Task CheckMaintenanceDueAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var installations = await context.Installations
            .Where(i => i.NextCleaningDueDate != null && i.NextCleaningDueDate <= DateTime.UtcNow)
            .ToListAsync(ct);

        foreach (var installation in installations)
        {
            var existingAlert = await context.SystemAlerts
                .AnyAsync(a => a.InstallationId == installation.Id
                    && a.AlertType == AlertType.MaintenanceDue
                    && a.ResolvedAt == null, ct);

            if (!existingAlert)
            {
                context.SystemAlerts.Add(new SystemAlert
                {
                    InstallationId = installation.Id,
                    AlertType = AlertType.MaintenanceDue,
                    Severity = AlertSeverity.Medium,
                    Title = $"Panel Cleaning Due: {installation.SiteName}",
                    Message = $"Panel cleaning is due for '{installation.SiteName}'. " +
                              $"Remaining cleanings: {installation.RemainingCleanings}/{installation.TotalCleaningCount}",
                    IsNotified = false
                });
            }
        }

        await context.SaveChangesAsync(ct);
    }

    private async Task CheckWarrantyExpiryAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var threshold = DateTime.UtcNow.AddDays(90);
        var installations = await context.Installations
            .Where(i => i.WarrantyExpiryDate <= threshold
                && i.WarrantyExpiryDate > DateTime.UtcNow)
            .ToListAsync(ct);

        foreach (var installation in installations)
        {
            var existingAlert = await context.SystemAlerts
                .AnyAsync(a => a.InstallationId == installation.Id
                    && a.AlertType == AlertType.WarrantyExpiring
                    && a.ResolvedAt == null, ct);

            if (!existingAlert)
            {
                var daysLeft = (installation.WarrantyExpiryDate - DateTime.UtcNow).Days;
                context.SystemAlerts.Add(new SystemAlert
                {
                    InstallationId = installation.Id,
                    AlertType = AlertType.WarrantyExpiring,
                    Severity = AlertSeverity.Low,
                    Title = $"Warranty Expiring: {installation.SiteName}",
                    Message = $"Warranty for '{installation.SiteName}' expires in {daysLeft} days ({installation.WarrantyExpiryDate:dd/MM/yyyy})",
                    IsNotified = false
                });
            }
        }

        await context.SaveChangesAsync(ct);
    }

    private async Task SendPendingNotificationsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var lineService = scope.ServiceProvider.GetRequiredService<ILineMessagingService>();

        var pendingAlerts = await context.SystemAlerts
            .Include(a => a.Installation)
                .ThenInclude(i => i.Customer)
            .Include(a => a.Installation)
                .ThenInclude(i => i.Partner)
            .Where(a => !a.IsNotified)
            .ToListAsync(ct);

        foreach (var alert in pendingAlerts)
        {
            // Notify customer
            if (!string.IsNullOrEmpty(alert.Installation.Customer.LineUserId))
            {
                await lineService.SendAlertAsync(
                    alert.Installation.Customer.LineUserId,
                    alert.Installation.SiteName,
                    alert.Title,
                    alert.Message,
                    alert.RootCauseAnalysis);
            }

            // Notify partner
            if (alert.Installation.Partner != null
                && !string.IsNullOrEmpty(alert.Installation.Partner.LineUserId))
            {
                await lineService.SendAlertAsync(
                    alert.Installation.Partner.LineUserId,
                    alert.Installation.SiteName,
                    alert.Title,
                    alert.Message,
                    alert.RootCauseAnalysis);
            }

            alert.IsNotified = true;
        }

        await context.SaveChangesAsync(ct);
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

        var consecutiveFailures = recentLogs.Count;
        return $"System has failed {consecutiveFailures} consecutive API calls. " +
               $"Latest error: {errorMessages.FirstOrDefault() ?? "Unknown"}. " +
               "Possible causes: hardware issue, network problem, or cloud API changes.";
    }
}
