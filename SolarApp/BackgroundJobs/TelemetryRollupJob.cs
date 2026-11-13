using Hangfire;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Models.Telemetry;
using SolarApp.Services;
using SolarApp.Services.Telemetry;

namespace SolarApp.BackgroundJobs;

/// <summary>
/// Rolls telemetry up raw → 5 min → 1 h → 1 day (DESIGN.md §5.3) and keeps
/// the Daily EnergyData row of gateway sites current.
///
/// Per site a watermark (TelemetryRollupState) marks where 5-minute buckets
/// are final. Each run:
/// <list type="number">
/// <item>recomputes the buckets named by TelemetryDirtyRanges — data that
/// arrived behind the watermark, typically an HA box replaying its buffer
/// after a day offline — plus the hours and days above them;</item>
/// <item>advances the watermark over buckets that closed more than
/// <see cref="Grace"/> ago, so a sample a few minutes late still lands in its
/// bucket on the normal path instead of forcing a recompute;</item>
/// <item>upserts today's and yesterday's Daily EnergyData from the aggregates
/// (and any older day a dirty range touched).</item>
/// </list>
/// Work per site per run is capped at <see cref="MaxSpanPerRun"/> for each of
/// the two passes, so a site that comes back with a month of history catches
/// up over a few hours instead of one run holding a day's raw rows of a
/// hundred sites in memory.
/// </summary>
public class TelemetryRollupJob
{
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxSpanPerRun = TimeSpan.FromDays(1);

    /// <summary>
    /// How far back to look for a counter's opening reading, and how far past a
    /// dirty range to recompute: a late sample also changes the opening of the
    /// next bucket that has data.
    /// </summary>
    public static readonly TimeSpan CounterLookback = TimeSpan.FromHours(2);

    /// <summary>Never start a fresh watermark further back than this (guards a bogus FirstTs).</summary>
    public static readonly TimeSpan MaxInitialBacklog = TimeSpan.FromDays(400);

    private readonly ApplicationDbContext _context;
    private readonly ITelemetryStore _store;
    private readonly ILogger<TelemetryRollupJob> _logger;
    private readonly TimeZoneInfo _tz;

    public TelemetryRollupJob(ApplicationDbContext context, ITelemetryStore store, ILogger<TelemetryRollupJob> logger)
    {
        _context = context;
        _store = store;
        _logger = logger;
        _tz = SiteTime.TimeZone;   // day boundaries are site-local (SiteTime)
    }

    private sealed record ChannelInfo(int Id, string Key, bool IsCumulative, DateTime FirstTs);

    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [AutomaticRetry(Attempts = 0)]   // the next run 5 minutes later is the retry
    public Task ExecuteAsync() => RunAsync(DateTime.UtcNow);

    public async Task RunAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var siteIds = await _context.TelemetryChannels.AsNoTracking()
            .Select(c => c.InstallationId).Distinct().ToListAsync(ct);

        int ok = 0, failed = 0;
        foreach (var id in siteIds)
        {
            try
            {
                await RollupSiteAsync(id, nowUtc, ct);
                ok++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One site's bad data must not stop the others; its watermark
                // did not move, so the next run retries the same span.
                failed++;
                _logger.LogError(ex, "Telemetry rollup failed for installation {InstallationId}", id);
                _context.ChangeTracker.Clear();
            }
        }
        if (siteIds.Count > 0)
            _logger.LogInformation("Telemetry rollup: {Ok} site(s) rolled up, {Failed} failed", ok, failed);
    }

    public async Task RollupSiteAsync(int installationId, DateTime nowUtc, CancellationToken ct = default)
    {
        var channels = await _context.TelemetryChannels.AsNoTracking()
            .Where(c => c.InstallationId == installationId)
            .Select(c => new ChannelInfo(c.Id, c.Key, c.IsCumulative, c.FirstTs))
            .ToListAsync(ct);
        if (channels.Count == 0) return;

        var state = await _context.TelemetryRollupStates.FindAsync(new object[] { installationId }, ct);
        if (state == null)
        {
            var earliest = channels.Where(c => c.FirstTs > DateTime.MinValue).Select(c => c.FirstTs)
                .DefaultIfEmpty(nowUtc).Min();
            if (earliest < nowUtc - MaxInitialBacklog) earliest = nowUtc - MaxInitialBacklog;
            state = new TelemetryRollupState
            {
                InstallationId = installationId,
                WatermarkUtc = TelemetryRollup.FiveMinuteStart(earliest),
                UpdatedAt = nowUtc,
            };
            _context.TelemetryRollupStates.Add(state);
            await _context.SaveChangesAsync(ct);
        }

        var touchedDays = new HashSet<DateTime>();

        // ── 1. Late data ──
        var dirty = await _context.TelemetryDirtyRanges
            .Where(d => d.InstallationId == installationId)
            .OrderBy(d => d.FromUtc)
            .ToListAsync(ct);
        if (dirty.Count > 0)
        {
            var spans = MergeDirty(dirty, state.WatermarkUtc);
            var budget = MaxSpanPerRun;
            var leftovers = new List<(DateTime From, DateTime To)>();
            foreach (var (from, to) in spans)
            {
                if (budget <= TimeSpan.Zero) { leftovers.Add((from, to)); continue; }
                var end = to - from > budget ? from + budget : to;
                await RecomputeAsync(channels, from, end, ct);
                budget -= end - from;
                if (end < to) leftovers.Add((end, to));
                for (var d = TelemetryRollup.LocalDate(from, _tz); d <= TelemetryRollup.LocalDate(end.AddTicks(-1), _tz); d = d.AddDays(1))
                    touchedDays.Add(d);
            }
            // Remove exactly the rows read (ingest may add new ones meanwhile)
            // and put back what the budget left for the next run.
            _context.TelemetryDirtyRanges.RemoveRange(dirty);
            foreach (var (from, to) in leftovers)
                _context.TelemetryDirtyRanges.Add(new TelemetryDirtyRange
                {
                    InstallationId = installationId, FromUtc = from, ToUtc = to, CreatedAt = nowUtc,
                });
            await _context.SaveChangesAsync(ct);
        }

        // ── 2. Advance the watermark over closed buckets ──
        // Bucket [s, s+5) is closed once s+5 ≤ now − grace, i.e. every s < floor5(now − grace).
        var target = TelemetryRollup.FiveMinuteStart(nowUtc - Grace);
        var advanced = false;
        if (target > state.WatermarkUtc)
        {
            var to = target - state.WatermarkUtc > MaxSpanPerRun ? state.WatermarkUtc + MaxSpanPerRun : target;
            await RecomputeAsync(channels, state.WatermarkUtc, to, ct);
            state.WatermarkUtc = to;
            state.UpdatedAt = nowUtc;
            await _context.SaveChangesAsync(ct);
            advanced = true;
        }

        // ── 3. Daily EnergyData for the days that changed ──
        if (advanced || touchedDays.Count > 0)
        {
            var today = TelemetryRollup.LocalDate(nowUtc, _tz);
            touchedDays.Add(today);
            touchedDays.Add(today.AddDays(-1));
            await BridgeDailyAsync(installationId, channels, touchedDays, today, ct);
        }
    }

    /// <summary>
    /// Dirty rows → disjoint 5-minute-aligned spans, extended by the counter
    /// look-back and clamped at the watermark (beyond it the normal pass does the work).
    /// </summary>
    private static List<(DateTime From, DateTime To)> MergeDirty(IEnumerable<TelemetryDirtyRange> rows, DateTime watermark)
    {
        var spans = new List<(DateTime From, DateTime To)>();
        foreach (var r in rows.OrderBy(r => r.FromUtc))
        {
            var from = TelemetryRollup.FiveMinuteStart(r.FromUtc);
            var to = TelemetryRollup.FiveMinuteStart(r.ToUtc) + TelemetryRollup.FiveMinutes + CounterLookback;
            if (to > watermark) to = watermark;
            if (to <= from) continue;
            if (spans.Count > 0 && from <= spans[^1].To)
                spans[^1] = (spans[^1].From, to > spans[^1].To ? to : spans[^1].To);
            else
                spans.Add((from, to));
        }
        return spans;
    }

    /// <summary>Recompute 5-minute buckets in [from, to) and every hour and site-local day that overlaps them.</summary>
    private async Task RecomputeAsync(IReadOnlyList<ChannelInfo> channels, DateTime from, DateTime to, CancellationToken ct)
    {
        var cumulative = channels.Where(c => c.IsCumulative).Select(c => c.Id).ToHashSet();
        var plain = channels.Where(c => !c.IsCumulative).Select(c => c.Id).ToList();
        bool IsCum(int id) => cumulative.Contains(id);

        // Counters need context on both sides: the reading before the span
        // opens its first bucket, the minutes after it tell a bad read from a reset.
        var raw = await _store.ReadRawAsync(plain, from, to, ct);
        if (cumulative.Count > 0)
            raw.AddRange(await _store.ReadRawAsync(cumulative, from - CounterLookback, to + TelemetryRollup.GlitchWindow, ct));

        var fives = TelemetryRollup.ToFiveMinutes(raw, IsCum, from, to);
        await _store.UpsertAggregatesAsync(fives, ct);

        var ids = channels.Select(c => c.Id).ToList();
        var hourFrom = TelemetryRollup.HourStart(from, _tz);
        var hourTo = TelemetryRollup.HourStart(to.AddTicks(-1), _tz).AddHours(1);
        var children = await _store.ReadAggregatesAsync(ids, AggregateResolution.FiveMinutes, hourFrom, hourTo, ct);
        var hours = TelemetryRollup.RollUp(children, AggregateResolution.Hour, t => TelemetryRollup.HourStart(t, _tz), IsCum);
        await _store.UpsertAggregatesAsync(hours, ct);

        var dayFrom = TelemetryRollup.DayStart(from, _tz);
        var dayTo = TelemetryRollup.LocalDateStartUtc(TelemetryRollup.LocalDate(to.AddTicks(-1), _tz).AddDays(1), _tz);
        var hourly = await _store.ReadAggregatesAsync(ids, AggregateResolution.Hour, dayFrom, dayTo, ct);
        var days = TelemetryRollup.RollUp(hourly, AggregateResolution.Day, t => TelemetryRollup.DayStart(t, _tz), IsCum);
        await _store.UpsertAggregatesAsync(days, ct);
    }

    /// <summary>
    /// Daily EnergyData from the aggregates (EnergyDailyBridge), for sites
    /// whose source of record includes the gateway.
    /// </summary>
    private async Task BridgeDailyAsync(int installationId, IReadOnlyList<ChannelInfo> channels,
        IEnumerable<DateTime> localDates, DateTime today, CancellationToken ct)
    {
        var inst = await _context.Installations
            .Select(i => new { i.Id, i.TelemetrySource, i.SystemCapacityKw })
            .FirstOrDefaultAsync(i => i.Id == installationId, ct);
        if (inst == null || !TelemetrySourcePolicy.GatewayWritesDaily(inst.TelemetrySource)) return;

        var counters = channels.Where(c => EnergyDailyBridge.IsCounterKey(c.Key)).ToList();
        var powers = channels.Where(c => EnergyDailyBridge.IsPowerKey(c.Key)).ToList();
        if (counters.Count == 0 && powers.Count == 0) return;

        // Same plausibility ceiling as SolarDataSyncJob: a site cannot make
        // more than 18 × kWp in a day; more means a mis-scaled mapping.
        var plausibleMax = inst.SystemCapacityKw > 0 ? (double)inst.SystemCapacityKw * 18 : double.MaxValue;

        foreach (var date in localDates.Where(d => d <= today).OrderBy(d => d))
        {
            var start = TelemetryRollup.LocalDateStartUtc(date, _tz);
            var end = TelemetryRollup.LocalDateStartUtc(date.AddDays(1), _tz);

            var hourly = await _store.ReadAggregatesAsync(counters.Select(c => c.Id).ToList(), AggregateResolution.Hour, start, end, ct);
            var fives = await _store.ReadAggregatesAsync(powers.Select(c => c.Id).ToList(), AggregateResolution.FiveMinutes, start, end, ct);
            var energy = EnergyDailyBridge.Compute(ByKey(counters, hourly), ByKey(powers, fives));
            if (energy.IsEmpty) continue;

            if (energy.GenerationKwh > plausibleMax)
            {
                _logger.LogWarning("Telemetry bridge: implausible PV {Kwh:0.0} kWh on {Date:yyyy-MM-dd} for installation {Id} " +
                                   "(capacity {Cap} kWp) — check the entity mapping scale; generation not written",
                    energy.GenerationKwh, date, installationId, inst.SystemCapacityKw);
                energy = energy with { GenerationKwh = null };
            }

            var row = await _context.EnergyData.FirstOrDefaultAsync(e => e.InstallationId == installationId
                && e.Granularity == DataGranularity.Daily && e.Timestamp == date, ct);
            if (row == null)
            {
                row = new EnergyData { InstallationId = installationId, Timestamp = date, Granularity = DataGranularity.Daily };
                _context.EnergyData.Add(row);
            }
            ApplyDaily(row, energy);
        }
        await _context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Write a day's figures without ever lowering one. Energy over a day
    /// only grows as data arrives (late samples add, never remove), and the
    /// row may already hold a fresher or fuller figure: ingest writes the
    /// inverter's own "today" counter within seconds, and on a Hybrid site the
    /// cloud may have written the hours before the gateway came online. This
    /// is the same monotonic rule SolarDataSyncJob applies.
    /// </summary>
    public static void ApplyDaily(EnergyData row, DailyEnergy e)
    {
        static decimal R(double v) => Math.Round((decimal)v, 3);
        if (e.GenerationKwh is { } g && R(g) > row.EnergyGenerationKwh) row.EnergyGenerationKwh = R(g);
        if (e.GridImportKwh is { } imp && R(imp) > row.GridImportKwh) row.GridImportKwh = R(imp);
        if (e.GridExportKwh is { } exp && R(exp) > row.GridExportKwh) row.GridExportKwh = R(exp);
        if (e.BatteryChargeKwh is { } ch && (row.BatteryChargeKwh == null || R(ch) > row.BatteryChargeKwh)) row.BatteryChargeKwh = R(ch);
        if (e.BatteryDischargeKwh is { } dis && (row.BatteryDischargeKwh == null || R(dis) > row.BatteryDischargeKwh))
            row.BatteryDischargeKwh = R(dis);

        // Derived from the stored figures so it stays consistent with them.
        if (row.EnergyGenerationKwh > 0)
            row.SelfConsumptionKwh = Math.Max(0, row.EnergyGenerationKwh - row.GridExportKwh);
        else if (e.SelfConsumptionKwh is { } sc)
            row.SelfConsumptionKwh = R(sc);
    }

    private static Dictionary<string, IReadOnlyList<AggregatePoint>> ByKey(IEnumerable<ChannelInfo> channels,
        IEnumerable<AggregatePoint> points)
    {
        var keyById = channels.ToDictionary(c => c.Id, c => c.Key);
        return points.Where(p => keyById.ContainsKey(p.ChannelId))
            .GroupBy(p => keyById[p.ChannelId])
            .ToDictionary(g => g.Key, g => (IReadOnlyList<AggregatePoint>)g.OrderBy(p => p.BucketStartUtc).ToList());
    }
}
