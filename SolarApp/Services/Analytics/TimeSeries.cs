using System.Text.Json;
using System.Text.Json.Serialization;

namespace SolarApp.Services.Analytics;

/// <summary>One sample of a channel. Timestamps are always UTC (DESIGN.md §7).</summary>
public readonly record struct Point(DateTime TsUtc, double Value);

/// <summary>A half-open UTC interval [FromUtc, ToUtc).</summary>
public readonly record struct TimeRange(DateTime FromUtc, DateTime ToUtc)
{
    public bool Contains(DateTime tsUtc) => tsUtc >= FromUtc && tsUtc < ToUtc;
    [JsonIgnore] public TimeSpan Duration => ToUtc - FromUtc;
}

/// <summary>
/// An immutable, ascending, de-duplicated series of samples. Every analytic
/// takes these instead of DB rows so the engine is a set of pure functions
/// that can be unit-tested with synthetic data.
/// </summary>
[JsonConverter(typeof(TimeSeriesJsonConverter))]
public sealed class TimeSeries
{
    /// <summary>
    /// Two samples further apart than this are not joined when integrating:
    /// a gateway that was offline for an hour must not be credited with an
    /// hour of whatever power it last reported.
    /// </summary>
    public static readonly TimeSpan DefaultMaxGap = TimeSpan.FromMinutes(15);

    public static TimeSeries Empty { get; } = new(Array.Empty<Point>());

    private readonly Point[] _points;

    public IReadOnlyList<Point> Points => _points;
    public int Count => _points.Length;
    public bool IsEmpty => _points.Length == 0;
    public DateTime? StartUtc => _points.Length > 0 ? _points[0].TsUtc : null;
    public DateTime? EndUtc => _points.Length > 0 ? _points[^1].TsUtc : null;

    /// <summary>
    /// Sorts ascending, drops NaN/∞, and keeps the LAST value given for a
    /// repeated timestamp (a replayed buffer carries the corrected value).
    /// Unspecified-kind timestamps are taken as UTC.
    /// </summary>
    public TimeSeries(IEnumerable<Point>? points)
    {
        if (points is null) { _points = Array.Empty<Point>(); return; }
        var ordered = points
            .Where(p => double.IsFinite(p.Value))
            .Select((p, i) => (P: new Point(AsUtc(p.TsUtc), p.Value), I: i))
            .OrderBy(x => x.P.TsUtc).ThenBy(x => x.I)   // stable: later input wins below
            .Select(x => x.P)
            .ToList();
        var result = new List<Point>(ordered.Count);
        foreach (var p in ordered)
        {
            if (result.Count > 0 && result[^1].TsUtc == p.TsUtc) result[^1] = p;
            else result.Add(p);
        }
        _points = result.ToArray();
    }

    /// <summary>Evenly spaced series — handy for tests and resampled data.</summary>
    public static TimeSeries FromValues(DateTime startUtc, TimeSpan step, IEnumerable<double> values) =>
        new(values.Select((v, i) => new Point(startUtc + step * i, v)));

    public static TimeSeries FromPairs(IEnumerable<(DateTime TsUtc, double Value)> pairs) =>
        new(pairs.Select(p => new Point(p.TsUtc, p.Value)));

    internal static DateTime AsUtc(DateTime t) => t.Kind switch
    {
        DateTimeKind.Utc => t,
        DateTimeKind.Local => t.ToUniversalTime(),
        _ => DateTime.SpecifyKind(t, DateTimeKind.Utc),
    };

    /// <summary>Index of the last sample at or before <paramref name="ts"/>, or −1.</summary>
    public int IndexAtOrBefore(DateTime ts)
    {
        int lo = 0, hi = _points.Length - 1, ans = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (_points[mid].TsUtc <= ts) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }

    /// <summary>Index of the first sample at or after <paramref name="ts"/> (Count if none).</summary>
    public int IndexAtOrAfter(DateTime ts)
    {
        var i = IndexAtOrBefore(ts);
        if (i >= 0 && _points[i].TsUtc == ts) return i;
        return i + 1;
    }

    /// <summary>
    /// Step-hold value: the last sample at or before <paramref name="ts"/>.
    /// Null before the first sample, or when the last sample is older than
    /// <paramref name="maxAge"/> (if given).
    /// </summary>
    public double? ValueAt(DateTime ts, TimeSpan? maxAge = null)
    {
        var i = IndexAtOrBefore(ts);
        if (i < 0) return null;
        if (maxAge is { } age && ts - _points[i].TsUtc > age) return null;
        return _points[i].Value;
    }

    /// <summary>Linear interpolation between neighbours no further apart than maxGap.</summary>
    public double? InterpolateAt(DateTime ts, TimeSpan? maxGap = null)
    {
        var gap = maxGap ?? DefaultMaxGap;
        var i = IndexAtOrBefore(ts);
        if (i < 0) return null;
        var a = _points[i];
        if (a.TsUtc == ts) return a.Value;
        if (i + 1 >= _points.Length) return null;
        var b = _points[i + 1];
        if (b.TsUtc - a.TsUtc > gap) return null;
        return Lerp(a, b, ts);
    }

    /// <summary>Samples in the half-open window [from, to).</summary>
    public TimeSeries Between(DateTime fromUtc, DateTime toUtc)
    {
        var s = IndexAtOrAfter(fromUtc);
        var e = IndexAtOrAfter(toUtc);
        if (s >= e) return Empty;
        return new TimeSeries(_points.AsSpan(s, e - s).ToArray(), trusted: true);
    }

    public TimeSeries Map(Func<double, double> f) => new(_points.Select(p => p with { Value = f(p.Value) }));
    public TimeSeries Where(Func<Point, bool> predicate) => new(_points.Where(predicate).ToArray(), trusted: true);

    public double? Min() => _points.Length == 0 ? null : _points.Min(p => p.Value);
    public double? Max() => _points.Length == 0 ? null : _points.Max(p => p.Value);
    public double? Mean() => _points.Length == 0 ? null : _points.Average(p => p.Value);

    /// <summary>
    /// Energy in kWh, treating values as W: trapezoids between consecutive
    /// samples, clipped to [from, to). Pairs further apart than maxGap are
    /// skipped rather than bridged.
    /// </summary>
    public double IntegrateKwh(DateTime fromUtc, DateTime toUtc, TimeSpan? maxGap = null) =>
        IntegrateKwh(fromUtc, toUtc, v => v, maxGap);

    /// <summary>
    /// As <see cref="IntegrateKwh(DateTime, DateTime, TimeSpan?)"/>, applying
    /// <paramref name="transform"/> to each value first — e.g. v ⇒ max(v, 0)
    /// for charge-only energy of a signed battery power series.
    /// </summary>
    public double IntegrateKwh(DateTime fromUtc, DateTime toUtc, Func<double, double> transform, TimeSpan? maxGap = null)
    {
        if (_points.Length < 2 || toUtc <= fromUtc) return 0;
        var gap = maxGap ?? DefaultMaxGap;
        var start = Math.Max(0, IndexAtOrBefore(fromUtc));
        double wh = 0;
        for (var i = start; i + 1 < _points.Length; i++)
        {
            var a = _points[i];
            var b = _points[i + 1];
            if (a.TsUtc >= toUtc) break;
            if (b.TsUtc <= fromUtc) continue;
            if (b.TsUtc - a.TsUtc > gap) continue;
            var s = a.TsUtc < fromUtc ? fromUtc : a.TsUtc;
            var e = b.TsUtc > toUtc ? toUtc : b.TsUtc;
            var vs = transform(Lerp(a, b, s));
            var ve = transform(Lerp(a, b, e));
            wh += (vs + ve) / 2.0 * (e - s).TotalHours;
        }
        return wh / 1000.0;
    }

    /// <summary>
    /// How long the step-held value satisfied <paramref name="predicate"/>
    /// within [from, to). A sample holds until the next one; across a gap
    /// longer than maxGap it holds for nothing (we do not know).
    /// </summary>
    public TimeSpan DurationWhere(Func<double, bool> predicate, DateTime fromUtc, DateTime toUtc, TimeSpan? maxGap = null)
    {
        if (_points.Length == 0 || toUtc <= fromUtc) return TimeSpan.Zero;
        var gap = maxGap ?? DefaultMaxGap;
        var total = TimeSpan.Zero;
        for (var i = Math.Max(0, IndexAtOrBefore(fromUtc)); i + 1 < _points.Length; i++)
        {
            var a = _points[i];
            var b = _points[i + 1];
            if (a.TsUtc >= toUtc) break;
            if (b.TsUtc - a.TsUtc > gap || !predicate(a.Value)) continue;
            var s = a.TsUtc < fromUtc ? fromUtc : a.TsUtc;
            var e = b.TsUtc > toUtc ? toUtc : b.TsUtc;
            if (e > s) total += e - s;
        }
        return total;
    }

    /// <summary>Largest gap between consecutive samples inside [from, to), or zero.</summary>
    public TimeSpan LargestGap(DateTime fromUtc, DateTime toUtc)
    {
        var max = TimeSpan.Zero;
        var w = Between(fromUtc, toUtc).Points;
        for (var i = 1; i < w.Count; i++)
            if (w[i].TsUtc - w[i - 1].TsUtc > max) max = w[i].TsUtc - w[i - 1].TsUtc;
        return max;
    }

    private static double Lerp(Point a, Point b, DateTime t)
    {
        if (t <= a.TsUtc) return a.Value;
        if (t >= b.TsUtc) return b.Value;
        var f = (t - a.TsUtc).TotalSeconds / (b.TsUtc - a.TsUtc).TotalSeconds;
        return a.Value + (b.Value - a.Value) * f;
    }

    // Already sorted/deduped slice: skip the O(n log n) normalisation.
    private TimeSeries(Point[] points, bool trusted) { _ = trusted; _points = points; }
}

/// <summary>Serialises a series as <c>{"points":[{"tsUtc":…,"value":…}]}</c>.</summary>
public sealed class TimeSeriesJsonConverter : JsonConverter<TimeSeries>
{
    public override TimeSeries? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType == JsonTokenType.StartArray)
            return new TimeSeries(JsonSerializer.Deserialize<List<Point>>(ref reader, options));
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("TimeSeries: object expected");
        List<Point>? points = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            var name = reader.GetString();
            reader.Read();
            if (string.Equals(name, "points", StringComparison.OrdinalIgnoreCase))
                points = JsonSerializer.Deserialize<List<Point>>(ref reader, options);
            else reader.Skip();
        }
        return new TimeSeries(points);
    }

    public override void Write(Utf8JsonWriter writer, TimeSeries value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName("Points") ?? "Points");
        JsonSerializer.Serialize(writer, value.Points, options);
        writer.WriteEndObject();
    }
}

/// <summary>The JSON settings every analytics result is stored with (SiteAnalyticsDaily.Json).</summary>
public static class AnalyticsJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            // NaN must never crash a nightly job; it round-trips as "NaN".
            NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}

/// <summary>Good / Watch / Poor grading used by every KPI.</summary>
public enum KpiGrade
{
    NotAvailable = 0,
    Good = 1,
    Watch = 2,
    Poor = 3,
}

public static class KpiGrades
{
    public static string Thai(KpiGrade g) => g switch
    {
        KpiGrade.Good => "ดี",
        KpiGrade.Watch => "เฝ้าระวัง",
        KpiGrade.Poor => "ต้องแก้ไข",
        _ => "ไม่มีข้อมูล",
    };

    /// <summary>
    /// Grade a "lower is better" value. <paramref name="goodInclusive"/>
    /// distinguishes "≤ 20 good" from "&lt; 50 good"; the watch limit is
    /// always inclusive ("20–50 watch, &gt; 50 poor").
    /// </summary>
    public static KpiGrade LowerIsBetter(double? value, double goodLimit, double watchLimit, bool goodInclusive = true)
    {
        if (value is not { } v || !double.IsFinite(v)) return KpiGrade.NotAvailable;
        var good = goodInclusive ? v <= goodLimit : v < goodLimit;
        if (good) return KpiGrade.Good;
        return v <= watchLimit ? KpiGrade.Watch : KpiGrade.Poor;
    }
}

/// <summary>Small statistics helpers shared by the analyzers.</summary>
internal static class Stats
{
    public static double? Median(IEnumerable<double> values) => Percentile(values, 50);

    /// <summary>Linear-interpolated percentile (p in 0..100); null when empty.</summary>
    public static double? Percentile(IEnumerable<double> values, double p)
    {
        var a = values.Where(double.IsFinite).OrderBy(v => v).ToArray();
        if (a.Length == 0) return null;
        if (a.Length == 1) return a[0];
        var rank = Math.Clamp(p, 0, 100) / 100.0 * (a.Length - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return a[lo] + (a[hi] - a[lo]) * (rank - lo);
    }

    public static double? Mean(IEnumerable<double> values)
    {
        var a = values.Where(double.IsFinite).ToArray();
        return a.Length == 0 ? null : a.Average();
    }

    /// <summary>Least squares y = a·x + b; null when x has no spread.</summary>
    public static (double A, double B, double R2)? LinearFit(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        var n = Math.Min(x.Count, y.Count);
        if (n < 2) return null;
        double mx = 0, my = 0;
        for (var i = 0; i < n; i++) { mx += x[i]; my += y[i]; }
        mx /= n; my /= n;
        double sxx = 0, sxy = 0, syy = 0;
        for (var i = 0; i < n; i++)
        {
            sxx += (x[i] - mx) * (x[i] - mx);
            sxy += (x[i] - mx) * (y[i] - my);
            syy += (y[i] - my) * (y[i] - my);
        }
        if (sxx <= 1e-12) return null;
        var a = sxy / sxx;
        var b = my - a * mx;
        var r2 = syy <= 1e-12 ? 1.0 : sxy * sxy / (sxx * syy);
        return (a, b, r2);
    }
}

/// <summary>
/// Site-local time helpers. Every analyzer takes a TimeZoneInfo instead of
/// reading a static clock so results are reproducible in tests.
/// </summary>
internal static class LocalClock
{
    public static TimeZoneInfo Resolve(TimeZoneInfo? tz) => tz ?? SiteTime.TimeZone;

    public static DateTime ToLocal(DateTime utc, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);

    public static DateTime ToUtc(DateTime local, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz);

    public static DateOnly LocalDate(DateTime utc, TimeZoneInfo tz) => DateOnly.FromDateTime(ToLocal(utc, tz));

    public static DateTime DayStartUtc(DateOnly day, TimeZoneInfo tz) => ToUtc(day.ToDateTime(TimeOnly.MinValue), tz);

    /// <summary>UTC instant of a site-local wall-clock hour on a day (hour may be ≥ 24).</summary>
    public static DateTime AtLocalHour(DateOnly day, double hour, TimeZoneInfo tz) =>
        ToUtc(day.ToDateTime(TimeOnly.MinValue).AddHours(hour), tz);

    /// <summary>Every local date touched by [fromUtc, toUtc].</summary>
    public static IEnumerable<DateOnly> Days(DateTime fromUtc, DateTime toUtc, TimeZoneInfo tz)
    {
        var d = LocalDate(fromUtc, tz);
        var last = LocalDate(toUtc, tz);
        for (; d <= last; d = d.AddDays(1)) yield return d;
    }
}
