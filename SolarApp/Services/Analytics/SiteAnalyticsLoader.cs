using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Models.Telemetry;
using SolarApp.Services.Telemetry;

namespace SolarApp.Services.Analytics;

/// <summary>
/// One site's telemetry for a time window, as analyzer-ready series keyed by
/// canonical channel key (DESIGN.md §3). Built by <see cref="SiteAnalyticsLoader"/>.
/// </summary>
public sealed class SiteTelemetrySet
{
    public Installation Site { get; init; } = null!;
    public SiteCapabilities Capabilities { get; init; } = null!;
    public IReadOnlyList<TelemetryChannel> Channels { get; init; } = Array.Empty<TelemetryChannel>();
    public IReadOnlyDictionary<string, TimeSeries> Series { get; init; } = new Dictionary<string, TimeSeries>();
    public DateTime FromUtc { get; init; }
    public DateTime ToUtc { get; init; }
    public TimeZoneInfo TimeZone { get; init; } = SiteTime.TimeZone;

    /// <summary>The series for a key, or null when the site has no such channel or no data in the window.</summary>
    public TimeSeries? Get(string key) =>
        Series.TryGetValue(key, out var s) && s.Points.Count > 0 ? s : null;

    public bool Has(string key) => Get(key) != null;

    /// <summary>Indexes n present for keys like "pv.string{n}.power_w".</summary>
    public IEnumerable<int> Indexes(TelemetryDeviceKind kind) =>
        Channels.Where(c => c.Kind == kind && c.DeviceIndex > 0).Select(c => c.DeviceIndex).Distinct().OrderBy(i => i);
}

/// <summary>
/// Reads telemetry out of <see cref="ITelemetryStore"/> for the analytics
/// layer. Short windows read raw samples; longer ones read 5-minute
/// aggregates (one point per bucket) and append raw samples after the last
/// closed bucket, so the most recent minutes are never missing while the
/// rollup catches up.
///
/// Which aggregate value represents a bucket depends on the quantity:
/// counters and codes keep the bucket's last reading, "max" channels its
/// maximum, "min" channels its minimum, everything else the mean.
/// </summary>
public class SiteAnalyticsLoader
{
    /// <summary>Up to this window the loader reads raw rows (a day of 1-minute samples per channel is cheap).</summary>
    public static readonly TimeSpan RawWindowLimit = TimeSpan.FromDays(2);

    private readonly ApplicationDbContext _db;
    private readonly ITelemetryStore _store;

    public SiteAnalyticsLoader(ApplicationDbContext db, ITelemetryStore store)
    {
        _db = db;
        _store = store;
    }

    public async Task<SiteTelemetrySet?> LoadAsync(int installationId, DateTime fromUtc, DateTime toUtc,
        Func<TelemetryChannel, bool>? channelFilter = null, bool forceRaw = false, CancellationToken ct = default)
    {
        var site = await _db.Installations.AsNoTracking().FirstOrDefaultAsync(i => i.Id == installationId, ct);
        if (site == null) return null;
        var channels = await _db.TelemetryChannels.AsNoTracking()
            .Where(c => c.InstallationId == installationId).ToListAsync(ct);

        var caps = SiteCapabilities.From(site, channels);
        var wanted = channels.Where(c => !c.Key.StartsWith("ha.", StringComparison.Ordinal)
                                         && (channelFilter == null || channelFilter(c))).ToList();
        var series = await ReadSeriesAsync(wanted, fromUtc, toUtc, forceRaw, ct);

        return new SiteTelemetrySet
        {
            Site = site,
            Capabilities = caps,
            Channels = channels,
            Series = series,
            FromUtc = fromUtc,
            ToUtc = toUtc,
        };
    }

    public async Task<Dictionary<string, TimeSeries>> ReadSeriesAsync(IReadOnlyCollection<TelemetryChannel> channels,
        DateTime fromUtc, DateTime toUtc, bool forceRaw = false, CancellationToken ct = default)
    {
        var result = new Dictionary<string, TimeSeries>(StringComparer.Ordinal);
        if (channels.Count == 0 || toUtc <= fromUtc) return result;

        var ids = channels.Select(c => c.Id).ToList();
        var byId = channels.ToDictionary(c => c.Id);
        var points = ids.ToDictionary(id => id, _ => new List<Point>());

        DateTime rawFrom = fromUtc;
        if (!forceRaw && toUtc - fromUtc > RawWindowLimit)
        {
            var aggs = await _store.ReadAggregatesAsync(ids, AggregateResolution.FiveMinutes, fromUtc, toUtc, ct);
            var lastBucketEnd = DateTime.MinValue;
            foreach (var a in aggs)
            {
                if (!byId.TryGetValue(a.ChannelId, out var ch)) continue;
                points[a.ChannelId].Add(new Point(a.BucketStartUtc, Pick(ch, a)));
                var end = a.BucketStartUtc.AddMinutes(5);
                if (end > lastBucketEnd) lastBucketEnd = end;
            }
            // Raw tail: whatever the rollup has not closed yet (or everything, if it never ran).
            rawFrom = lastBucketEnd > fromUtc ? lastBucketEnd : fromUtc;
        }

        if (rawFrom < toUtc)
        {
            var raw = await _store.ReadRawAsync(ids, rawFrom, toUtc, ct);
            foreach (var r in raw)
                if (points.TryGetValue(r.ChannelId, out var list)) list.Add(new Point(r.TsUtc, r.Value));
        }

        foreach (var (id, list) in points)
        {
            if (list.Count == 0) continue;
            result[byId[id].Key] = new TimeSeries(list.OrderBy(p => p.TsUtc));
        }
        return result;
    }

    /// <summary>The value that stands for a 5-minute bucket of this channel.</summary>
    internal static double Pick(TelemetryChannel ch, AggregatePoint a)
    {
        if (ch.IsCumulative) return a.Last;
        return ch.Quantity switch
        {
            TelemetryQuantity.Status or TelemetryQuantity.AlarmCode => a.Last,
            TelemetryQuantity.CellVoltageMax or TelemetryQuantity.CellTempMax => a.Max,
            TelemetryQuantity.CellVoltageMin or TelemetryQuantity.CellTempMin => a.Min,
            // Any dropout inside the bucket is an outage: keep the minimum (0 = grid absent).
            TelemetryQuantity.GridPresent => a.Min,
            _ => a.Avg,
        };
    }

    /// <summary>
    /// Hourly grid import and export, kWh, for TOU and bill calculations.
    /// Prefers the cumulative meter counters' hourly aggregates (exact across
    /// any sampling rate); falls back to splitting grid.power_w (+import/−export)
    /// per 5-minute bucket, and beyond 5-minute retention to the hourly mean.
    /// </summary>
    public async Task<(TimeSeries Import, TimeSeries Export)> HourlyGridEnergyAsync(int installationId,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var channels = await _db.TelemetryChannels.AsNoTracking()
            .Where(c => c.InstallationId == installationId &&
                        (c.Key == "grid.import_total_kwh" || c.Key == "grid.export_total_kwh" || c.Key == "grid.power_w"))
            .ToListAsync(ct);
        var imp = channels.FirstOrDefault(c => c.Key == "grid.import_total_kwh");
        var exp = channels.FirstOrDefault(c => c.Key == "grid.export_total_kwh");
        var pow = channels.FirstOrDefault(c => c.Key == "grid.power_w");

        if (imp != null)
        {
            var ids = new List<int> { imp.Id };
            if (exp != null) ids.Add(exp.Id);
            var hours = await _store.ReadAggregatesAsync(ids, AggregateResolution.Hour, fromUtc, toUtc, ct);
            var importSeries = CounterHourly(hours.Where(h => h.ChannelId == imp.Id));
            var exportSeries = exp != null ? CounterHourly(hours.Where(h => h.ChannelId == exp.Id)) : TimeSeries.Empty;
            if (importSeries.Points.Count > 0) return (importSeries, exportSeries);
        }

        if (pow == null) return (TimeSeries.Empty, TimeSeries.Empty);
        var five = await _store.ReadAggregatesAsync(new[] { pow.Id }, AggregateResolution.FiveMinutes, fromUtc, toUtc, ct);
        var imports = new SortedDictionary<DateTime, double>();
        var exports = new SortedDictionary<DateTime, double>();
        DateTime? firstFive = five.Count > 0 ? five.Min(f => f.BucketStartUtc) : null;
        foreach (var f in five)
        {
            var hour = TruncHour(f.BucketStartUtc);
            var kwh = f.Avg * (5.0 / 60.0) / 1000.0;
            Add(imports, hour, Math.Max(0, kwh));
            Add(exports, hour, Math.Max(0, -kwh));
        }
        // Older than 5-minute retention: hourly means (import/export inside one hour net out).
        var hourlyTo = firstFive ?? toUtc;
        if (hourlyTo > fromUtc)
        {
            var hrs = await _store.ReadAggregatesAsync(new[] { pow.Id }, AggregateResolution.Hour, fromUtc, hourlyTo, ct);
            foreach (var h in hrs.Where(h => h.BucketStartUtc < hourlyTo))
            {
                var kwh = h.Avg / 1000.0;
                Add(imports, h.BucketStartUtc, Math.Max(0, kwh));
                Add(exports, h.BucketStartUtc, Math.Max(0, -kwh));
            }
        }
        return (TimeSeries.FromPairs(imports.Select(kv => (kv.Key, kv.Value))),
                TimeSeries.FromPairs(exports.Select(kv => (kv.Key, kv.Value))));

        static void Add(SortedDictionary<DateTime, double> d, DateTime k, double v) =>
            d[k] = d.TryGetValue(k, out var x) ? x + v : v;
    }

    /// <summary>
    /// kWh per hour from a counter's hourly aggregates: the step from the
    /// previous hour's last reading to this hour's last reading when the hours
    /// are consecutive, otherwise the increase inside the hour. A step down is a
    /// meter reset: the new reading is the increase since the reset.
    /// </summary>
    internal static TimeSeries CounterHourly(IEnumerable<AggregatePoint> hours)
    {
        var ordered = hours.OrderBy(h => h.BucketStartUtc).ToList();
        var pts = new List<(DateTime, double)>(ordered.Count);
        AggregatePoint? prev = null;
        foreach (var h in ordered)
        {
            double inc;
            if (prev is { } p && h.BucketStartUtc - p.BucketStartUtc == TimeSpan.FromHours(1))
                inc = h.Last >= p.Last ? h.Last - p.Last : Math.Max(0, h.Last);
            else
                inc = h.Last >= h.First ? h.Last - h.First : Math.Max(0, h.Last);
            pts.Add((h.BucketStartUtc, inc));
            prev = h;
        }
        return TimeSeries.FromPairs(pts);
    }

    private static DateTime TruncHour(DateTime t) =>
        new(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc);
}

/// <summary>
/// Turns a <see cref="SiteTelemetrySet"/> into analyzer inputs. Pure — no I/O —
/// so the mapping from channel keys to analyzer fields is unit-tested directly.
/// </summary>
public static class SiteAnalyticsInputs
{
    public static BatteryWarranty? Warranty(Installation site) =>
        site.BatteryWarrantyYears is > 0 || site.BatteryWarrantyThroughputMwh is > 0
            ? new BatteryWarranty(
                site.BatteryWarrantyYears ?? 0,
                (double)(site.BatteryWarrantyThroughputMwh ?? 0),
                (double)(site.BatteryWarrantyMinCapacityPct ?? 70))
            : null;

    /// <summary>
    /// Battery input from battery.* and battery.pack{n}.*. When the site has no
    /// per-pack channels the system is treated as one pack of the total
    /// nominal capacity, so KPIs still render.
    /// </summary>
    public static BatteryInput Battery(SiteTelemetrySet s)
    {
        var site = s.Site;
        var perPack = site.BatteryCapacityKwh is > 0 ? (double)site.BatteryCapacityKwh.Value : 0;
        var packIdx = s.Indexes(TelemetryDeviceKind.BatteryPack).ToList();

        var packs = new List<BatteryPackInput>();
        foreach (var n in packIdx)
        {
            var p = $"battery.pack{n}.";
            packs.Add(new BatteryPackInput
            {
                Index = n,
                NominalCapacityKwh = perPack,
                Soc = s.Get(p + "soc_pct"),
                PowerW = s.Get(p + "power_w"),
                CellVMax = s.Get(p + "cell_v_max"),
                CellVMin = s.Get(p + "cell_v_min"),
                CellTempMaxC = s.Get(p + "cell_temp_max_c"),
                CellTempMinC = s.Get(p + "cell_temp_min_c"),
                Soh = s.Get(p + "soh_pct"),
                VoltageV = s.Get(p + "voltage_v"),
                CurrentA = s.Get(p + "current_a"),
            });
        }

        var soc = s.Get("battery.soc_pct") ?? AverageOf(packs.Select(p => p.Soc));
        var power = s.Get("battery.power_w") ?? SumOf(packs.Select(p => p.PowerW));

        if (packs.Count == 0)
            packs.Add(new BatteryPackInput
            {
                Index = 1,
                NominalCapacityKwh = TotalNominal(site),
                Soc = soc,
                PowerW = power,
                Soh = s.Get("battery.soh_pct"),
                CellTempMaxC = s.Get("battery.temp_c"),
                VoltageV = s.Get("battery.voltage_v"),
                CurrentA = s.Get("battery.current_a"),
            });

        return new BatteryInput
        {
            Soc = soc ?? TimeSeries.Empty,
            PowerW = power ?? TimeSeries.Empty,
            Packs = packs,
            SystemSoh = s.Get("battery.soh_pct"),
            GridPresent = s.Get("grid.status"),
            TemperatureC = s.Get("battery.temp_c"),
            DischargeTotalKwh = s.Get("battery.discharge_total_kwh"),
            NominalCapacityKwh = TotalNominal(site),
            Warranty = Warranty(site),
            InstallDateUtc = site.InstallationDate == default ? null : SiteTime.ToUtc(site.InstallationDate.Date),
            TimeZone = s.TimeZone,
        };
    }

    public static double TotalNominal(Installation site) =>
        (double)(site.BatteryNominalKwhTotal ?? 0);

    public static double InverterRatingW(Installation site)
    {
        // InverterSizeKw is per inverter; without it the PV array size is the best guess.
        return site.InverterSizeKw > 0
            ? (double)site.InverterSizeKw * 1000 * Math.Max(1, site.InverterCount)
            : (double)site.SystemCapacityKw * 1000;
    }

    /// <summary>Lux preferred (Tuya sensor), irradiance otherwise; the second item names the kind for the PV model.</summary>
    public static (TimeSeries? Series, string Kind) Sensor(SiteTelemetrySet s) =>
        s.Get("env.lux") is { } lux ? (lux, "lux")
        : s.Get("env.irradiance_wm2") is { } irr ? (irr, "irradiance")
        : (null, "lux");

    /// <summary>Total PV power: pv.power_w, else the sum of the strings, else of the inverters.</summary>
    public static TimeSeries? PvPower(SiteTelemetrySet s) =>
        s.Get("pv.power_w")
        ?? SumOf(s.Indexes(TelemetryDeviceKind.PvString).Select(n => s.Get($"pv.string{n}.power_w")))
        ?? SumOf(s.Indexes(TelemetryDeviceKind.Inverter).Select(n => s.Get($"inverter{n}.power_w")));

    public static CurtailmentInput Curtailment(SiteTelemetrySet s, ExpectedPvModel? model, DateTime fromUtc, DateTime toUtc) => new()
    {
        PvPowerW = PvPower(s) ?? TimeSeries.Empty,
        GridPowerW = s.Get("grid.power_w") ?? TimeSeries.Empty,
        BatterySoc = s.Get("battery.soc_pct"),
        BatteryPowerW = s.Get("battery.power_w"),
        Model = model,
        Sensor = Sensor(s).Series,
        InverterRatingW = InverterRatingW(s.Site),
        FromUtc = fromUtc,
        ToUtc = toUtc,
        TimeZone = s.TimeZone,
    };

    /// <summary>String inputs; a string with only V and A gets P = V × A.</summary>
    public static List<StringInput> Strings(SiteTelemetrySet s)
    {
        var list = new List<StringInput>();
        foreach (var n in s.Indexes(TelemetryDeviceKind.PvString))
        {
            var p = s.Get($"pv.string{n}.power_w") ?? Product(s.Get($"pv.string{n}.voltage_v"), s.Get($"pv.string{n}.current_a"));
            if (p != null) list.Add(new StringInput { Index = n, PowerW = p });
        }
        return list;
    }

    public static List<InverterThermalInput> Thermal(SiteTelemetrySet s)
    {
        var rating = InverterRatingW(s.Site) / Math.Max(1, s.Site.InverterCount);
        var list = new List<InverterThermalInput>();
        foreach (var n in s.Indexes(TelemetryDeviceKind.Inverter))
        {
            var t = s.Get($"inverter{n}.temp_c");
            if (t == null) continue;
            list.Add(new InverterThermalInput
            {
                Index = n,
                TempC = t,
                PowerW = s.Get($"inverter{n}.power_w") ?? (n == 1 ? PvPower(s) : null),
                RatingW = rating,
            });
        }
        return list;
    }

    /// <summary>Hottest battery temperature across packs (cell max), else the system sensor.</summary>
    public static TimeSeries? BatteryTemp(SiteTelemetrySet s) =>
        MaxOf(s.Indexes(TelemetryDeviceKind.BatteryPack).Select(n => s.Get($"battery.pack{n}.cell_temp_max_c")))
        ?? s.Get("battery.temp_c");

    public static TimeSeries? GridVoltage(SiteTelemetrySet s) =>
        s.Get("grid.voltage_v") ?? s.Get("grid.l1.voltage_v");

    /// <summary>Every analytics-relevant channel, for the data-quality watchdog.</summary>
    public static List<ChannelData> Quality(SiteTelemetrySet s, TimeSpan expectedInterval) =>
        s.Channels
            .Where(c => c.Kind != TelemetryDeviceKind.Other)
            .OrderBy(c => c.Key, StringComparer.Ordinal)
            .Select(c => new ChannelData
            {
                Key = c.Key,
                Series = s.Get(c.Key) ?? TimeSeries.Empty,
                ExpectedInterval = expectedInterval,
            })
            .ToList();

    // ── series helpers ───────────────────────────────────────────────

    /// <summary>Point-wise combination on the union of timestamps; each input is held at its last value (gaps break the hold).</summary>
    internal static TimeSeries? Combine(IEnumerable<TimeSeries?> inputs, Func<List<double>, double> fold)
    {
        var list = inputs.Where(x => x is { Points.Count: > 0 }).Select(x => x!).ToList();
        if (list.Count == 0) return null;
        if (list.Count == 1) return list[0];
        var stamps = list.SelectMany(x => x.Points.Select(p => p.TsUtc)).Distinct().OrderBy(t => t).ToList();
        var pts = new List<(DateTime, double)>(stamps.Count);
        foreach (var t in stamps)
        {
            var vals = new List<double>(list.Count);
            foreach (var x in list)
                if (x.ValueAt(t) is double v) vals.Add(v);
            if (vals.Count == list.Count) pts.Add((t, fold(vals)));
        }
        return pts.Count > 0 ? TimeSeries.FromPairs(pts) : null;
    }

    internal static TimeSeries? SumOf(IEnumerable<TimeSeries?> xs) => Combine(xs, v => v.Sum());
    internal static TimeSeries? AverageOf(IEnumerable<TimeSeries?> xs) => Combine(xs, v => v.Average());
    internal static TimeSeries? MaxOf(IEnumerable<TimeSeries?> xs) => Combine(xs, v => v.Max());
    internal static TimeSeries? Product(TimeSeries? a, TimeSeries? b) =>
        a == null || b == null ? null : Combine(new[] { a, b }, v => v[0] * v[1]);
}
