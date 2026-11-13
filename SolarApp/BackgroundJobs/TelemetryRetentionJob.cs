using Hangfire;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models.Enums;
using SolarApp.Services;
using SolarApp.Services.Telemetry;

namespace SolarApp.BackgroundJobs;

/// <summary>
/// Ages out telemetry (DESIGN.md §5.1–§5.2): raw samples after
/// "Telemetry.RawRetentionDays" (90), 5-minute aggregates after
/// "Telemetry.FiveMinuteRetentionDays" (400 — a full year plus a month for
/// the PV model and year-on-year), device events after two years. Hourly and
/// daily aggregates are kept forever; they are small and they are the history.
///
/// Deletes go through ITelemetryStore in batches (DELETE TOP n on SQL
/// Server) so the transaction log and locks stay small while ingest keeps
/// appending. Each tier is independent: one failing does not stop the rest.
/// </summary>
public class TelemetryRetentionJob
{
    public const int DefaultRawDays = 90;
    public const int DefaultFiveMinuteDays = 400;
    public static readonly TimeSpan DeviceEventRetention = TimeSpan.FromDays(730);
    public const int BatchSize = 50_000;
    /// <summary>Stops one run at this many batches per tier; tomorrow continues. Keeps a first run after a long outage bounded.</summary>
    public const int MaxBatchesPerRun = 400;

    private readonly ApplicationDbContext _context;
    private readonly ITelemetryStore _store;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TelemetryRetentionJob> _logger;

    public TelemetryRetentionJob(ApplicationDbContext context, ITelemetryStore store, IConfiguration configuration,
        ILogger<TelemetryRetentionJob> logger)
    {
        _context = context;
        _store = store;
        _configuration = configuration;
        _logger = logger;
    }

    public sealed record Result(int Raw, int FiveMinute, int DeviceEvents);

    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
    public Task ExecuteAsync() => RunAsync(DateTime.UtcNow);

    public async Task<Result> RunAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var rawDays = ReadDays("Telemetry.RawRetentionDays", DefaultRawDays);
        var fiveDays = ReadDays("Telemetry.FiveMinuteRetentionDays", DefaultFiveMinuteDays);

        var raw = await PruneAsync("TelemetryRaw", n => _store.DeleteRawBeforeAsync(nowUtc.AddDays(-rawDays), n, ct));
        var five = await PruneAsync("TelemetryAggregates(5 min)",
            n => _store.DeleteAggregatesBeforeAsync(AggregateResolution.FiveMinutes, nowUtc.AddDays(-fiveDays), n, ct));
        var events = await PruneAsync("DeviceEvents", n => DeleteDeviceEventsAsync(nowUtc - DeviceEventRetention, n, ct));

        _logger.LogInformation(
            "Telemetry retention: deleted {Raw} raw (> {RawDays} d), {Five} 5-min aggregates (> {FiveDays} d), {Events} device events (> 2 y)",
            raw, rawDays, five, fiveDays, events);

        // Weekly, on the site's Sunday: deletes leave columnstore row groups
        // full of tombstones; REORGANIZE compacts them online. SQL Server only.
        if (SiteTime.ToSiteTime(nowUtc).DayOfWeek == DayOfWeek.Sunday && _store is SqlServerTelemetryStore)
            await ReorganizeColumnstoreAsync(ct);

        return new Result(raw, five, events);
    }

    private async Task<int> PruneAsync(string label, Func<int, Task<int>> deleteBatch)
    {
        var total = 0;
        try
        {
            for (var i = 0; i < MaxBatchesPerRun; i++)
            {
                var n = await deleteBatch(BatchSize);
                total += n;
                if (n < BatchSize) break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telemetry retention: prune of {Table} failed after {Count} row(s)", label, total);
        }
        return total;
    }

    private async Task<int> DeleteDeviceEventsAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct)
    {
        var batch = _context.DeviceEvents.Where(e => e.TsUtc < cutoffUtc).OrderBy(e => e.Id).Take(batchSize);
        if (_context.Database.IsRelational())
            return await batch.ExecuteDeleteAsync(ct);

        // The in-memory provider (tests) has no set-based delete.
        var rows = await batch.ToListAsync(ct);
        _context.DeviceEvents.RemoveRange(rows);
        await _context.SaveChangesAsync(ct);
        return rows.Count;
    }

    private async Task ReorganizeColumnstoreAsync(CancellationToken ct)
    {
        try
        {
            var cs = _configuration.GetConnectionString("TelemetryConnection")
                     ?? _configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrEmpty(cs)) return;
            await using var conn = new SqlConnection(cs);
            await conn.OpenAsync(ct);
            // The index exists only where the edition supports columnstore
            // (see Startup's TelemetryRaw stage); otherwise this is a no-op.
            await using var cmd = new SqlCommand(
                "IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'CCI_TelemetryRaw' AND object_id = OBJECT_ID('TelemetryRaw')) " +
                "ALTER INDEX [CCI_TelemetryRaw] ON [TelemetryRaw] REORGANIZE WITH (COMPRESS_ALL_ROW_GROUPS = ON);", conn)
            { CommandTimeout = 1800 };
            await cmd.ExecuteNonQueryAsync(ct);
            _logger.LogInformation("Telemetry retention: TelemetryRaw columnstore reorganised");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telemetry retention: columnstore reorganise skipped");
        }
    }

    private int ReadDays(string key, int fallback)
    {
        try
        {
            var val = _context.SystemSettings.Where(s => s.Key == key).Select(s => s.Value).FirstOrDefault();
            return int.TryParse(val, out var days) && days > 0 ? days : fallback;
        }
        catch { return fallback; }
    }
}
