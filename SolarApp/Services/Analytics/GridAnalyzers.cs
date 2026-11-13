namespace SolarApp.Services.Analytics;

// ───────────────────────────── Outages ─────────────────────────────

public sealed class OutageOptions
{
    public TimeSpan MinDuration { get; init; } = TimeSpan.FromMinutes(1);
    public double LowVoltageV { get; init; } = 100;
    /// <summary>SOC at or below this means the battery could not cover any longer.</summary>
    public double BatteryEmptySocPct { get; init; } = 10;
    /// <summary>A reading older than this says nothing about the grid now.</summary>
    public TimeSpan MaxSampleAge { get; init; } = TimeSeries.DefaultMaxGap;
}

public sealed record OutageEvent(DateTime StartUtc, DateTime EndUtc, double DurationMinutes, bool Ongoing,
    double? SocStart, double? SocEnd, double? BatteryKwhSupplied, double? HoursCovered, bool BatteryDepleted);

/// <summary>Grid outages (DESIGN.md §7.5): grid.status = 0 or grid voltage &lt; 100 V for ≥ 1 minute.</summary>
public static class OutageDetector
{
    public static List<OutageEvent> Detect(TimeSeries? gridStatus, TimeSeries? gridVoltage, TimeSeries? soc = null,
        TimeSeries? batteryPowerW = null, OutageOptions? options = null)
    {
        var o = options ?? new OutageOptions();
        var times = (gridStatus?.Points ?? Array.Empty<Point>()).Select(p => p.TsUtc)
            .Concat((gridVoltage?.Points ?? Array.Empty<Point>()).Select(p => p.TsUtc))
            .Distinct().OrderBy(t => t).ToList();
        var events = new List<OutageEvent>();
        if (times.Count == 0) return events;

        bool Down(DateTime t) =>
            gridStatus?.ValueAt(t, o.MaxSampleAge) is <= 0.5 || gridVoltage?.ValueAt(t, o.MaxSampleAge) is { } v && v < o.LowVoltageV;

        DateTime? start = null;
        foreach (var t in times)
        {
            var down = Down(t);
            if (down && start is null) start = t;
            else if (!down && start is { } s)
            {
                Add(s, t, ongoing: false);
                start = null;
            }
        }
        if (start is { } open) Add(open, times[^1], ongoing: true);
        return events;

        void Add(DateTime s, DateTime e, bool ongoing)
        {
            if (e - s < o.MinDuration) return;
            var socStart = soc?.ValueAt(s, o.MaxSampleAge);
            var socEnd = soc?.ValueAt(e, o.MaxSampleAge);
            double? kwh = batteryPowerW is { Count: >= 2 } bp ? bp.IntegrateKwh(s, e, v => v < 0 ? -v : 0) : null;
            double? covered = null;
            var depleted = false;
            if (soc is { IsEmpty: false })
            {
                var empty = soc.Between(s, e.AddTicks(1)).Points.FirstOrDefault(p => p.Value <= o.BatteryEmptySocPct);
                depleted = empty.TsUtc != default;
                covered = ((depleted ? empty.TsUtc : e) - s).TotalHours;
            }
            else if (batteryPowerW is { IsEmpty: false } bpw)
                covered = bpw.DurationWhere(v => v < -50, s, e).TotalHours;
            events.Add(new OutageEvent(s, e, (e - s).TotalMinutes, ongoing, socStart, socEnd, kwh, covered, depleted));
        }
    }
}

// ───────────────────────────── Power quality ─────────────────────────────

public sealed class PowerQualityOptions
{
    public double NominalV { get; init; } = 230;
    public double TolerancePct { get; init; } = 10;
    public double NominalHz { get; init; } = 50;
    public double HzTolerance { get; init; } = 0.5;
    /// <summary>Below this the grid is out, not sagging; outage time is excluded from the percentages.</summary>
    public double OutageBelowV { get; init; } = 100;
    public TimeSpan MaxGap { get; init; } = TimeSeries.DefaultMaxGap;
}

public sealed record HourlyVoltage(DateTime LocalHourStart, DateTime UtcHourStart, double Min, double Avg, double Max, int Samples);

public sealed class PowerQualityReport
{
    public List<HourlyVoltage> Hours { get; init; } = new();
    public double? MinV { get; init; }
    public double? AvgV { get; init; }
    public double? MaxV { get; init; }
    public double? PctTimeVoltageOutside { get; init; }
    public double? PctTimeFrequencyOutside { get; init; }
    public double? MinHz { get; init; }
    public double? MaxHz { get; init; }
    public int SagCount { get; init; }
    public int SwellCount { get; init; }
    public KpiGrade Grade { get; init; }
    public List<string> Notes { get; init; } = new();
}

/// <summary>Grid voltage/frequency statistics (DESIGN.md §7.5). Percentages are time-weighted.</summary>
public static class PowerQualityAnalyzer
{
    public static PowerQualityReport Analyze(TimeSeries? voltage, TimeSeries? frequency = null, TimeZoneInfo? timeZone = null,
        PowerQualityOptions? options = null)
    {
        var o = options ?? new PowerQualityOptions();
        var tz = LocalClock.Resolve(timeZone);
        var lo = o.NominalV * (1 - o.TolerancePct / 100);
        var hi = o.NominalV * (1 + o.TolerancePct / 100);
        var notes = new List<string>();

        var hours = new List<HourlyVoltage>();
        double? pctV = null, minV = null, avgV = null, maxV = null;
        int sags = 0, swells = 0;
        if (voltage is { IsEmpty: false } v)
        {
            var live = v.Points.Where(p => p.Value >= o.OutageBelowV).ToList();
            hours = live.GroupBy(p => { var l = LocalClock.ToLocal(p.TsUtc, tz); return new DateTime(l.Year, l.Month, l.Day, l.Hour, 0, 0); })
                .OrderBy(g => g.Key)
                .Select(g => new HourlyVoltage(g.Key, LocalClock.ToUtc(g.Key, tz), g.Min(p => p.Value), g.Average(p => p.Value), g.Max(p => p.Value), g.Count()))
                .ToList();
            if (live.Count > 0)
            {
                minV = live.Min(p => p.Value);
                avgV = live.Average(p => p.Value);
                maxV = live.Max(p => p.Value);
            }
            var from = v.StartUtc!.Value;
            var to = v.EndUtc!.Value;
            var on = v.DurationWhere(x => x >= o.OutageBelowV, from, to, o.MaxGap);
            var outside = v.DurationWhere(x => x >= o.OutageBelowV && (x < lo || x > hi), from, to, o.MaxGap);
            pctV = on > TimeSpan.Zero ? outside / on * 100 : null;

            var state = 0;   // −1 sag, 0 normal/outage, +1 swell
            foreach (var p in v.Points)
            {
                var s = p.Value < o.OutageBelowV ? 0 : p.Value < lo ? -1 : p.Value > hi ? 1 : 0;
                if (s == -1 && state != -1) sags++;
                if (s == 1 && state != 1) swells++;
                state = s;
            }
        }
        else notes.Add("ไม่มีข้อมูลแรงดันกริด");

        double? pctF = null, minHz = null, maxHz = null;
        if (frequency is { IsEmpty: false } f)
        {
            // 0 Hz readings are an outage, not a frequency excursion.
            var live = f.Points.Where(p => p.Value > 1).ToList();
            if (live.Count > 0) { minHz = live.Min(p => p.Value); maxHz = live.Max(p => p.Value); }
            var on = f.DurationWhere(x => x > 1, f.StartUtc!.Value, f.EndUtc!.Value, o.MaxGap);
            var bad = f.DurationWhere(x => x > 1 && Math.Abs(x - o.NominalHz) > o.HzTolerance, f.StartUtc!.Value, f.EndUtc!.Value, o.MaxGap);
            pctF = on > TimeSpan.Zero ? bad / on * 100 : null;
        }

        var worst = new[] { pctV, pctF }.Where(x => x.HasValue).Select(x => x!.Value).DefaultIfEmpty(double.NaN).Max();
        var grade = double.IsNaN(worst) ? KpiGrade.NotAvailable : KpiGrades.LowerIsBetter(worst, 1, 5);
        if (pctV is > 5) notes.Add($"แรงดันกริดอยู่นอกช่วง {lo:0}–{hi:0} V {pctV:0.0} % ของเวลา — ควรแจ้งการไฟฟ้าตรวจสอบหม้อแปลง");

        return new PowerQualityReport
        {
            Hours = hours,
            MinV = minV,
            AvgV = avgV,
            MaxV = maxV,
            PctTimeVoltageOutside = pctV,
            PctTimeFrequencyOutside = pctF,
            MinHz = minHz,
            MaxHz = maxHz,
            SagCount = sags,
            SwellCount = swells,
            Grade = grade,
            Notes = notes,
        };
    }
}
