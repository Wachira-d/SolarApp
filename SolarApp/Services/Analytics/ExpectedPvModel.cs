using System.Text.Json.Serialization;

namespace SolarApp.Services.Analytics;

/// <summary>Per-hour least-squares fit P[W] = A·sensor + B (sensor = lux or W/m²).</summary>
public sealed record SensorFit(double A, double B, int N, double R2);

public sealed class ExpectedPvTrainOptions
{
    /// <summary>Clear-sky GHI below this (dawn/dusk) is ignored: the ratio is mostly noise there.</summary>
    public double MinClearSkyGhi { get; init; } = 50;
    public int MinFitSamples { get; init; } = 12;
    public TimeSpan SensorAlign { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>Ratios above this are bad data (or a wrong kWp) and are clipped.</summary>
    public double MaxRatio { get; init; } = 1.5;
}

/// <summary>
/// What a site should be producing right now (DESIGN.md §7.2). Trained
/// weekly on non-curtailed 5-minute data: per (month, local hour) the median
/// of P ÷ (GHI_clearsky × kWp); where a lux/irradiance sensor exists, a
/// per-hour linear fit on the sensor replaces the clear-sky ratio. The model
/// is plain data so it is stored as JSON (SiteAnalyticsDaily Kind=pv-model).
/// </summary>
public sealed class ExpectedPvModel
{
    /// <summary>Used where a (month, hour) bucket has never been seen: a typical PR.</summary>
    public const double FallbackRatio = 0.75;

    public double CapacityKwp { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public string? TimeZoneId { get; init; }
    /// <summary>Kept beside the id so a model still predicts where the id does not resolve.</summary>
    public double UtcOffsetMinutes { get; init; }
    public DateTime? TrainedFromUtc { get; init; }
    public DateTime? TrainedToUtc { get; init; }
    public int SampleCount { get; init; }
    public int CurtailedSamplesExcluded { get; init; }
    /// <summary>Ratios[month − 1][local hour]; null where no training data.</summary>
    public double?[][] Ratios { get; init; } = NewRatios();
    /// <summary>"lux" or "irradiance" when <see cref="SensorFits"/> are present.</summary>
    public string? SensorKind { get; init; }
    /// <summary>SensorFits[local hour]; null where too few samples.</summary>
    public SensorFit?[] SensorFits { get; init; } = new SensorFit?[24];

    private TimeZoneInfo? _tz;

    [JsonIgnore]
    public TimeZoneInfo TimeZone => _tz ??= ResolveTz();

    private static double?[][] NewRatios() => Enumerable.Range(0, 12).Select(_ => new double?[24]).ToArray();

    public static ExpectedPvModel Train(TimeSeries pvPowerW, double capacityKwp, double latitude, double longitude,
        TimeSeries? sensor = null, IReadOnlyList<TimeRange>? curtailed = null, TimeZoneInfo? timeZone = null,
        string sensorKind = "lux", ExpectedPvTrainOptions? options = null)
    {
        var o = options ?? new ExpectedPvTrainOptions();
        var tz = LocalClock.Resolve(timeZone);
        var buckets = Enumerable.Range(0, 12 * 24).Select(_ => new List<double>()).ToArray();
        var fitX = Enumerable.Range(0, 24).Select(_ => new List<double>()).ToArray();
        var fitY = Enumerable.Range(0, 24).Select(_ => new List<double>()).ToArray();
        var ranges = (curtailed ?? Array.Empty<TimeRange>()).OrderBy(r => r.FromUtc).ToList();
        var ri = 0;
        int used = 0, excluded = 0;

        foreach (var pt in pvPowerW.Points)
        {
            // Sweep the sorted curtailment ranges alongside the sorted samples.
            while (ri < ranges.Count && ranges[ri].ToUtc <= pt.TsUtc) ri++;
            if (ri < ranges.Count && ranges[ri].Contains(pt.TsUtc)) { excluded++; continue; }
            if (capacityKwp <= 0) continue;

            var ghi = ClearSky.Ghi(pt.TsUtc, latitude, longitude);
            if (ghi < o.MinClearSkyGhi) continue;
            var local = LocalClock.ToLocal(pt.TsUtc, tz);
            var ratio = Math.Clamp(Math.Max(0, pt.Value) / (ghi * capacityKwp), 0, o.MaxRatio);
            buckets[(local.Month - 1) * 24 + local.Hour].Add(ratio);
            used++;

            if (sensor?.ValueAt(pt.TsUtc, o.SensorAlign) is { } s)
            {
                fitX[local.Hour].Add(s);
                fitY[local.Hour].Add(Math.Max(0, pt.Value));
            }
        }

        var ratios = NewRatios();
        for (var m = 0; m < 12; m++)
            for (var h = 0; h < 24; h++)
                ratios[m][h] = Stats.Median(buckets[m * 24 + h]);

        SensorFit?[] fits = new SensorFit?[24];
        var anyFit = false;
        for (var h = 0; h < 24; h++)
        {
            if (fitX[h].Count < o.MinFitSamples) continue;
            if (Stats.LinearFit(fitX[h], fitY[h]) is { } f && f.A > 0)
            {
                fits[h] = new SensorFit(f.A, f.B, fitX[h].Count, f.R2);
                anyFit = true;
            }
        }

        return new ExpectedPvModel
        {
            CapacityKwp = capacityKwp,
            Latitude = latitude,
            Longitude = longitude,
            TimeZoneId = tz.Id,
            UtcOffsetMinutes = tz.BaseUtcOffset.TotalMinutes,
            TrainedFromUtc = pvPowerW.StartUtc,
            TrainedToUtc = pvPowerW.EndUtc,
            SampleCount = used,
            CurtailedSamplesExcluded = excluded,
            Ratios = ratios,
            SensorKind = anyFit ? sensorKind : null,
            SensorFits = fits,
        };
    }

    /// <summary>Expected PV power, W. Uses the sensor fit when a sensor value is given and the hour has a fit.</summary>
    public double PredictW(DateTime tsUtc, double? sensorValue = null)
    {
        var local = LocalClock.ToLocal(TimeSeries.AsUtc(tsUtc), TimeZone);
        var cap = CapacityKwp * 1000 * 1.05;   // inverter clipping: never expect more than ~nameplate
        if (sensorValue is { } s && SensorFits.Length > local.Hour && SensorFits[local.Hour] is { } f)
            return Math.Clamp(f.A * s + f.B, 0, cap);

        var ghi = ClearSky.Ghi(tsUtc, Latitude, Longitude);
        if (ghi <= 0) return 0;
        return Math.Clamp(ghi * CapacityKwp * RatioFor(local.Month, local.Hour), 0, cap);
    }

    /// <summary>Alias of <see cref="PredictW"/>: expected PV power at one instant, W.</summary>
    public double Predict(DateTime tsUtc, double? sensorValue = null) => PredictW(tsUtc, sensorValue);

    /// <summary>Expected power every <paramref name="step"/> over [from, to); sensor values step-held if given.</summary>
    public TimeSeries Predict(DateTime fromUtc, DateTime toUtc, TimeSpan step, TimeSeries? sensor = null)
    {
        var pts = new List<Point>();
        for (var t = fromUtc; t < toUtc; t += step)
            pts.Add(new Point(t, PredictW(t, sensor?.ValueAt(t, TimeSpan.FromMinutes(10)))));
        return new TimeSeries(pts);
    }

    /// <summary>The bucket's ratio, else the same hour in the nearest month, else <see cref="FallbackRatio"/>.</summary>
    public double RatioFor(int month, int hour)
    {
        for (var d = 0; d <= 6; d++)
        {
            foreach (var m in new[] { month + d, month - d })
            {
                var mi = ((m - 1) % 12 + 12) % 12;
                if (Ratios.Length > mi && Ratios[mi].Length > hour && Ratios[mi][hour] is { } r) return r;
            }
        }
        return FallbackRatio;
    }

    public string ToJson() => AnalyticsJson.Serialize(this);
    public static ExpectedPvModel FromJson(string json) =>
        AnalyticsJson.Deserialize<ExpectedPvModel>(json) ?? throw new FormatException("Empty PV model JSON");

    private TimeZoneInfo ResolveTz()
    {
        if (!string.IsNullOrEmpty(TimeZoneId))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        var off = TimeSpan.FromMinutes(UtcOffsetMinutes);
        return TimeZoneInfo.CreateCustomTimeZone("pv-model", off, "pv-model", "pv-model");
    }
}

public sealed record PvDay(DateOnly Date, double ActualKwh, double ExpectedKwh, double KwhPerKwp, double ExpectedKwhPerKwp,
    double? PerformanceIndexPct, double InsolationKwhM2, double? PrPct, string PrBasis);

public sealed record PvPerformanceSummary(double ActualKwh, double ExpectedKwh, double KwhPerKwp, double? PerformanceIndexPct,
    double? PrPct, int Days);

/// <summary>Expected vs actual per site-local day, specific yield and performance ratio.</summary>
public static class PvPerformance
{
    /// <param name="irradianceWm2">Measured irradiance; when absent the PR is against clear-sky (a lower bound on cloudy days).</param>
    public static List<PvDay> Daily(TimeSeries actualW, TimeSeries expectedW, double capacityKwp, double latitude, double longitude,
        DateTime fromUtc, DateTime toUtc, TimeZoneInfo? timeZone = null, TimeSeries? irradianceWm2 = null)
    {
        var tz = LocalClock.Resolve(timeZone);
        var days = new List<PvDay>();
        if (toUtc <= fromUtc) return days;
        foreach (var d in LocalClock.Days(fromUtc, toUtc.AddTicks(-1), tz))
        {
            var ds = LocalClock.DayStartUtc(d, tz);
            var de = LocalClock.DayStartUtc(d.AddDays(1), tz);
            if (ds < fromUtc) ds = fromUtc;
            if (de > toUtc) de = toUtc;
            var actual = actualW.IntegrateKwh(ds, de, v => Math.Max(0, v));
            var expected = expectedW.IntegrateKwh(ds, de, v => Math.Max(0, v));
            var measured = irradianceWm2 is { Count: >= 2 } irr && irr.Between(ds, de).Count >= 2;
            var h = measured ? irradianceWm2!.IntegrateKwh(ds, de, v => Math.Max(0, v)) : ClearSky.InsolationKwhM2(ds, de, latitude, longitude);
            days.Add(new PvDay(d, actual, expected,
                capacityKwp > 0 ? actual / capacityKwp : 0,
                capacityKwp > 0 ? expected / capacityKwp : 0,
                expected > 0 ? actual / expected * 100 : null,
                h,
                h > 0 && capacityKwp > 0 ? actual / (capacityKwp * h) * 100 : null,
                measured ? "irradiance" : "clearsky"));
        }
        return days;
    }

    /// <summary>Same, with the expectation drawn from a trained model (5-minute steps).</summary>
    public static List<PvDay> Daily(TimeSeries actualW, ExpectedPvModel model, DateTime fromUtc, DateTime toUtc,
        TimeSeries? sensor = null, TimeSeries? irradianceWm2 = null)
    {
        var expected = model.Predict(fromUtc, toUtc.AddMinutes(5), TimeSpan.FromMinutes(5), sensor);
        return Daily(actualW, expected, model.CapacityKwp, model.Latitude, model.Longitude, fromUtc, toUtc, model.TimeZone, irradianceWm2);
    }

    public static PvPerformanceSummary Summarize(IReadOnlyList<PvDay> days, double capacityKwp)
    {
        var a = days.Sum(d => d.ActualKwh);
        var e = days.Sum(d => d.ExpectedKwh);
        var h = days.Sum(d => d.InsolationKwhM2);
        return new PvPerformanceSummary(a, e, capacityKwp > 0 ? a / capacityKwp : 0,
            e > 0 ? a / e * 100 : null, h > 0 && capacityKwp > 0 ? a / (capacityKwp * h) * 100 : null, days.Count);
    }
}
