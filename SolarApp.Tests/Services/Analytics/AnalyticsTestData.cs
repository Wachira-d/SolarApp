using SolarApp.Services.Analytics;

namespace SolarApp.Tests.Services.Analytics;

/// <summary>Synthetic-data helpers. Times are built in Thailand local time (UTC+7) and stored as UTC.</summary>
internal static class TD
{
    /// <summary>A fixed UTC+7 zone so tests never depend on the machine's tzdata.</summary>
    public static readonly TimeZoneInfo Ict = TimeZoneInfo.CreateCustomTimeZone("ICT-test", TimeSpan.FromHours(7), "ICT", "ICT");

    public static DateTime Utc(int y, int m, int d, int h = 0, int mi = 0) => new(y, m, d, h, mi, 0, DateTimeKind.Utc);

    /// <summary>Site-local wall clock → UTC.</summary>
    public static DateTime Local(int y, int m, int d, int h = 0, int mi = 0) => Utc(y, m, d, h, mi).AddHours(-7);

    public static TimeSeries Constant(DateTime from, TimeSpan duration, TimeSpan step, double value) =>
        TimeSeries.FromValues(from, step, Enumerable.Repeat(value, (int)(duration / step) + 1));

    public static TimeSeries Series(DateTime start, TimeSpan step, params double[] values) => TimeSeries.FromValues(start, step, values);
}

/// <summary>Builds matching SOC and power series step by step; SOC changes linearly within a segment.</summary>
internal sealed class BatterySim
{
    private readonly List<Point> _soc = new();
    private readonly List<Point> _p = new();
    private readonly TimeSpan _step;

    public DateTime T { get; private set; }
    public double Soc { get; private set; }

    public BatterySim(DateTime startUtc, double soc, double powerW = 0, TimeSpan? step = null)
    {
        _step = step ?? TimeSpan.FromMinutes(1);
        T = startUtc;
        Soc = soc;
        Emit(powerW);
    }

    private void Emit(double p)
    {
        _soc.Add(new Point(T, Soc));
        _p.Add(new Point(T, p));
    }

    /// <summary>Emit samples at T+step … T+steps·step with constant power, SOC reaching socEnd on the last.</summary>
    public BatterySim Run(int steps, double powerW, double socEnd)
    {
        var s0 = Soc;
        for (var k = 1; k <= steps; k++)
        {
            T += _step;
            Soc = s0 + (socEnd - s0) * k / steps;
            Emit(powerW);
        }
        return this;
    }

    public BatterySim Until(DateTime utc, double powerW, double socEnd) => Run((int)Math.Round((utc - T) / _step), powerW, socEnd);

    public TimeSeries SocSeries => new(_soc);
    public TimeSeries SocRounded => new(_soc.Select(p => p with { Value = Math.Round(p.Value) }));
    public TimeSeries PowerSeries => new(_p);

    /// <summary>
    /// The product owner's worked example, from local 18:00 on <paramref name="day"/>:
    /// full battery, 100 % → 25 % overnight delivering <paramref name="dischargeKwh"/>
    /// (22.8 → 30.4 kWh capacity), idle, then a 24 kWh charge back to 98 % by 14:00
    /// and 100 % by 14:10, idle until 18:00.
    /// </summary>
    public static BatterySim WorkedExample(int days = 1, double dischargeKwh = 22.8, TimeSpan? step = null,
        Func<int, double>? dischargeForDay = null, int y = 2026, int m = 1, int d = 5)
    {
        var start = TD.Local(y, m, d, 18);
        var sim = new BatterySim(start, 100, 0, step);
        for (var i = 0; i < days; i++)
        {
            var kwh = dischargeForDay?.Invoke(i) ?? dischargeKwh;
            var dayStart = start.AddDays(i);
            sim.Until(dayStart.AddHours(12), -kwh / 12 * 1000, 25);          // 18:00 → 06:00
            sim.Until(dayStart.AddHours(14), 0, 25);                          // idle to 08:00
            sim.Until(dayStart.AddHours(20), 4000, 98);                       // 24 kWh to 14:00
            sim.Until(dayStart.AddMinutes(20 * 60 + 10), 4000, 100);          // top up to 14:10
            sim.Until(dayStart.AddHours(24), 0, 100);                         // idle to 18:00
        }
        return sim;
    }
}
