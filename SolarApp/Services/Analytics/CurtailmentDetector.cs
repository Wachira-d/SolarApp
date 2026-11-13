namespace SolarApp.Services.Analytics;

public sealed class CurtailmentInput
{
    public TimeSeries PvPowerW { get; init; } = TimeSeries.Empty;
    /// <summary>W, + import / − export.</summary>
    public TimeSeries GridPowerW { get; init; } = TimeSeries.Empty;
    public TimeSeries? BatterySoc { get; init; }
    /// <summary>W, + charge / − discharge.</summary>
    public TimeSeries? BatteryPowerW { get; init; }
    /// <summary>Expected PV power; or give <see cref="Model"/>.</summary>
    public TimeSeries? ExpectedPvW { get; init; }
    public ExpectedPvModel? Model { get; init; }
    /// <summary>Optional lux/irradiance series passed to the model.</summary>
    public TimeSeries? Sensor { get; init; }
    public double InverterRatingW { get; init; }
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
    public TimeZoneInfo? TimeZone { get; init; }
}

public sealed class CurtailmentOptions
{
    public TimeSpan Step { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan MaxSampleAge { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>|export| below this counts as "zero export".</summary>
    public double ExportZeroBandW { get; init; } = 50;
    /// <summary>A small import still counts: zero-export controllers keep a little import as margin.</summary>
    public double ImportNearZeroW { get; init; } = 100;
    public double BatteryFullPct { get; init; } = 98;
    /// <summary>Battery charging less than this is "not accepting charge".</summary>
    public double BatteryChargingW { get; init; } = 100;
    public double PvBelowExpectedFraction { get; init; } = 0.80;
    /// <summary>Ignore steps where little was expected (dawn, dusk, night).</summary>
    public double MinExpectedW { get; init; } = 200;
    public double RateThbPerKwh { get; init; } = 4.5;
    public int TopHours { get; init; } = 3;
}

public sealed record CurtailedInterval(DateTime StartUtc, DateTime EndUtc, double Minutes, double LostKwh);

public sealed record CurtailmentDay(DateOnly Date, double Minutes, double LostKwh, double LostThb);

public sealed record CurtailmentHour(int LocalHour, double LostKwh, double Minutes, string MessageTh);

public sealed class CurtailmentReport
{
    public bool Available { get; init; }
    public double CurtailedMinutes { get; init; }
    public double LostKwh { get; init; }
    public double LostThb { get; init; }
    public double RateThbPerKwh { get; init; }
    public List<CurtailedInterval> Intervals { get; init; } = new();
    public List<CurtailmentDay> Days { get; init; } = new();
    /// <summary>Lost kWh by site-local hour 0..23.</summary>
    public double[] LostKwhByHour { get; init; } = new double[24];
    /// <summary>Best hours to run appliances: where the most PV was thrown away.</summary>
    public List<CurtailmentHour> TopHours { get; init; } = new();
    public List<string> Notes { get; init; } = new();

    /// <summary>The curtailed intervals as a mask for <see cref="ExpectedPvModel.Train"/>.</summary>
    public List<TimeRange> Mask() => Intervals.Select(i => new TimeRange(i.StartUtc, i.EndUtc)).ToList();
}

/// <summary>
/// Zero-export curtailment (DESIGN.md §7.3): nothing exported, nowhere to
/// put energy (battery full or not charging), and PV well below what the sky
/// allows. The lost energy is valued at the import rate — it is what the
/// customer could have used by moving loads into those hours.
/// </summary>
public static class CurtailmentDetector
{
    public static CurtailmentReport Detect(CurtailmentInput input, CurtailmentOptions? options = null)
    {
        var o = options ?? new CurtailmentOptions();
        var tz = LocalClock.Resolve(input.TimeZone);
        if (input.ExpectedPvW is null && input.Model is null)
            return new CurtailmentReport { RateThbPerKwh = o.RateThbPerKwh, Notes = { "ไม่มีค่าผลิตที่คาดหวัง (ต้องฝึกโมเดล PV ก่อน)" } };
        if (input.PvPowerW.IsEmpty || input.GridPowerW.IsEmpty)
            return new CurtailmentReport { RateThbPerKwh = o.RateThbPerKwh, Notes = { "ต้องมีข้อมูลกำลังผลิต PV และมิเตอร์กริด" } };

        var from = input.FromUtc ?? input.PvPowerW.StartUtc!.Value;
        var to = input.ToUtc ?? input.PvPowerW.EndUtc!.Value;
        var hasBattery = input.BatterySoc is { IsEmpty: false } || input.BatteryPowerW is { IsEmpty: false };

        var intervals = new List<CurtailedInterval>();
        var byHour = new double[24];
        var minutesByHour = new double[24];
        var byDay = new SortedDictionary<DateOnly, (double Min, double Kwh)>();
        DateTime? runStart = null;
        DateTime runEnd = default;
        double runKwh = 0;

        void Close()
        {
            if (runStart is null) return;
            intervals.Add(new CurtailedInterval(runStart.Value, runEnd, (runEnd - runStart.Value).TotalMinutes, runKwh));
            runStart = null;
            runKwh = 0;
        }

        for (var t = from; t < to; t += o.Step)
        {
            var pv = input.PvPowerW.ValueAt(t, o.MaxSampleAge);
            var grid = input.GridPowerW.ValueAt(t, o.MaxSampleAge);
            var expected = input.ExpectedPvW?.ValueAt(t, o.MaxSampleAge)
                           ?? input.Model?.PredictW(t, input.Sensor?.ValueAt(t, o.MaxSampleAge));
            var curtailed = pv is { } p && grid is { } g && expected is { } e && e >= o.MinExpectedW
                            && g > -o.ExportZeroBandW && g < o.ImportNearZeroW
                            && BatteryCannotAbsorb(input, t, hasBattery, o)
                            && p < o.PvBelowExpectedFraction * e;
            if (!curtailed) { Close(); continue; }

            var lostKwh = (expected!.Value - pv!.Value) * o.Step.TotalHours / 1000;
            var stepEnd = t + o.Step > to ? to : t + o.Step;
            if (runStart is null) runStart = t;
            runEnd = stepEnd;
            runKwh += lostKwh;

            var local = LocalClock.ToLocal(t, tz);
            byHour[local.Hour] += lostKwh;
            minutesByHour[local.Hour] += o.Step.TotalMinutes;
            var d = DateOnly.FromDateTime(local);
            byDay[d] = byDay.TryGetValue(d, out var cur) ? (cur.Min + o.Step.TotalMinutes, cur.Kwh + lostKwh) : (o.Step.TotalMinutes, lostKwh);
        }
        Close();

        var lost = intervals.Sum(i => i.LostKwh);
        var top = Enumerable.Range(0, 24).Where(h => byHour[h] > 0)
            .OrderByDescending(h => byHour[h]).Take(o.TopHours)
            .Select(h => new CurtailmentHour(h, byHour[h], minutesByHour[h],
                $"ช่วง {h:00}:00–{(h + 1) % 24:00}:00 ไฟจากแผงเหลือทิ้ง {byHour[h]:0.0} kWh — เหมาะเปิดเครื่องใช้ไฟฟ้า (แอร์ เครื่องซักผ้า ปั๊มน้ำ ชาร์จรถ)"))
            .ToList();

        var notes = new List<string>();
        if (!hasBattery) notes.Add("ไม่มีแบตเตอรี่: ใช้เฉพาะเงื่อนไขไม่ส่งออก + PV ต่ำกว่าที่คาด");

        return new CurtailmentReport
        {
            Available = true,
            CurtailedMinutes = intervals.Sum(i => i.Minutes),
            LostKwh = lost,
            LostThb = lost * o.RateThbPerKwh,
            RateThbPerKwh = o.RateThbPerKwh,
            Intervals = intervals,
            Days = byDay.Select(kv => new CurtailmentDay(kv.Key, kv.Value.Min, kv.Value.Kwh, kv.Value.Kwh * o.RateThbPerKwh)).ToList(),
            LostKwhByHour = byHour,
            TopHours = top,
            Notes = notes,
        };
    }

    /// <summary>Battery full, or not taking charge; a site without a battery has nowhere to put the energy.</summary>
    private static bool BatteryCannotAbsorb(CurtailmentInput input, DateTime t, bool hasBattery, CurtailmentOptions o)
    {
        if (!hasBattery) return true;
        var soc = input.BatterySoc?.ValueAt(t, o.MaxSampleAge);
        var bp = input.BatteryPowerW?.ValueAt(t, o.MaxSampleAge);
        if (soc is { } s && s >= o.BatteryFullPct) return true;
        if (bp is { } b && b < o.BatteryChargingW) return true;
        return false;
    }
}
