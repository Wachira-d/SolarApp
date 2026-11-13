namespace SolarApp.Services.Analytics;

public sealed class InverterThermalInput
{
    public int Index { get; init; }
    public TimeSeries TempC { get; init; } = TimeSeries.Empty;
    public TimeSeries? PowerW { get; init; }
    public double RatingW { get; init; }
}

public sealed class ThermalOptions
{
    /// <summary>Heatsink temperature from which inverters typically start derating.</summary>
    public double InverterHighTempC { get; init; } = 60;
    public double InverterGoodMaxC { get; init; } = 60;
    public double InverterWatchMaxC { get; init; } = 70;
    public double BatteryGoodMaxC { get; init; } = 35;
    public double BatteryWatchMaxC { get; init; } = 45;
    public double BatteryWarningC { get; init; } = 45;
    public double BatteryCriticalC { get; init; } = 50;
    /// <summary>Output must stay this flat (max − min, % of rating) for at least <see cref="FlatMinDuration"/>.</summary>
    public double FlatTolerancePctOfRating { get; init; } = 3;
    public TimeSpan FlatMinDuration { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>Flat at or above this share of rating is clipping at nameplate, not derating.</summary>
    public double BelowRatingFraction { get; init; } = 0.9;
    public double MinOutputFraction { get; init; } = 0.1;
    public TimeSpan MaxGap { get; init; } = TimeSeries.DefaultMaxGap;
    public TimeSpan TempAlign { get; init; } = TimeSpan.FromMinutes(10);
}

/// <param name="Confirmed">True when an expected-PV series showed the sun could have given more.</param>
public sealed record DeratingEvent(int Inverter, DateTime StartUtc, DateTime EndUtc, double Minutes, double AvgTempC,
    double AvgPowerW, double PctOfRating, bool Confirmed);

public sealed record ThermalDevice(string Name, int Index, double? MaxTempC, DateTime? MaxAtUtc, double? AvgTempC, KpiGrade Grade,
    List<DeratingEvent> Derating);

public sealed class ThermalReport
{
    public List<ThermalDevice> Inverters { get; init; } = new();
    public ThermalDevice? Battery { get; set; }
    public List<string> Alerts { get; init; } = new();
}

/// <summary>
/// Inverter and battery temperatures, and thermal derating: a hot inverter
/// whose output sits flat below its rating is throttling itself, which looks
/// like "cloudy" in the kWh total but is fixable (shade, airflow, fan).
/// </summary>
public static class ThermalAnalyzer
{
    public static ThermalReport Analyze(IReadOnlyList<InverterThermalInput> inverters, TimeSeries? batteryTempC = null,
        TimeSeries? expectedPvW = null, ThermalOptions? options = null)
    {
        var o = options ?? new ThermalOptions();
        var report = new ThermalReport();
        foreach (var inv in inverters)
        {
            var (max, at, avg) = Stat(inv.TempC);
            var events = inv.PowerW is { Count: >= 2 } && inv.RatingW > 0 ? Derating(inv, expectedPvW, o) : new List<DeratingEvent>();
            report.Inverters.Add(new ThermalDevice($"inverter{inv.Index}", inv.Index, max, at, avg,
                KpiGrades.LowerIsBetter(max, o.InverterGoodMaxC, o.InverterWatchMaxC), events));
            if (events.Count > 0)
                report.Alerts.Add($"อินเวอร์เตอร์ {inv.Index}: ลดกำลังเพราะความร้อน {events.Count} ครั้ง รวม {events.Sum(e => e.Minutes):0} นาที — ตรวจการระบายอากาศ/พัดลม/แสงแดดส่องตัวเครื่อง");
        }
        if (batteryTempC is { IsEmpty: false } bt)
        {
            var (max, at, avg) = Stat(bt);
            report.Battery = new ThermalDevice("battery", 0, max, at, avg,
                KpiGrades.LowerIsBetter(max, o.BatteryGoodMaxC, o.BatteryWatchMaxC), new List<DeratingEvent>());
            if (max >= o.BatteryCriticalC) report.Alerts.Add($"แบตเตอรี่ร้อนวิกฤต {max:0.0} °C (≥ {o.BatteryCriticalC:0} °C) — ต้องแจ้งช่างทันที");
            else if (max >= o.BatteryWarningC) report.Alerts.Add($"แบตเตอรี่ร้อน {max:0.0} °C (≥ {o.BatteryWarningC:0} °C) — เฝ้าระวัง");
        }
        return report;
    }

    private static (double? Max, DateTime? At, double? Avg) Stat(TimeSeries s)
    {
        if (s.IsEmpty) return (null, null, null);
        var m = s.Points.MaxBy(p => p.Value);
        return (m.Value, m.TsUtc, s.Mean());
    }

    private static List<DeratingEvent> Derating(InverterThermalInput inv, TimeSeries? expected, ThermalOptions o)
    {
        var pts = inv.PowerW!.Points;
        var events = new List<DeratingEvent>();
        var tol = inv.RatingW * o.FlatTolerancePctOfRating / 100;

        bool Hot(Point p) =>
            inv.TempC.ValueAt(p.TsUtc, o.TempAlign) is { } t && t >= o.InverterHighTempC
            && p.Value >= o.MinOutputFraction * inv.RatingW && p.Value < o.BelowRatingFraction * inv.RatingW;

        var i = 0;
        while (i < pts.Count)
        {
            if (!Hot(pts[i])) { i++; continue; }
            var j = i;
            double lo = pts[i].Value, hi = pts[i].Value;
            while (j + 1 < pts.Count && Hot(pts[j + 1]) && pts[j + 1].TsUtc - pts[j].TsUtc <= o.MaxGap
                   && Math.Max(hi, pts[j + 1].Value) - Math.Min(lo, pts[j + 1].Value) <= tol)
            {
                j++;
                lo = Math.Min(lo, pts[j].Value);
                hi = Math.Max(hi, pts[j].Value);
            }
            var dur = pts[j].TsUtc - pts[i].TsUtc;
            if (dur >= o.FlatMinDuration)
            {
                var window = pts.Skip(i).Take(j - i + 1).ToList();
                var avgP = window.Average(p => p.Value);
                var avgT = window.Select(p => inv.TempC.ValueAt(p.TsUtc, o.TempAlign) ?? double.NaN).Where(double.IsFinite).Average();
                var expAvg = expected is null ? null : Stats.Mean(window.Select(p => expected.ValueAt(p.TsUtc, o.TempAlign) ?? double.NaN));
                // With an expectation, the sun must have offered clearly more; without one, report as suspected.
                if (expAvg is null || expAvg > avgP * 1.1)
                    events.Add(new DeratingEvent(inv.Index, pts[i].TsUtc, pts[j].TsUtc, dur.TotalMinutes, avgT, avgP,
                        avgP / inv.RatingW * 100, expAvg is not null));
                i = j + 1;
            }
            else i++;
        }
        return events;
    }
}
