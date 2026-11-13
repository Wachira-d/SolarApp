using SolarApp.Models.Enums;

namespace SolarApp.Services.Telemetry;

/// <summary>
/// Pure rollup arithmetic (DESIGN.md §5.3): raw → 5 min → 1 h → 1 day. No
/// database access — TelemetryRollupJob feeds it rows from ITelemetryStore
/// and writes back what it returns, so every rule here is unit-tested.
///
/// <para><b>Ordinary channels</b> (power, SOC, temperature…): Min / Max /
/// Avg / First / Last / Count of the samples in the bucket. Avg is a sample
/// average; parents weight children by Count so an hour with a gap is not
/// skewed by the sparse 5-minute bucket.</para>
///
/// <para><b>Cumulative counters</b> (<c>*_total_kwh</c>,
/// <c>TelemetryChannel.IsCumulative</c>): the energy in a bucket
/// is <c>Last − First</c> at every resolution — use <see cref="Increase"/>.
/// To make that hold:</para>
/// <list type="bullet">
/// <item><c>First</c> is the <i>opening</i> reading — the last good reading
/// before the bucket (within the job's look-back), or the bucket's own first
/// reading when there is none. Using the reading before the bucket is what
/// keeps the step between two buckets from being lost: with one sample a
/// minute, "last − first inside the bucket" would drop a fifth of the
/// energy.</item>
/// <item><c>Last</c> = <c>First</c> + the bridged increase over the bucket.
/// A counter that resets (inverter replaced, meter restarted, a "today"
/// counter at midnight) restarts from ~0, so after a reset the increase
/// counts the new reading itself rather than a negative step. Without a
/// reset <c>Last</c> is simply the closing reading.</item>
/// <item>Parents: <c>First</c> = first child's First, <c>Last</c> = First + Σ
/// child increases, so a day's increase is exactly the sum of its hours'.</item>
/// <item>Min / Max / Avg stay real readings (useful for "meter at end of
/// month" style lookups; use Max, not Last, after a reset).</item>
/// </list>
/// </summary>
public static class TelemetryRollup
{
    public static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A drop no larger than this fraction of the reading (or <see cref="JitterAbsolute"/>)
    /// is read-out jitter, not a reset: the baseline holds, nothing is counted twice when it recovers.
    /// </summary>
    public const double JitterFraction = 0.01;
    public const double JitterAbsolute = 0.05;

    /// <summary>
    /// A drop that comes back to the previous level within this window was a
    /// bad read (HA's Modbus integration reports 0 on a failed poll), not a
    /// reset. Treating it as a reset would count the whole meter reading as
    /// new energy — a day showing 50 000 kWh.
    /// </summary>
    public static readonly TimeSpan GlitchWindow = TimeSpan.FromMinutes(10);

    // ── Bucket boundaries ──────────────────────────────────────────────

    public static DateTime FiveMinuteStart(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % FiveMinutes.Ticks, DateTimeKind.Utc);

    /// <summary>Start (UTC) of the site-local hour containing <paramref name="utc"/>.</summary>
    public static DateTime HourStart(DateTime utc, TimeZoneInfo tz)
    {
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        // Floor in local time, then shift back by the same offset — correct
        // for half-hour zones as well, and needs no local→UTC conversion.
        var offset = tz.GetUtcOffset(utc);
        var local = utc + offset;
        var floored = new DateTime(local.Ticks - local.Ticks % TimeSpan.TicksPerHour);
        return DateTime.SpecifyKind(floored - offset, DateTimeKind.Utc);
    }

    /// <summary>Start (UTC) of the site-local day containing <paramref name="utc"/>: 17:00 UTC the day before, for Bangkok.</summary>
    public static DateTime DayStart(DateTime utc, TimeZoneInfo tz) =>
        LocalDateStartUtc(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz).Date, tz);

    /// <summary>The UTC instant a site-local calendar date begins.</summary>
    public static DateTime LocalDateStartUtc(DateTime localDate, TimeZoneInfo tz)
    {
        var d = DateTime.SpecifyKind(localDate.Date, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(d)) d = d.AddHours(1);   // midnight skipped by a DST jump (not in Thailand)
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(d, tz), DateTimeKind.Utc);
    }

    /// <summary>The site-local date a UTC instant falls on.</summary>
    public static DateTime LocalDate(DateTime utc, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz).Date;

    // ── Counter arithmetic ─────────────────────────────────────────────

    /// <summary>
    /// Energy in a bucket of a cumulative channel (kWh for *_total_kwh). Never negative.
    /// For non-cumulative channels this is just Last − First and means little.
    /// </summary>
    public static double Increase(AggregatePoint p) => Math.Max(0, p.Last - p.First);

    private static double JitterTolerance(double baseline) => Math.Max(Math.Abs(baseline) * JitterFraction, JitterAbsolute);

    /// <summary>
    /// One step of a counter from <paramref name="baseline"/> to <paramref name="value"/>:
    /// the energy it represents and the new baseline. Rising → the difference;
    /// a jitter-sized dip → 0 and the baseline holds; a real drop → a reset,
    /// counted as the new reading (the counter restarted from zero).
    /// </summary>
    public static (double Increase, double Baseline) CounterStep(double baseline, double value)
    {
        if (value >= baseline) return (value - baseline, value);
        if (baseline - value <= JitterTolerance(baseline)) return (0, baseline);
        return (Math.Max(0, value), value);
    }

    /// <summary>Bridged increase across a sequence of readings (e.g. the First/Last pairs of a daily counter's hours).</summary>
    public static double IncreaseOfReadings(IEnumerable<double> readings)
    {
        double? b = null;
        double total = 0;
        foreach (var v in readings)
        {
            if (!double.IsFinite(v)) continue;
            if (b == null) { b = v; continue; }
            var (inc, nb) = CounterStep(b.Value, v);
            total += inc;
            b = nb;
        }
        return total;
    }

    /// <summary>
    /// Flags readings of one counter (sorted by time) that are bad reads: a
    /// large drop that returns to the previous level within <see cref="GlitchWindow"/>.
    /// </summary>
    public static bool[] FindCounterGlitches(IReadOnlyList<RawSample> series)
    {
        var glitch = new bool[series.Count];
        double? b = null;
        for (var i = 0; i < series.Count; i++)
        {
            var v = series[i].Value;
            if (b == null || v >= b.Value - JitterTolerance(b.Value))
            {
                b = b == null ? v : Math.Max(b.Value, v);
                continue;
            }
            // A big drop: a reset, unless the counter is back within the window.
            var recovered = -1;
            for (var j = i + 1; j < series.Count && series[j].TsUtc - series[i].TsUtc <= GlitchWindow; j++)
                if (series[j].Value >= b.Value - JitterTolerance(b.Value)) { recovered = j; break; }
            if (recovered < 0) { b = v; continue; }   // genuine reset
            for (var k = i; k < recovered; k++) glitch[k] = true;
            i = recovered - 1;                        // resume at the recovery sample
        }
        return glitch;
    }

    // ── Raw → 5 minutes ────────────────────────────────────────────────

    /// <summary>
    /// 5-minute aggregates from raw samples. Samples outside
    /// [<paramref name="emitFromUtc"/>, <paramref name="emitToUtc"/>) are context
    /// only: before — the opening reading of counters; after — glitch look-ahead.
    /// No bucket is produced for a 5-minute span without samples.
    /// </summary>
    public static List<AggregatePoint> ToFiveMinutes(IEnumerable<RawSample> samples, Func<int, bool> isCumulative,
        DateTime? emitFromUtc = null, DateTime? emitToUtc = null)
    {
        var from = emitFromUtc ?? DateTime.MinValue;
        var to = emitToUtc ?? DateTime.MaxValue;
        var result = new List<AggregatePoint>();

        foreach (var channel in samples.Where(s => double.IsFinite(s.Value)).GroupBy(s => s.ChannelId))
        {
            var series = channel.OrderBy(s => s.TsUtc).ToList();
            var cumulative = isCumulative(channel.Key);
            var glitch = cumulative ? FindCounterGlitches(series) : new bool[series.Count];

            double? baseline = null;          // counters: bridged reading carried across buckets
            var i = 0;
            while (i < series.Count)
            {
                var bucket = FiveMinuteStart(series[i].TsUtc);
                var emit = bucket >= from && bucket < to;

                double min = double.MaxValue, max = double.MinValue, sum = 0, first = 0, last = 0, increase = 0;
                double? opening = baseline;
                var count = 0;
                for (; i < series.Count && FiveMinuteStart(series[i].TsUtc) == bucket; i++)
                {
                    if (glitch[i]) continue;
                    var v = series[i].Value;
                    if (count == 0) first = v;
                    last = v;
                    min = Math.Min(min, v);
                    max = Math.Max(max, v);
                    sum += v;
                    count++;
                    if (cumulative)
                    {
                        if (baseline == null) { baseline = v; opening = v; continue; }
                        var (inc, nb) = CounterStep(baseline.Value, v);
                        increase += inc;
                        baseline = nb;
                    }
                }
                if (count == 0 || !emit) continue;

                if (cumulative)
                {
                    first = opening!.Value;
                    last = first + increase;
                }
                result.Add(new AggregatePoint(channel.Key, AggregateResolution.FiveMinutes, bucket,
                    min, max, sum / count, first, last, count));
            }
        }
        return result;
    }

    // ── 5 min → 1 h → 1 day ────────────────────────────────────────────

    /// <summary>
    /// Combine child aggregates into <paramref name="target"/> buckets given by
    /// <paramref name="bucketOf"/> (an hour or site-local day start).
    /// </summary>
    public static List<AggregatePoint> RollUp(IEnumerable<AggregatePoint> children, AggregateResolution target,
        Func<DateTime, DateTime> bucketOf, Func<int, bool> isCumulative)
    {
        var result = new List<AggregatePoint>();
        foreach (var g in children.Where(c => c.Count > 0)
                     .GroupBy(c => (c.ChannelId, Bucket: bucketOf(c.BucketStartUtc))))
        {
            var kids = g.OrderBy(c => c.BucketStartUtc).ToList();
            var count = kids.Sum(c => c.Count);
            var avg = kids.Sum(c => c.Avg * c.Count) / count;
            var first = kids[0].First;
            var last = isCumulative(g.Key.ChannelId)
                ? first + kids.Sum(Increase)
                : kids[^1].Last;
            result.Add(new AggregatePoint(g.Key.ChannelId, target, g.Key.Bucket,
                kids.Min(c => c.Min), kids.Max(c => c.Max), avg, first, last, count));
        }
        return result;
    }

    // ── Power integration (for the EnergyData bridge) ──────────────────

    /// <summary>
    /// Energy (kWh) of the positive and the negative part of a power channel (W)
    /// over one bucket of <paramref name="bucketLength"/>. A bucket that is
    /// one-signed is exact (Avg × duration). A mixed bucket (grid flipping
    /// between import and export) is split as if the power sat at Max for a
    /// fraction f of the time and at Min for the rest, with f chosen so the
    /// average matches: positive − negative still equals Avg × duration, and
    /// the split is exact for the on/off pattern zero-export control produces.
    /// </summary>
    public static (double PositiveKwh, double NegativeKwh) SplitEnergyKwh(AggregatePoint p, TimeSpan bucketLength)
    {
        var hours = bucketLength.TotalHours;
        if (p.Count <= 0) return (0, 0);
        if (p.Min >= 0) return (p.Avg * hours / 1000d, 0);
        if (p.Max <= 0) return (0, -p.Avg * hours / 1000d);
        var f = Math.Clamp((p.Avg - p.Min) / (p.Max - p.Min), 0, 1);
        return (f * p.Max * hours / 1000d, (1 - f) * -p.Min * hours / 1000d);
    }

    public static TimeSpan LengthOf(AggregateResolution res) => res switch
    {
        AggregateResolution.FiveMinutes => FiveMinutes,
        AggregateResolution.Hour => TimeSpan.FromHours(1),
        _ => TimeSpan.FromDays(1),
    };
}
