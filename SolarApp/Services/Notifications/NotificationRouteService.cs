using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.LineApi;

namespace SolarApp.Services.Notifications;

/// <inheritdoc/>
public class NotificationRouteService : INotificationRouteService
{
    private readonly ApplicationDbContext _context;
    private readonly ILineMessagingService _lineService;
    private readonly ILogger<NotificationRouteService> _logger;

    public NotificationRouteService(
        ApplicationDbContext context,
        ILineMessagingService lineService,
        ILogger<NotificationRouteService> logger)
    {
        _context = context;
        _lineService = lineService;
        _logger = logger;
    }

    public async Task<List<string>> ResolveAsync(NotificationEventType eventType, int? installationId)
    {
        try
        {
            // Match rows that target this exact event OR the All wildcard,
            // and are either global (InstallationId null) or scoped to this
            // installation.  Disabled rows never match.
            var query = _context.NotificationRoutes
                .AsNoTracking()
                .Where(r => r.IsEnabled
                    && (r.EventType == eventType || r.EventType == NotificationEventType.All)
                    && (r.InstallationId == null
                        || (installationId != null && r.InstallationId == installationId)));

            var ids = await query
                .Select(r => r.LineId)
                .ToListAsync();

            return ids
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            // Table may not exist yet (first boot before the schema hotfix
            // creates it) — never let routing break the alert pipeline.
            _logger.LogDebug(ex, "NotificationRoute resolve failed for {Event}/{Install} — treating as no routes",
                eventType, installationId);
            return new List<string>();
        }
    }

    public async Task<int> SendEventAsync(
        NotificationEventType eventType,
        int? installationId,
        string title,
        string message,
        IEnumerable<string>? excludeLineIds = null)
    {
        var targets = await ResolveAsync(eventType, installationId);
        if (targets.Count == 0) return 0;

        var exclude = new HashSet<string>(
            excludeLineIds?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
                ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        int sent = 0;
        foreach (var lineId in targets)
        {
            if (exclude.Contains(lineId)) continue; // already reached by built-in flow
            exclude.Add(lineId);                    // also de-dup within this fan-out

            var log = new NotificationLog
            {
                RecipientLineId = lineId,
                Channel = NotificationChannel.Line,
                Title = title,
                Message = message,
                RelatedEntityType = "NotificationRoute",
                RelatedEntityId = installationId?.ToString(),
                Status = NotificationStatus.Pending,
                CreatedAt = DateTime.UtcNow,
            };
            try
            {
                await _lineService.SendMessageAsync(lineId, message);
                log.Status = NotificationStatus.Sent;
                log.SentAt = DateTime.UtcNow;
                sent++;
            }
            catch (Exception ex)
            {
                log.Status = NotificationStatus.Failed;
                log.ErrorMessage = ex.Message;
                _logger.LogWarning(ex, "NotificationRoute send to {LineId} failed for {Event}", lineId, eventType);
            }
            _context.NotificationLogs.Add(log);
        }

        try { await _context.SaveChangesAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist NotificationRoute logs"); }

        return sent;
    }

    public NotificationEventType FromAlertType(AlertType alertType, string? title = null)
    {
        // Station Faulty rows are AlertType.Error or .Offline but deserve
        // their own routing bucket so the operator can route "site reported
        // faulty" separately from a plain cloud-comms outage.
        if (!string.IsNullOrEmpty(title) && title.StartsWith("Station Faulty:", StringComparison.OrdinalIgnoreCase))
            return NotificationEventType.StationFaulty;

        return alertType switch
        {
            AlertType.Offline => NotificationEventType.GridLossOffline,
            AlertType.Error => NotificationEventType.GridLossOffline,
            AlertType.LowPerformance => NotificationEventType.LowProduction,
            AlertType.MaintenanceDue => NotificationEventType.CleaningDue,
            AlertType.WarrantyExpiring => NotificationEventType.WarrantyExpiry,
            AlertType.PredictiveDiagnostic => NotificationEventType.PredictiveDiagnostic,
            _ => NotificationEventType.GridLossOffline,
        };
    }
}
