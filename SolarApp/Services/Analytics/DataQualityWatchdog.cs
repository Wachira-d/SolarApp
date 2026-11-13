using SolarApp.Models.Enums;
using SolarApp.Services.Telemetry;

namespace SolarApp.Services.Analytics;

public sealed class ChannelData
{
    /// <summary>Canonical key (DESIGN.md §3); parsed with <see cref="TelemetryKey.Parse"/> to know what to expect.</summary>
    public string Key { get; init; } = "";
    public TimeSeries Series { get; init; } = TimeSeries.Empty;
    /// <summary>The gateway's push interval for this channel; default 60 s.</summary>
    public TimeSpan? ExpectedInterval { get; init; }
}

public enum DataIssueKind { LowCoverage, Gap, Stuck, OutOfRange, CounterReset, FutureTimestamp, NoData }

public sealed record DataQualityIssue(string Key, DataIssueKind Kind, int Count, double? Minutes, string MessageTh);

public sealed record ChannelQuality(string Key, int Samples, int ExpectedSamples, double CoveragePct, int Gaps, double MaxGapMinutes,
    double StuckMinutes, int OutOfRange, int CounterResets, int FutureSamples, double Score, List<DataQualityIssue> Issues);

public sealed class DataQualityReport
{
    public DateOnly Date { get; init; }
    public double Score { get; init; }
    public List<ChannelQuality> Channels { get; init; } = new();
    public List<DataQualityIssue> Issues { get; init; } = new();
}

public sealed class DataQualityOptions
{
    public TimeSpan DefaultInterval { get; init; } = TimeSpan.FromMinutes(1);
    /// <summary>A gap is more than this many intervals without a sample.</summary>
    public int GapIntervals { get; init; } = 3;
    public TimeSpan StuckMinDuration { get; init; } = TimeSpan.FromMinutes(60);
    /// <summary>PV is expected to move between these local hours.</summary>
    public int DaylightFromHour { get; init; } = 8;
    public int DaylightToHour { get; init; } = 16;
    /// <summary>Allowed clock skew before a timestamp counts as "in the future".</summary>
    public TimeSpan FutureTolerance { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Per channel per site-local day: is the data there, and is it believable?
/// Analytics built on a stuck sensor are confidently wrong, so the page
/// shows this score beside every report.
/// </summary>
public static class DataQualityWatchdog
{
    public static DataQualityReport Evaluate(IReadOnlyList<ChannelData> channels, DateOnly day, DateTime nowUtc,
        TimeZoneInfo? timeZone = null, DataQualityOptions? options = null)
    {
        var o = options ?? new DataQualityOptions();
        var tz = LocalClock.Resolve(timeZone);
        var ds = LocalClock.DayStartUtc(day, tz);
        var de = LocalClock.DayStartUtc(day.AddDays(1), tz);
        // Today counts only up to now: a half-finished day is not "50 % coverage".
        var effectiveEnd = nowUtc < de ? (nowUtc > ds ? nowUtc : ds) : de;

        var results = new List<ChannelQuality>();
        foreach (var ch in channels)
        {
            var parsed = TelemetryKey.Parse(ch.Key);
            var interval = ch.ExpectedInterval ?? o.DefaultInterval;
            var issues = new List<DataQualityIssue>();
            var all = ch.Series.Between(ds, de).Points;
            var future = all.Count(p => p.TsUtc > nowUtc + o.FutureTolerance);
            var pts = all.Where(p => p.TsUtc <= nowUtc + o.FutureTolerance).ToList();
            var expected = Math.Max(0, (int)Math.Round((effectiveEnd - ds) / interval));
            var coverage = expected > 0 ? Math.Min(100, pts.Count * 100.0 / expected) : 100;

            // Gaps, including the edges of the day.
            var gapLimit = interval * o.GapIntervals;
            var gaps = 0;
            var maxGap = TimeSpan.Zero;
            var edges = new List<DateTime> { ds };
            edges.AddRange(pts.Select(p => p.TsUtc));
            edges.Add(effectiveEnd);
            for (var i = 1; i < edges.Count; i++)
            {
                var g = edges[i] - edges[i - 1];
                if (g > gapLimit) { gaps++; if (g > maxGap) maxGap = g; }
            }

            // Stuck: an unchanged value for ≥ 60 min while the quantity should move.
            var stuck = TimeSpan.Zero;
            double? stuckValue = null;
            if (ShouldMove(parsed))
            {
                var runStart = 0;
                for (var i = 1; i <= pts.Count; i++)
                {
                    if (i < pts.Count && pts[i].Value == pts[runStart].Value) continue;
                    var last = pts[i - 1];
                    if (i - 1 > runStart)
                    {
                        var moving = MovingDuration(pts[runStart].TsUtc, last.TsUtc, parsed, tz, o);
                        if (moving >= o.StuckMinDuration && moving > stuck) { stuck = moving; stuckValue = pts[runStart].Value; }
                    }
                    runStart = i;
                }
            }

            var range = ValidRange(parsed);
            var outOfRange = range is { } r ? pts.Count(p => p.Value < r.Lo || p.Value > r.Hi) : 0;

            var resets = 0;
            if (parsed.IsCumulative)
                for (var i = 1; i < pts.Count; i++)
                    if (pts[i].Value < pts[i - 1].Value - 1e-6) resets++;

            if (pts.Count == 0 && expected > 0)
                issues.Add(new(ch.Key, DataIssueKind.NoData, 1, (effectiveEnd - ds).TotalMinutes, "ไม่ได้รับข้อมูลเลยทั้งวัน"));
            else if (coverage < 90)
                issues.Add(new(ch.Key, DataIssueKind.LowCoverage, pts.Count, null, $"ได้รับข้อมูล {coverage:0} % ของที่ควรได้ ({pts.Count}/{expected} ค่า)"));
            if (gaps > 0 && pts.Count > 0)
                issues.Add(new(ch.Key, DataIssueKind.Gap, gaps, maxGap.TotalMinutes, $"ข้อมูลขาดหาย {gaps} ช่วง (นานสุด {maxGap.TotalMinutes:0} นาที)"));
            if (stuck > TimeSpan.Zero)
                issues.Add(new(ch.Key, DataIssueKind.Stuck, 1, stuck.TotalMinutes, $"ค่าค้างที่ {stuckValue:0.##} นาน {stuck.TotalMinutes:0} นาที ขณะที่ควรเปลี่ยนแปลง — เซ็นเซอร์/การอ่าน Modbus อาจค้าง"));
            if (outOfRange > 0)
                issues.Add(new(ch.Key, DataIssueKind.OutOfRange, outOfRange, null, $"ค่าผิดช่วง {outOfRange} ครั้ง (ช่วงที่ถูกต้อง {range!.Value.Lo:0}–{range.Value.Hi:0})"));
            if (resets > 0)
                issues.Add(new(ch.Key, DataIssueKind.CounterReset, resets, null, $"ตัวนับสะสมลดลง/รีเซ็ต {resets} ครั้ง"));
            if (future > 0)
                issues.Add(new(ch.Key, DataIssueKind.FutureTimestamp, future, null, $"เวลาของข้อมูลอยู่ในอนาคต {future} ค่า — นาฬิกาเกตเวย์อาจไม่ตรง"));

            var score = 100.0;
            score -= (100 - coverage) * 0.6;
            score -= Math.Min(20, gaps * 5);
            if (stuck > TimeSpan.Zero) score -= 15;
            if (outOfRange > 0) score -= 15;
            if (resets > 0) score -= 10;
            if (future > 0) score -= 10;
            score = Math.Clamp(score, 0, 100);

            results.Add(new ChannelQuality(ch.Key, pts.Count, expected, coverage, gaps, maxGap.TotalMinutes, stuck.TotalMinutes,
                outOfRange, resets, future, score, issues));
        }

        return new DataQualityReport
        {
            Date = day,
            Score = results.Count > 0 ? results.Average(c => c.Score) : 0,
            Channels = results,
            Issues = results.SelectMany(c => c.Issues).ToList(),
        };
    }

    /// <summary>Quantities that are never constant for an hour while running.</summary>
    private static bool ShouldMove(ParsedKey k) =>
        (k.Kind is TelemetryDeviceKind.Pv or TelemetryDeviceKind.PvString && k.Quantity == TelemetryQuantity.Power)
        || (k.Kind is TelemetryDeviceKind.Grid or TelemetryDeviceKind.Load && k.Quantity == TelemetryQuantity.Power);

    /// <summary>How much of a constant run falls where movement is expected (daylight for PV, always otherwise).</summary>
    private static TimeSpan MovingDuration(DateTime from, DateTime to, ParsedKey k, TimeZoneInfo tz, DataQualityOptions o)
    {
        if (k.Kind is not (TelemetryDeviceKind.Pv or TelemetryDeviceKind.PvString)) return to - from;
        var day = LocalClock.LocalDate(from, tz);
        var ls = LocalClock.AtLocalHour(day, o.DaylightFromHour, tz);
        var le = LocalClock.AtLocalHour(day, o.DaylightToHour, tz);
        var s = from > ls ? from : ls;
        var e = to < le ? to : le;
        return e > s ? e - s : TimeSpan.Zero;
    }

    private static (double Lo, double Hi)? ValidRange(ParsedKey k) => k.Quantity switch
    {
        TelemetryQuantity.Soc or TelemetryQuantity.Soh => (0, 100),
        TelemetryQuantity.CellTempMax or TelemetryQuantity.CellTempMin => (-20, 90),
        TelemetryQuantity.Temperature when k.Kind is TelemetryDeviceKind.Battery or TelemetryDeviceKind.BatteryPack => (-20, 90),
        TelemetryQuantity.Temperature => (-30, 120),
        TelemetryQuantity.Frequency => (0, 70),
        TelemetryQuantity.GridPresent => (0, 1),
        _ => null,
    };
}
