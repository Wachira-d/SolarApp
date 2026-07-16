using Hangfire;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models.Enums;

namespace SolarApp.BackgroundJobs;

/// <summary>
/// Prunes high-volume, low-value rows so the database doesn't grow without
/// bound.  Several tables gain rows on every sync cycle for every site and
/// were never cleaned up — at scale they dominate DB size and slow the
/// health/dedup queries that scan them each cycle.
///
/// What is pruned (and what is NOT):
///   • EnergyData Realtime snapshots — one per site per sync.  The Daily /
///     Monthly / Yearly AGGREGATE rows are the durable record and are kept
///     forever; only the raw Realtime rows age out.
///   • InverterApiLog — per-call diagnostics.
///   • NotificationLog — per-message delivery log.
///   • StartupLog — per-boot diagnostics.
/// AuditLog is deliberately NOT pruned here — it is the compliance trail.
///
/// Windows are configurable via SystemSettings ("Retention.*Days"); the
/// defaults are conservative.  Deletes are batched with ExecuteDeleteAsync
/// (set-based, no change-tracking) and each table is independent so one
/// failure doesn't block the rest.
/// </summary>
public class DataRetentionJob
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<DataRetentionJob> _logger;

    public DataRetentionJob(ApplicationDbContext context, ILogger<DataRetentionJob> logger)
    {
        _context = context;
        _logger = logger;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public async Task ExecuteAsync()
    {
        var now = DateTime.UtcNow;

        // Defaults (days).  Aggregates are never touched, so a short Realtime
        // window is safe — 90 days of 15-min snapshots is ample for any
        // "recent live readings" view.
        var realtimeDays = ReadSetting("Retention.RealtimeDays", 90);
        var apiLogDays   = ReadSetting("Retention.ApiLogDays", 120);
        var notifDays    = ReadSetting("Retention.NotificationLogDays", 180);
        var startupDays  = ReadSetting("Retention.StartupLogDays", 60);

        await PruneAsync("EnergyData.Realtime", async () =>
            await _context.EnergyData
                .Where(e => e.Granularity == DataGranularity.Realtime
                            && e.Timestamp < now.AddDays(-realtimeDays))
                .ExecuteDeleteAsync());

        await PruneAsync("InverterApiLog", async () =>
            await _context.InverterApiLogs
                .Where(l => l.RequestTime < now.AddDays(-apiLogDays))
                .ExecuteDeleteAsync());

        await PruneAsync("NotificationLog", async () =>
            await _context.NotificationLogs
                .Where(n => n.CreatedAt < now.AddDays(-notifDays))
                .ExecuteDeleteAsync());

        // StartupLogs table may not exist on very old schemas — guard it.
        try
        {
            await PruneAsync("StartupLog", async () =>
                await _context.Database.ExecuteSqlRawAsync(
                    "DELETE FROM [StartupLogs] WHERE [Timestamp] < {0}", now.AddDays(-startupDays)));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "StartupLog prune skipped (table may not exist)");
        }
    }

    private async Task PruneAsync(string label, Func<Task<int>> deleteAsync)
    {
        try
        {
            var deleted = await deleteAsync();
            if (deleted > 0)
                _logger.LogInformation("DataRetention: pruned {Count} {Table} row(s)", deleted, label);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DataRetention: prune of {Table} failed", label);
        }
    }

    private int ReadSetting(string key, int fallback)
    {
        try
        {
            var val = _context.SystemSettings
                .Where(s => s.Key == key)
                .Select(s => s.Value)
                .FirstOrDefault();
            return int.TryParse(val, out var days) && days > 0 ? days : fallback;
        }
        catch { return fallback; }
    }
}
