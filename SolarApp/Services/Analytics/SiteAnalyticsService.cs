using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Models.Telemetry;

namespace SolarApp.Services.Analytics;

/// <summary>Kinds of rows in SiteAnalyticsDaily.</summary>
public static class SiteAnalyticsKinds
{
    /// <summary>Full BatteryReport over <see cref="SiteAnalyticsService.BatteryWindowDays"/>, stored under the run's date.</summary>
    public const string Battery = "battery";
    /// <summary>One BatteryDay per site-local day; kept forever, so the history chart outlives raw retention.</summary>
    public const string BatteryDay = "battery-day";
    public const string PvModel = "pv-model";
    public const string Pv = "pv";
    public const string Curtailment = "curtailment";
    public const string Strings = "strings";
    public const string Grid = "grid";
    public const string Thermal = "thermal";
    /// <summary>DataQualityReport for that date.</summary>
    public const string Quality = "quality";
    public const string Tou = "tou";
}

public sealed record PvSnapshot(List<PvDay> Days, PvPerformanceSummary Summary, DateTime? ModelTrainedToUtc,
    int ModelSamples, string? SensorKind, List<string> Notes);

public sealed record GridSnapshot(List<OutageEvent> Outages, PowerQualityReport? Quality, int WindowDays);

public sealed record TouSnapshot(TouComparison Comparison, TariffConfig Current, TariffConfig Alternative, int HoursOfData);

/// <summary>What one run did for a site — shown after "คำนวณใหม่" and logged by the job.</summary>
public sealed class SiteAnalyticsRunResult
{
    public int InstallationId { get; init; }
    public List<string> Computed { get; } = new();
    public List<string> Skipped { get; } = new();
    public List<string> Errors { get; } = new();
    public override string ToString() =>
        $"site {InstallationId}: computed [{string.Join(", ", Computed)}]" +
        (Skipped.Count > 0 ? $"; skipped [{string.Join("; ", Skipped)}]" : "") +
        (Errors.Count > 0 ? $"; errors [{string.Join("; ", Errors)}]" : "");
}

/// <summary>
/// Computes a site's analytics from telemetry and stores the results as JSON
/// in SiteAnalyticsDaily (DESIGN.md §7–8). Each section runs only when the
/// site has the devices it needs (SiteCapabilities) and fails on its own:
/// a broken string channel never blocks the battery report.
/// </summary>
public class SiteAnalyticsService
{
    public const int BatteryWindowDays = 120;   // 30-day summary + 90-day comparison lag
    public const int ShortWindowDays = 30;
    public const int TouWindowDays = 365;
    public static readonly TimeSpan PvModelMaxAge = TimeSpan.FromDays(7);

    /// <summary>Fallback when the site has no coordinates: the clear-sky shape barely changes across Thailand.</summary>
    public const double FallbackLatitude = 13.75, FallbackLongitude = 100.5;

    private readonly ApplicationDbContext _db;
    private readonly SiteAnalyticsLoader _loader;
    private readonly ILogger<SiteAnalyticsService> _logger;

    public SiteAnalyticsService(ApplicationDbContext db, SiteAnalyticsLoader loader, ILogger<SiteAnalyticsService> logger)
    {
        _db = db;
        _loader = loader;
        _logger = logger;
    }

    public async Task<SiteAnalyticsRunResult> ComputeSiteAsync(int installationId, DateTime nowUtc, CancellationToken ct = default)
    {
        var result = new SiteAnalyticsRunResult { InstallationId = installationId };
        var tz = SiteTime.TimeZone;
        var today = LocalClock.LocalDate(nowUtc, tz);
        var todayKey = today.ToDateTime(TimeOnly.MinValue);

        var longFrom = nowUtc.AddDays(-BatteryWindowDays);
        var set = await _loader.LoadAsync(installationId, longFrom, nowUtc, ct: ct);
        if (set == null) { result.Errors.Add("ไม่พบไซต์"); return result; }
        if (!set.Capabilities.HasTelemetry) { result.Skipped.Add("ไซต์นี้ยังไม่มีข้อมูลจาก Local API"); return result; }

        var caps = set.Capabilities;
        var site = set.Site;
        var shortFrom = nowUtc.AddDays(-ShortWindowDays);
        var pv = SiteAnalyticsInputs.PvPower(set);
        var (sensor, sensorKind) = SiteAnalyticsInputs.Sensor(set);
        var kWp = (double)site.SystemCapacityKw;
        var lat = site.Latitude ?? FallbackLatitude;
        var lon = site.Longitude ?? FallbackLongitude;

        // ── Battery ────────────────────────────────────────────────
        if (!caps.HasBattery) result.Skipped.Add("แบตเตอรี่: ไม่มีแบตเตอรี่ในระบบนี้");
        else if (!set.Has("battery.soc_pct") && !set.Indexes(TelemetryDeviceKind.BatteryPack).Any())
            result.Skipped.Add("แบตเตอรี่: ยังไม่มีช่อง battery.soc_pct");
        else
            await Section(result, SiteAnalyticsKinds.Battery, async () =>
            {
                var report = BatteryAnalyzer.Analyze(SiteAnalyticsInputs.Battery(set), new BatteryOptions { AsOfUtc = nowUtc });
                await UpsertAsync(installationId, todayKey, SiteAnalyticsKinds.Battery, report, nowUtc, ct);
                await UpsertBatteryDaysAsync(installationId, report.Days, nowUtc, ct);
            });

        // ── Expected-PV model (weekly) ─────────────────────────────
        ExpectedPvModel? model = null;
        List<TimeRange> curtailed = new();
        if (caps.HasPv && pv != null && kWp > 0)
        {
            await Section(result, SiteAnalyticsKinds.PvModel, async () =>
            {
                var stored = await LatestRowAsync(installationId, SiteAnalyticsKinds.PvModel, ct);
                if (stored != null && nowUtc - stored.ComputedAt < PvModelMaxAge)
                {
                    model = ExpectedPvModel.FromJson(stored.Json);
                    return;
                }
                // Train, find curtailment with that first model, then retrain without the curtailed periods.
                var first = ExpectedPvModel.Train(pv, kWp, lat, lon, sensor, null, tz, sensorKind);
                if (caps.HasGridMeter)
                    curtailed = CurtailmentDetector.Detect(SiteAnalyticsInputs.Curtailment(set, first, longFrom, nowUtc)).Mask();
                model = ExpectedPvModel.Train(pv, kWp, lat, lon, sensor, curtailed, tz, sensorKind);
                await UpsertRawAsync(installationId, todayKey, SiteAnalyticsKinds.PvModel, model.ToJson(), nowUtc, ct);
            });
        }
        else if (!caps.HasPv || pv == null) result.Skipped.Add("PV: ยังไม่มีช่องกำลังผลิต (pv.power_w หรือสตริง)");
        else result.Skipped.Add("PV: ยังไม่ได้กรอกขนาดระบบ (kWp)");

        // ── Curtailment (zero export) ──────────────────────────────
        if (model != null && caps.HasGridMeter && set.Has("grid.power_w"))
            await Section(result, SiteAnalyticsKinds.Curtailment, async () =>
            {
                var rate = site.ElectricityRatePerKwh > 0 ? (double)site.ElectricityRatePerKwh : 4.5;
                var report = CurtailmentDetector.Detect(SiteAnalyticsInputs.Curtailment(set, model, shortFrom, nowUtc),
                    new CurtailmentOptions { RateThbPerKwh = rate });
                curtailed = report.Mask();
                await UpsertAsync(installationId, todayKey, SiteAnalyticsKinds.Curtailment, report, nowUtc, ct);
            });
        else if (model != null) result.Skipped.Add("การจำกัดการผลิต: ไม่มีมิเตอร์กริด (grid.power_w)");

        // ── PV actual vs expected ──────────────────────────────────
        if (model != null && pv != null)
            await Section(result, SiteAnalyticsKinds.Pv, async () =>
            {
                var days = PvPerformance.Daily(pv, model, DataStart(pv, shortFrom, tz), nowUtc, sensor, set.Get("env.irradiance_wm2"));
                var notes = new List<string>();
                if (site.Latitude == null || site.Longitude == null)
                    notes.Add("ไซต์ยังไม่มีพิกัด ใช้พิกัดกรุงเทพฯ แทนในการคำนวณแสงแดด");
                if (sensor == null) notes.Add("ไม่มีเซ็นเซอร์แสง ค่าคาดหวังอิงสภาพฟ้าใสและสถิติของไซต์เอง");
                var snap = new PvSnapshot(days, PvPerformance.Summarize(days, kWp), model.TrainedToUtc, model.SampleCount,
                    model.SensorKind, notes);
                await UpsertAsync(installationId, todayKey, SiteAnalyticsKinds.Pv, snap, nowUtc, ct);
            });

        // ── Strings ────────────────────────────────────────────────
        var strings = SiteAnalyticsInputs.Strings(set);
        if (strings.Count >= 2)
            await Section(result, SiteAnalyticsKinds.Strings, async () =>
            {
                var sliced = strings.Select(s => new StringInput { Index = s.Index, PowerW = s.PowerW.Between(shortFrom, nowUtc) }).ToList();
                var report = StringComparator.Compare(sliced, kWp, pv?.Between(shortFrom, nowUtc), curtailed, tz);
                await UpsertAsync(installationId, todayKey, SiteAnalyticsKinds.Strings, report, nowUtc, ct);
            });
        else result.Skipped.Add("สตริง: ต้องมีข้อมูลอย่างน้อย 2 สตริง");

        // ── Grid: outages and power quality ────────────────────────
        var voltage = SiteAnalyticsInputs.GridVoltage(set);
        if (caps.HasGridStatus || voltage != null)
            await Section(result, SiteAnalyticsKinds.Grid, async () =>
            {
                var outages = OutageDetector.Detect(set.Get("grid.status"), voltage, set.Get("battery.soc_pct"), set.Get("battery.power_w"));
                PowerQualityReport? pq = voltage != null || set.Has("grid.frequency_hz")
                    ? PowerQualityAnalyzer.Analyze(voltage?.Between(shortFrom, nowUtc), set.Get("grid.frequency_hz")?.Between(shortFrom, nowUtc), tz)
                    : null;
                await UpsertAsync(installationId, todayKey, SiteAnalyticsKinds.Grid,
                    new GridSnapshot(outages, pq, BatteryWindowDays), nowUtc, ct);
            });
        else result.Skipped.Add("ไฟฟ้า: ไม่มีสถานะกริดหรือแรงดันกริด");

        // ── Temperatures ───────────────────────────────────────────
        var inverters = SiteAnalyticsInputs.Thermal(set);
        var batteryTemp = SiteAnalyticsInputs.BatteryTemp(set);
        if (inverters.Count > 0 || batteryTemp != null)
            await Section(result, SiteAnalyticsKinds.Thermal, async () =>
            {
                var sliced = inverters.Select(i => new InverterThermalInput
                {
                    Index = i.Index, TempC = i.TempC.Between(shortFrom, nowUtc),
                    PowerW = i.PowerW?.Between(shortFrom, nowUtc), RatingW = i.RatingW,
                }).ToList();
                var expected = model?.Predict(shortFrom, nowUtc, TimeSpan.FromMinutes(5), sensor);
                var report = ThermalAnalyzer.Analyze(sliced, batteryTemp?.Between(shortFrom, nowUtc), expected);
                await UpsertAsync(installationId, todayKey, SiteAnalyticsKinds.Thermal, report, nowUtc, ct);
            });
        else result.Skipped.Add("อุณหภูมิ: ไม่มีช่องอุณหภูมิอินเวอร์เตอร์หรือแบตเตอรี่");

        // ── Data quality: yesterday (final) and today (so far), from raw samples ──
        await Section(result, SiteAnalyticsKinds.Quality, async () =>
        {
            var yesterday = today.AddDays(-1);
            var rawSet = await _loader.LoadAsync(installationId, LocalClock.DayStartUtc(yesterday, tz), nowUtc, forceRaw: true, ct: ct);
            if (rawSet == null) return;
            var channels = SiteAnalyticsInputs.Quality(rawSet, TimeSpan.FromSeconds(60));
            foreach (var day in new[] { yesterday, today })
            {
                var report = DataQualityWatchdog.Evaluate(channels, day, nowUtc, tz);
                await UpsertAsync(installationId, day.ToDateTime(TimeOnly.MinValue), SiteAnalyticsKinds.Quality, report, nowUtc, ct);
            }
        });

        // ── Bills and TOU what-if ──────────────────────────────────
        if (caps.HasGridMeter)
            await Section(result, SiteAnalyticsKinds.Tou, async () =>
            {
                var (imp, exp) = await _loader.HourlyGridEnergyAsync(installationId, nowUtc.AddDays(-TouWindowDays), nowUtc, ct);
                if (imp.Points.Count == 0) { result.Skipped.Add("ค่าไฟ: ยังไม่มีข้อมูลซื้อไฟรายชั่วโมง"); return; }
                var (current, alternative) = await TariffsForAsync(site, ct);
                var exportRate = site.FeedInTariffRate is > 0 ? (double)site.FeedInTariffRate.Value : (double?)null;
                var cmp = TouAnalyzer.Compare(imp, exp.Points.Count > 0 ? exp : null, current, alternative, exportRate, null, tz);
                await UpsertAsync(installationId, todayKey, SiteAnalyticsKinds.Tou,
                    new TouSnapshot(cmp, current, alternative, imp.Points.Count), nowUtc, ct);
            });
        else result.Skipped.Add("ค่าไฟ & TOU: ไม่มีมิเตอร์กริด");

        return result;
    }

    /// <summary>
    /// Where a daily comparison may start: the window start, or — when data
    /// begins later (a new gateway) — the first full site-local day with data,
    /// so days before the gateway existed are not scored as zero production.
    /// </summary>
    internal static DateTime DataStart(TimeSeries series, DateTime fromUtc, TimeZoneInfo tz)
    {
        if (series.Points.Count == 0) return fromUtc;
        var first = series.Points[0].TsUtc;
        if (first <= fromUtc) return fromUtc;
        var day = LocalClock.LocalDate(first, tz);
        var dayStart = LocalClock.DayStartUtc(day, tz);
        return first - dayStart <= TimeSpan.FromMinutes(15) ? dayStart : LocalClock.DayStartUtc(day.AddDays(1), tz);
    }

    /// <summary>
    /// The site's current plan and the one to compare it with. Without saved
    /// plans: PEA progressive now vs PEA TOU, the question a non-TOU meter owner asks.
    /// </summary>
    public async Task<(TariffConfig Current, TariffConfig Alternative)> TariffsForAsync(Installation site, CancellationToken ct = default)
    {
        var plans = await _db.TariffPlans.AsNoTracking().Where(p => p.InstallationId == site.Id)
            .OrderByDescending(p => p.IsCurrent).ThenBy(p => p.Id).ToListAsync(ct);
        return PickTariffs(plans);
    }

    public static (TariffConfig Current, TariffConfig Alternative) PickTariffs(IReadOnlyList<TariffPlan> plans)
    {
        TariffConfig? Parse(TariffPlan p)
        {
            try { return TariffConfig.FromJson(p.ConfigJson); }
            catch { return null; }
        }
        var configs = plans.Select(p => (Plan: p, Config: Parse(p))).Where(x => x.Config != null).ToList();
        var current = configs.FirstOrDefault(x => x.Plan.IsCurrent).Config ?? TariffPresets.PeaResidentialProgressive();
        var alternative = configs.FirstOrDefault(x => !x.Plan.IsCurrent && x.Config!.Kind != current.Kind).Config
                          ?? configs.FirstOrDefault(x => !x.Plan.IsCurrent).Config
                          ?? (current.Kind == TariffKind.TimeOfUse ? TariffPresets.PeaResidentialProgressive() : TariffPresets.PeaResidentialTou());
        return (current, alternative!);
    }

    // ── reading ──────────────────────────────────────────────────────

    public Task<SiteAnalyticsDaily?> LatestRowAsync(int installationId, string kind, CancellationToken ct = default) =>
        _db.SiteAnalyticsDaily.AsNoTracking()
            .Where(a => a.InstallationId == installationId && a.Kind == kind)
            .OrderByDescending(a => a.Date).ThenByDescending(a => a.ComputedAt)
            .FirstOrDefaultAsync(ct);

    public async Task<(T? Value, DateTime? ComputedAt)> LatestAsync<T>(int installationId, string kind, CancellationToken ct = default)
        where T : class
    {
        var row = await LatestRowAsync(installationId, kind, ct);
        if (row == null) return (null, null);
        try { return (AnalyticsJson.Deserialize<T>(row.Json), row.ComputedAt); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stored {Kind} analytics for site {Id} could not be read", kind, installationId);
            return (null, row.ComputedAt);
        }
    }

    /// <summary>All stored battery days, oldest first — the source of the long history chart.</summary>
    public async Task<List<BatteryDay>> BatteryDaysAsync(int installationId, DateOnly? from = null, CancellationToken ct = default)
    {
        var q = _db.SiteAnalyticsDaily.AsNoTracking()
            .Where(a => a.InstallationId == installationId && a.Kind == SiteAnalyticsKinds.BatteryDay);
        if (from is DateOnly f)
        {
            var fd = f.ToDateTime(TimeOnly.MinValue);
            q = q.Where(a => a.Date >= fd);
        }
        var rows = await q.OrderBy(a => a.Date).Select(a => a.Json).ToListAsync(ct);
        var days = new List<BatteryDay>(rows.Count);
        foreach (var json in rows)
        {
            try { if (AnalyticsJson.Deserialize<BatteryDay>(json) is { } d) days.Add(d); }
            catch { /* skip a corrupt row rather than lose the chart */ }
        }
        return days;
    }

    // ── writing ──────────────────────────────────────────────────────

    private async Task Section(SiteAnalyticsRunResult result, string name, Func<Task> body)
    {
        try
        {
            await body();
            result.Computed.Add(name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Analytics section {Section} failed for site {Id}", name, result.InstallationId);
            result.Errors.Add($"{name}: {ex.Message}");
            _db.ChangeTracker.Clear();
        }
    }

    private Task UpsertAsync<T>(int siteId, DateTime date, string kind, T value, DateTime nowUtc, CancellationToken ct) =>
        UpsertRawAsync(siteId, date, kind, AnalyticsJson.Serialize(value), nowUtc, ct);

    private async Task UpsertRawAsync(int siteId, DateTime date, string kind, string json, DateTime nowUtc, CancellationToken ct)
    {
        var row = await _db.SiteAnalyticsDaily
            .FirstOrDefaultAsync(a => a.InstallationId == siteId && a.Date == date && a.Kind == kind, ct);
        if (row == null)
            _db.SiteAnalyticsDaily.Add(new SiteAnalyticsDaily
            {
                InstallationId = siteId, Date = date, Kind = kind, Json = json, ComputedAt = nowUtc,
            });
        else
        {
            row.Json = json;
            row.ComputedAt = nowUtc;
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Upsert one row per day. A day already stored is replaced only when the
    /// new computation saw at least as much of it — so a day that has aged out
    /// of the raw/5-minute window keeps its complete figures.
    /// </summary>
    private async Task UpsertBatteryDaysAsync(int siteId, IReadOnlyList<BatteryDay> days, DateTime nowUtc, CancellationToken ct)
    {
        if (days.Count == 0) return;
        var first = days.Min(d => d.Date).ToDateTime(TimeOnly.MinValue);
        var existing = await _db.SiteAnalyticsDaily
            .Where(a => a.InstallationId == siteId && a.Kind == SiteAnalyticsKinds.BatteryDay && a.Date >= first)
            .ToDictionaryAsync(a => a.Date, ct);

        // The oldest day in the window is usually partial (the window starts mid-day): never let it overwrite.
        var oldest = days.Min(d => d.Date);
        foreach (var d in days)
        {
            var key = d.Date.ToDateTime(TimeOnly.MinValue);
            var json = AnalyticsJson.Serialize(d);
            if (existing.TryGetValue(key, out var row))
            {
                if (d.Date == oldest) continue;
                row.Json = json;
                row.ComputedAt = nowUtc;
            }
            else
                _db.SiteAnalyticsDaily.Add(new SiteAnalyticsDaily
                {
                    InstallationId = siteId, Date = key, Kind = SiteAnalyticsKinds.BatteryDay, Json = json, ComputedAt = nowUtc,
                });
        }
        await _db.SaveChangesAsync(ct);
    }
}
