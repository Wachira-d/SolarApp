using Microsoft.EntityFrameworkCore;
using SolarApp.BackgroundJobs;
using SolarApp.Data;

namespace SolarApp.Services.Telemetry;

public enum TelemetryStoreKind { SqlServer, Postgres, Timescale, Other }

/// <summary>Everything /Admin/TelemetryStorage shows. Each part degrades to an error text on its own.</summary>
public sealed class TelemetryStorageStatus
{
    public TelemetryStoreKind Kind { get; set; }
    public string? Schema { get; set; }

    public TelemetryStorageReport? Storage { get; set; }
    public string? StorageError { get; set; }

    public int Channels { get; set; }
    public int Sites { get; set; }
    public int ActiveChannels { get; set; }
    public int ActiveSites { get; set; }
    public DateTime? LastSampleUtc { get; set; }
    public long? RawLast24h { get; set; }
    public string? CountsError { get; set; }

    public int RawRetentionDays { get; set; } = TelemetryRetentionJob.DefaultRawDays;
    public int FiveMinuteRetentionDays { get; set; } = TelemetryRetentionJob.DefaultFiveMinuteDays;
    public TelemetryCapacityEstimate? Estimate { get; set; }
    public List<string> EstimateNotes { get; } = new();

    /// <summary>SQL Server tables left behind, checked only when PostgreSQL is active.</summary>
    public TelemetryStorageReport? Legacy { get; set; }
    public string? LegacyError { get; set; }

    public TelemetryCopyProgress Copy { get; set; } = new();
    public bool CopyActive { get; set; }

    public bool IsPostgres => Kind is TelemetryStoreKind.Postgres or TelemetryStoreKind.Timescale;
    /// <summary>The copy tool: PostgreSQL is active and the SQL Server tables still hold rows.</summary>
    public bool CopyAvailable => IsPostgres && Legacy?.AnyRows == true;
}

/// <summary>Reads the storage status of the active telemetry store (read-only; changes nothing).</summary>
public sealed class TelemetryStorageInspector
{
    /// <summary>Bytes per row when the store holds too few rows to measure (DESIGN.md §5.2, TIMESCALEDB-SETUP.md).</summary>
    public static (double Raw, double Aggregate) ReferenceBytesPerRow(TelemetryStoreKind kind) => kind switch
    {
        TelemetryStoreKind.Postgres => (80, 95),
        TelemetryStoreKind.Timescale => (2.5, 8),
        _ => (2.5, 30),
    };

    private readonly ApplicationDbContext _db;
    private readonly ITelemetryStore _store;
    private readonly Func<ITelemetryStoreStats?> _legacySource;
    private readonly ILogger? _logger;

    /// <param name="legacySource">The SQL Server tables, for the "data left behind" check in PostgreSQL mode; null when none is configured.</param>
    public TelemetryStorageInspector(ApplicationDbContext db, ITelemetryStore store, Func<ITelemetryStoreStats?> legacySource,
        ILogger? logger = null)
    {
        _db = db;
        _store = store;
        _legacySource = legacySource;
        _logger = logger;
    }

    public async Task<TelemetryStorageStatus> InspectAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var s = new TelemetryStorageStatus
        {
            Kind = _store switch
            {
                SqlServerTelemetryStore => TelemetryStoreKind.SqlServer,
                PostgresTelemetryStore => TelemetryStoreKind.Postgres,
                _ => TelemetryStoreKind.Other,
            },
        };

        // ── the active store's tables ──
        if (_store is ITelemetryStoreStats stats)
        {
            try { s.Storage = await stats.StorageAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                s.StorageError = ex.Message;
                _logger?.LogWarning(ex, "Telemetry storage: reading table sizes failed");
            }
        }
        if (_store is PostgresTelemetryStore pg)
        {
            s.Schema = pg.Schema;
            if (pg.TimescaleEnabled) s.Kind = TelemetryStoreKind.Timescale;
        }

        // ── counts ──
        var since = nowUtc.AddHours(-24);
        try
        {
            var channels = _db.TelemetryChannels.AsNoTracking();
            s.Channels = await channels.CountAsync(ct);
            s.Sites = await channels.Select(c => c.InstallationId).Distinct().CountAsync(ct);
            s.ActiveChannels = await channels.CountAsync(c => c.LastTs >= since, ct);
            s.ActiveSites = await channels.Where(c => c.LastTs >= since).Select(c => c.InstallationId).Distinct().CountAsync(ct);
            s.LastSampleUtc = s.Channels > 0 ? await channels.MaxAsync(c => (DateTime?)c.LastTs, ct) : null;
            s.RawRetentionDays = await ReadDaysAsync("Telemetry.RawRetentionDays", TelemetryRetentionJob.DefaultRawDays, ct);
            s.FiveMinuteRetentionDays = await ReadDaysAsync("Telemetry.FiveMinuteRetentionDays", TelemetryRetentionJob.DefaultFiveMinuteDays, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            s.CountsError = ex.Message;
            _logger?.LogWarning(ex, "Telemetry storage: reading channel counts failed");
        }
        if (_store is ITelemetryStoreStats counter && s.StorageError == null)
        {
            try { s.RawLast24h = await counter.CountRawAsync(since, nowUtc, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                s.CountsError ??= ex.Message;
                _logger?.LogWarning(ex, "Telemetry storage: counting raw samples failed");
            }
        }

        Estimate(s);

        // ── SQL Server data left behind, and the copy ──
        if (s.IsPostgres)
        {
            try
            {
                var legacy = _legacySource();
                if (legacy == null) s.LegacyError = "ไม่ได้ตั้ง connection string ของ SQL Server";
                else s.Legacy = await legacy.StorageAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                s.LegacyError = ex.Message;
                _logger?.LogInformation(ex, "Telemetry storage: SQL Server telemetry tables not readable");
            }
        }
        try
        {
            s.Copy = await TelemetryCopyProgress.LoadAsync(_db, ct);
            s.CopyActive = s.Copy.IsActive(nowUtc);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Telemetry storage: reading copy progress failed");
        }
        return s;
    }

    private void Estimate(TelemetryStorageStatus s)
    {
        if (s.RawLast24h is not { } perDay)
        {
            s.EstimateNotes.Add("ยังประมาณไม่ได้ เพราะนับจำนวนค่าดิบ 24 ชั่วโมงล่าสุดไม่ได้");
            return;
        }
        var tables = s.Storage?.Tables ?? Array.Empty<TelemetryTableSize>();
        var raw = tables.FirstOrDefault(t => t.Kind == TelemetryTableKind.Raw);
        var agg = tables.FirstOrDefault(t => t.Kind is TelemetryTableKind.FiveMinutes or TelemetryTableKind.AllAggregates);
        var reference = ReferenceBytesPerRow(s.Kind);

        s.Estimate = TelemetryCapacityEstimate.Compute(
            TelemetryCapacityEstimate.Measure(raw?.Bytes, raw?.Rows), TelemetryCapacityEstimate.Measure(agg?.Bytes, agg?.Rows),
            reference.Raw, reference.Aggregate, perDay, s.ActiveChannels, s.ActiveSites, s.RawRetentionDays, s.FiveMinuteRetentionDays);

        if (!s.Estimate.RawMeasured || !s.Estimate.AggregateMeasured)
            s.EstimateNotes.Add($"ข้อมูลยังน้อย (ต่ำกว่า {TelemetryCapacityEstimate.MinRowsToMeasure:N0} แถว) " +
                                "ส่วนที่วัดไม่ได้จึงใช้ค่าอ้างอิงจากเอกสารออกแบบแทน");
        if (s.Kind == TelemetryStoreKind.Timescale && raw is { BeforeCompressionBytes: null })
            s.EstimateNotes.Add("ยังไม่มีข้อมูลดิบที่ถูกบีบอัด (TimescaleDB บีบอัดข้อมูลที่เก่ากว่า 7 วัน) " +
                                "ขนาดต่อแถวตอนนี้จึงสูงกว่าระยะยาว ให้ดูตัวเลขนี้อีกครั้งหลังใช้งานครบ 1–2 สัปดาห์");
        if (s.Kind == TelemetryStoreKind.Postgres)
            s.EstimateNotes.Add("PostgreSQL ที่ไม่มี TimescaleDB ไม่บีบอัดข้อมูล จึงใช้พื้นที่มากกว่า TimescaleDB หลายสิบเท่า");
        if (s.ActiveSites == 0)
            s.EstimateNotes.Add("ไม่มีไซต์ที่ส่งข้อมูลใน 24 ชั่วโมงล่าสุด จึงคิดขนาดต่อไซต์ไม่ได้");
    }

    private async Task<int> ReadDaysAsync(string key, int fallback, CancellationToken ct)
    {
        var val = await _db.SystemSettings.AsNoTracking().Where(x => x.Key == key).Select(x => x.Value).FirstOrDefaultAsync(ct);
        return int.TryParse(val, out var days) && days > 0 ? days : fallback;
    }
}
