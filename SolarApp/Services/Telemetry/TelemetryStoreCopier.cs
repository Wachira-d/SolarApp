using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Models.Telemetry;

namespace SolarApp.Services.Telemetry;

public enum TelemetryCopyStatus
{
    /// <summary>Never started.</summary>
    None,
    /// <summary>Requested from the admin page; waiting for a Hangfire worker.</summary>
    Queued,
    Running,
    /// <summary>Stopped part-way (app shutdown); the next run carries on from the saved position.</summary>
    Interrupted,
    /// <summary>Stopped on an error; the next run carries on from the saved position.</summary>
    Failed,
    Done,
}

/// <summary>
/// Where the one-off copy from SQL Server is, kept as JSON in one
/// SystemSettings row (<see cref="SettingKey"/>) so no schema change is
/// needed and a restarted job carries on where it stopped.
/// </summary>
public sealed class TelemetryCopyProgress
{
    public const string SettingKey = "Telemetry.CopyFromSqlServer";

    /// <summary>A Queued/Running copy with no heartbeat for this long is presumed dead, so a new click may start it again.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    public TelemetryCopyStatus Status { get; set; }
    /// <summary>Identifies one request from the page; a Hangfire job whose id is not this one does nothing.</summary>
    public string? RunId { get; set; }
    public string? RequestedBy { get; set; }
    public DateTime? QueuedUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public DateTime? HeartbeatUtc { get; set; }

    public int ChannelsTotal { get; set; }
    public int ChannelsDone { get; set; }

    /// <summary>What is being copied now (display).</summary>
    public int? CurrentChannelId { get; set; }
    public string? CurrentChannelKey { get; set; }
    public int? CurrentInstallationId { get; set; }
    public DateTime? CurrentDayUtc { get; set; }

    /// <summary>Next unit to copy: channels with a smaller id are done; this channel continues at <see cref="ResumeDayUtc"/>.</summary>
    public int? ResumeChannelId { get; set; }
    public DateTime? ResumeDayUtc { get; set; }

    public long RawCopied { get; set; }
    /// <summary>Raw samples already in the destination (copied before, or arrived there directly).</summary>
    public long RawAlreadyThere { get; set; }
    public long AggregatesCopied { get; set; }
    public long AggregatesAlreadyThere { get; set; }
    /// <summary>Buckets the destination already had with different figures — kept, and the day handed to the rollup.</summary>
    public long AggregatesKept { get; set; }
    public int DaysQueuedForRollup { get; set; }

    public string? LastError { get; set; }
    public DateTime? LastErrorUtc { get; set; }

    public bool IsActive(DateTime nowUtc) =>
        Status is TelemetryCopyStatus.Queued or TelemetryCopyStatus.Running
        && (HeartbeatUtc ?? QueuedUtc ?? DateTime.MinValue) > nowUtc - StaleAfter;

    /// <summary>Forget the position and counters: the next run starts at the first channel.</summary>
    public void Reset()
    {
        ChannelsTotal = ChannelsDone = DaysQueuedForRollup = 0;
        RawCopied = RawAlreadyThere = AggregatesCopied = AggregatesAlreadyThere = AggregatesKept = 0;
        CurrentChannelId = CurrentInstallationId = ResumeChannelId = null;
        CurrentChannelKey = null;
        CurrentDayUtc = ResumeDayUtc = null;
        StartedUtc = FinishedUtc = null;
        LastError = null;
        LastErrorUtc = null;
    }

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static TelemetryCopyProgress FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new TelemetryCopyProgress();
        try { return JsonSerializer.Deserialize<TelemetryCopyProgress>(json, Json) ?? new TelemetryCopyProgress(); }
        catch (JsonException) { return new TelemetryCopyProgress(); }
    }

    public static async Task<TelemetryCopyProgress> LoadAsync(ApplicationDbContext db, CancellationToken ct = default) =>
        FromJson(await db.SystemSettings.AsNoTracking().Where(s => s.Key == SettingKey).Select(s => s.Value).FirstOrDefaultAsync(ct));

    /// <summary>Write the progress row (created on first save) and everything else pending in the context.</summary>
    public async Task SaveAsync(ApplicationDbContext db, string? userId = null, CancellationToken ct = default)
    {
        var row = db.SystemSettings.Local.FirstOrDefault(s => s.Key == SettingKey)
                  ?? await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKey, ct);
        if (row == null)
        {
            row = new SystemSetting
            {
                Key = SettingKey,
                Description = "ความคืบหน้าการคัดลอก telemetry เดิมจาก SQL Server (/Admin/TelemetryStorage)",
            };
            db.SystemSettings.Add(row);
        }
        row.Value = ToJson();
        row.UpdatedAt = DateTime.UtcNow;
        if (userId != null) row.UpdatedByUserId = userId;
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Copies telemetry from one store to another — in production the SQL
/// Server tables left behind when Telemetry:Store moved to PostgreSQL —
/// channel by channel, day by day (UTC).
///
/// <list type="bullet">
/// <item>Idempotent: raw samples the destination already has
/// (<see cref="ITelemetryStore.ExistingAsync"/>) are skipped; aggregate
/// buckets are upserted only where the destination has none, so a second run
/// writes nothing.</item>
/// <item>Never overwrites newer data: a bucket the destination already holds
/// with different figures was rolled up there after the switch from partial
/// raw data. It is kept, and the day is queued as a TelemetryDirtyRange so the
/// normal rollup recomputes it from the now-complete raw samples.</item>
/// <item>Resumable: the position is saved (at most every
/// <see cref="SaveEvery"/>, and after every channel) in
/// <see cref="TelemetryCopyProgress"/>; a later run continues from it.</item>
/// <item>Read-only on the source: nothing is ever deleted there.</item>
/// </list>
/// </summary>
public sealed class TelemetryStoreCopier
{
    private static readonly AggregateResolution[] Resolutions =
        { AggregateResolution.FiveMinutes, AggregateResolution.Hour, AggregateResolution.Day };
    private const int AppendBatch = 10_000;

    private readonly ApplicationDbContext _db;
    private readonly ITelemetryStore _source;
    private readonly ITelemetryStore _destination;
    private readonly ILogger? _logger;

    public TelemetryStoreCopier(ApplicationDbContext db, ITelemetryStore source, ITelemetryStore destination, ILogger? logger = null)
    {
        if (ReferenceEquals(source, destination)) throw new ArgumentException("Source and destination are the same store");
        _db = db;
        _source = source;
        _destination = destination;
        _logger = logger;
    }

    /// <summary>How often the position is written while a channel is in progress.</summary>
    public TimeSpan SaveEvery { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Stop (as Interrupted) after this many channel-days. For tests of resuming; null = run to the end.</summary>
    public int? MaxDaysThisRun { get; init; }

    public Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

    private sealed record ChannelWork(int Id, int InstallationId, string Key, DateTime FirstDay, DateTime LastDay);

    /// <param name="runId">The request this run serves (from <see cref="RequestAsync"/>); a run whose id is no longer current does nothing. Null: run regardless (tests, console).</param>
    public async Task<TelemetryCopyProgress> RunAsync(string? runId = null, CancellationToken ct = default)
    {
        var p = await TelemetryCopyProgress.LoadAsync(_db, ct);
        if (runId != null && p.RunId != runId)
        {
            _logger?.LogInformation("Telemetry copy: run {RunId} is not the current request ({Current}); nothing to do", runId, p.RunId);
            return p;
        }
        if (runId != null && p.Status == TelemetryCopyStatus.Done)
            return p;   // a duplicate of a run that already finished

        if (p.Status is TelemetryCopyStatus.None or TelemetryCopyStatus.Done) p.Reset();
        var now = Clock();
        p.RunId = runId ?? p.RunId ?? Guid.NewGuid().ToString("N");
        p.StartedUtc ??= now;
        p.FinishedUtc = null;
        p.Status = TelemetryCopyStatus.Running;
        p.HeartbeatUtc = now;
        p.LastError = null;
        p.LastErrorUtc = null;
        await p.SaveAsync(_db, ct: ct);

        var lastSave = Clock();
        var queuedDays = new HashSet<(int Site, DateTime Day)>();
        try
        {
            var work = await PlanAsync(ct);
            p.ChannelsTotal = work.Count;
            p.ChannelsDone = p.ResumeChannelId is { } resumeAt ? work.Count(c => c.Id < resumeAt) : 0;
            _logger?.LogInformation("Telemetry copy: {Total} channel(s) to copy, {Done} already done", work.Count, p.ChannelsDone);

            var days = 0;
            var (startChannel, startDay) = (p.ResumeChannelId, p.ResumeDayUtc);
            foreach (var c in work.Where(c => startChannel == null || c.Id >= startChannel).ToList())
            {
                var first = c.Id == startChannel && startDay is { } d && d > c.FirstDay ? d : c.FirstDay;
                p.ResumeChannelId = c.Id;
                p.CurrentChannelId = c.Id;
                p.CurrentChannelKey = c.Key;
                p.CurrentInstallationId = c.InstallationId;
                for (var day = first; day <= c.LastDay; day = day.AddDays(1))
                {
                    ct.ThrowIfCancellationRequested();
                    p.CurrentDayUtc = day;
                    await CopyDayAsync(c, day, p, queuedDays, ct);
                    p.ResumeDayUtc = day.AddDays(1);

                    if (MaxDaysThisRun is { } max && ++days >= max)
                    {
                        p.Status = TelemetryCopyStatus.Interrupted;
                        await SaveProgressAsync(p, CancellationToken.None);
                        return p;
                    }
                    if (Clock() - lastSave >= SaveEvery)
                    {
                        await SaveProgressAsync(p, ct);
                        lastSave = Clock();
                    }
                }
                p.ChannelsDone++;
                p.ResumeChannelId = c.Id + 1;   // ids ascend; the next channel in the plan is ≥ this
                p.ResumeDayUtc = null;
                await SaveProgressAsync(p, ct);
                lastSave = Clock();
            }

            p.Status = TelemetryCopyStatus.Done;
            p.FinishedUtc = Clock();
            p.ResumeChannelId = null;
            p.ResumeDayUtc = null;
            p.CurrentChannelId = p.CurrentInstallationId = null;
            p.CurrentChannelKey = null;
            p.CurrentDayUtc = null;
            await SaveProgressAsync(p, ct);
            _logger?.LogInformation(
                "Telemetry copy finished: {Raw} raw copied ({RawThere} already there), {Agg} aggregates copied " +
                "({AggThere} already there, {Kept} kept), {Days} day(s) queued for rollup",
                p.RawCopied, p.RawAlreadyThere, p.AggregatesCopied, p.AggregatesAlreadyThere, p.AggregatesKept, p.DaysQueuedForRollup);
            return p;
        }
        catch (SupersededException ex)
        {
            // The page started a new run (this one looked dead); its progress row is not ours to write.
            _logger?.LogWarning("Telemetry copy: {Message}; stopping", ex.Message);
            return await TelemetryCopyProgress.LoadAsync(_db, CancellationToken.None);
        }
        catch (Exception ex) when (ex is OperationCanceledException && ct.IsCancellationRequested)
        {
            p.Status = TelemetryCopyStatus.Interrupted;
            await TrySaveAsync(p);
            _logger?.LogInformation("Telemetry copy interrupted at channel {Channel} day {Day:yyyy-MM-dd}; it resumes from there",
                p.CurrentChannelId, p.CurrentDayUtc);
            throw;
        }
        catch (Exception ex)
        {
            p.Status = TelemetryCopyStatus.Failed;
            p.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            p.LastErrorUtc = Clock();
            await TrySaveAsync(p);
            _logger?.LogError(ex, "Telemetry copy failed at channel {Channel} day {Day:yyyy-MM-dd}", p.CurrentChannelId, p.CurrentDayUtc);
            throw;
        }
    }

    /// <summary>
    /// Channels known to the catalog, ordered by id, each with the UTC days
    /// that hold data in the source. Uses the source's own extents when it
    /// can report them (skipping channels with nothing there), else the
    /// catalog's FirstTs..LastTs.
    /// </summary>
    private async Task<List<ChannelWork>> PlanAsync(CancellationToken ct)
    {
        var channels = await _db.TelemetryChannels.AsNoTracking()
            .OrderBy(c => c.Id)
            .Select(c => new { c.Id, c.InstallationId, c.Key, c.FirstTs, c.LastTs })
            .ToListAsync(ct);
        var extents = _source is ITelemetryStoreStats stats ? await stats.ChannelExtentsAsync(ct) : null;
        var today = Clock().Date;

        var work = new List<ChannelWork>();
        foreach (var c in channels)
        {
            DateTime first, last;
            if (extents != null)
            {
                if (!extents.TryGetValue(c.Id, out var e)) continue;
                (first, last) = (e.First.Date, e.Last.Date);
            }
            else
            {
                if (c.FirstTs == default) continue;
                // Day buckets start at site-local midnight, up to a UTC day earlier.
                (first, last) = (c.FirstTs.Date.AddDays(-1), (c.LastTs > c.FirstTs ? c.LastTs : c.FirstTs).Date);
                if (last > today) last = today;
            }
            work.Add(new ChannelWork(c.Id, c.InstallationId, c.Key,
                DateTime.SpecifyKind(first, DateTimeKind.Utc), DateTime.SpecifyKind(last, DateTimeKind.Utc)));
        }
        return work;
    }

    private async Task CopyDayAsync(ChannelWork c, DateTime day, TelemetryCopyProgress p,
        HashSet<(int Site, DateTime Day)> queuedDays, CancellationToken ct)
    {
        var ids = new[] { c.Id };
        var from = day;
        var to = day.AddDays(1);
        var destinationHad = false;
        var wrote = false;

        // ── raw ──
        var raw = await _source.ReadRawAsync(ids, from, to, ct);
        if (raw.Count > 0)
        {
            // Columnstore has no unique key: a replayed sample can be there twice. Keep one per timestamp.
            var unique = raw.GroupBy(s => s.TsUtc).Select(g => g.Last()).ToList();
            var existing = await _destination.ExistingAsync(unique.Select(s => (s.ChannelId, s.TsUtc)).ToList(), ct);
            var missing = unique.Where(s => !existing.Contains((s.ChannelId, s.TsUtc))).ToList();
            for (var i = 0; i < missing.Count; i += AppendBatch)
                await _destination.AppendRawAsync(missing.GetRange(i, Math.Min(AppendBatch, missing.Count - i)), ct);
            p.RawCopied += missing.Count;
            p.RawAlreadyThere += unique.Count - missing.Count;
            destinationHad |= existing.Count > 0;
            wrote |= missing.Count > 0;
        }

        // ── aggregates, every resolution ──
        foreach (var res in Resolutions)
        {
            var src = await _source.ReadAggregatesAsync(ids, res, from, to, ct);
            if (src.Count == 0) continue;
            var dst = (await _destination.ReadAggregatesAsync(ids, res, from, to, ct))
                .GroupBy(a => a.BucketStartUtc.Ticks).ToDictionary(g => g.Key, g => g.Last());
            destinationHad |= dst.Count > 0;

            var missing = new List<AggregatePoint>();
            foreach (var a in src.GroupBy(a => a.BucketStartUtc.Ticks).Select(g => g.Last()))
            {
                if (!dst.TryGetValue(a.BucketStartUtc.Ticks, out var there)) missing.Add(a);
                else if (SameFigures(a, there)) p.AggregatesAlreadyThere++;
                else p.AggregatesKept++;
            }
            if (missing.Count > 0) await _destination.UpsertAggregatesAsync(missing, ct);
            p.AggregatesCopied += missing.Count;
            wrote |= missing.Count > 0;
        }

        // Old and new data meet on this day: let the rollup rebuild its
        // buckets from the merged raw samples (it only ever upserts).
        if (wrote && destinationHad && queuedDays.Add((c.InstallationId, day)))
        {
            _db.TelemetryDirtyRanges.Add(new TelemetryDirtyRange
            {
                InstallationId = c.InstallationId, FromUtc = from, ToUtc = to, CreatedAt = Clock(),
            });
            p.DaysQueuedForRollup++;
        }
    }

    /// <summary>Stores keep values as real (float4): compare at that precision.</summary>
    private static bool SameFigures(AggregatePoint a, AggregatePoint b) =>
        a.Count == b.Count && (float)a.Min == (float)b.Min && (float)a.Max == (float)b.Max && (float)a.Avg == (float)b.Avg
        && (float)a.First == (float)b.First && (float)a.Last == (float)b.Last;

    /// <summary>Save, unless the page has since started a different run — then stop this one.</summary>
    private async Task SaveProgressAsync(TelemetryCopyProgress p, CancellationToken ct)
    {
        var current = await TelemetryCopyProgress.LoadAsync(_db, ct);
        if (current.RunId != null && current.RunId != p.RunId)
            throw new SupersededException($"run {p.RunId} was superseded by {current.RunId}");
        p.HeartbeatUtc = Clock();
        await p.SaveAsync(_db, ct: ct);
    }

    private async Task TrySaveAsync(TelemetryCopyProgress p)
    {
        try
        {
            p.HeartbeatUtc = Clock();
            await p.SaveAsync(_db);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Telemetry copy: could not save progress");
        }
    }

    private sealed class SupersededException : Exception
    {
        public SupersededException(string message) : base(message) { }
    }

    public sealed record Request(bool Started, string? RunId, TelemetryCopyProgress Progress);

    /// <summary>
    /// The admin page's click: mark a copy Queued and hand back the run id to
    /// enqueue — or, while one is queued or running, do nothing. A finished
    /// copy starts over from the first channel (writing only what is
    /// missing); a failed or interrupted one carries on from its position.
    /// </summary>
    public static async Task<Request> RequestAsync(ApplicationDbContext db, string? userName, string? userId, DateTime nowUtc,
        CancellationToken ct = default)
    {
        var p = await TelemetryCopyProgress.LoadAsync(db, ct);
        if (p.IsActive(nowUtc)) return new Request(false, p.RunId, p);

        if (p.Status is TelemetryCopyStatus.None or TelemetryCopyStatus.Done) p.Reset();
        p.Status = TelemetryCopyStatus.Queued;
        p.RunId = Guid.NewGuid().ToString("N");
        p.RequestedBy = userName;
        p.QueuedUtc = nowUtc;
        p.HeartbeatUtc = null;
        await p.SaveAsync(db, userId, ct);
        return new Request(true, p.RunId, p);
    }

    /// <summary>Record that a request could not even be queued (e.g. Hangfire unavailable).</summary>
    public static async Task RecordFailureAsync(ApplicationDbContext db, string message, DateTime nowUtc, CancellationToken ct = default)
    {
        var p = await TelemetryCopyProgress.LoadAsync(db, ct);
        p.Status = TelemetryCopyStatus.Failed;
        p.LastError = message.Length > 500 ? message[..500] : message;
        p.LastErrorUtc = nowUtc;
        await p.SaveAsync(db, ct: ct);
    }
}
