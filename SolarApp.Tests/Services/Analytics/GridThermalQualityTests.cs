using FluentAssertions;
using SolarApp.Services.Analytics;
using Xunit;

namespace SolarApp.Tests.Services.Analytics;

public class OutageDetectorTests
{
    private static readonly TimeSpan Min = TimeSpan.FromMinutes(1);
    private static readonly DateTime T0 = TD.Local(2026, 5, 1, 18);

    [Fact]
    public void Grid_status_zero_for_90_minutes_is_one_outage_with_battery_figures()
    {
        // Grid down 19:00–20:30; battery supplies 2 kW, SOC 80 → 70.
        var status = new TimeSeries(Enumerable.Range(0, 240).Select(i => new Point(T0.AddMinutes(i), i is >= 60 and < 150 ? 0 : 1)));
        var sim = new BatterySim(T0, 80, 0).Until(T0.AddHours(1), 0, 80).Until(T0.AddHours(2.5), -2000, 70).Until(T0.AddHours(4), 0, 70);
        var e = OutageDetector.Detect(status, null, sim.SocSeries, sim.PowerSeries).Should().ContainSingle().Subject;
        e.StartUtc.Should().Be(T0.AddHours(1));
        e.EndUtc.Should().Be(T0.AddHours(2.5));
        e.DurationMinutes.Should().Be(90);
        e.SocStart.Should().BeApproximately(80, 0.3);
        e.SocEnd.Should().BeApproximately(70, 1e-9);
        e.BatteryKwhSupplied!.Value.Should().BeApproximately(3.0, 0.05);
        e.HoursCovered.Should().Be(1.5);
        e.BatteryDepleted.Should().BeFalse();
        e.Ongoing.Should().BeFalse();
    }

    [Fact]
    public void Low_voltage_counts_and_short_blips_do_not()
    {
        var t = TimeSpan.FromSeconds(30);
        var v = TimeSeries.FromValues(T0, t, new double[] { 230, 230, 0, 230, 230, 50, 40, 30, 20, 231 });
        var events = OutageDetector.Detect(null, v);
        events.Should().ContainSingle().Which.DurationMinutes.Should().Be(2);   // 30 s blip ignored
    }

    [Fact]
    public void Battery_running_out_limits_hours_covered_and_open_outage_is_ongoing()
    {
        var status = TD.Series(T0, TimeSpan.FromHours(1), 1, 0, 0, 0, 0);
        var soc = TD.Series(T0, TimeSpan.FromHours(1), 30, 30, 20, 10, 10);
        var e = OutageDetector.Detect(status, null, soc, null, new OutageOptions { MaxSampleAge = TimeSpan.FromHours(2) }).Single();
        e.Ongoing.Should().BeTrue();
        e.BatteryDepleted.Should().BeTrue();
        e.HoursCovered.Should().Be(2);
        OutageDetector.Detect(null, null).Should().BeEmpty();
    }
}

public class PowerQualityAnalyzerTests
{
    private static readonly TimeSpan Min = TimeSpan.FromMinutes(1);

    [Fact]
    public void Hourly_stats_time_outside_and_sag_swell_counts()
    {
        var d = TD.Local(2026, 5, 1);
        // 24 h at 230 V, a swell to 260 V 12:00–13:00, a sag to 200 V 15:00–15:30, outage 0 V 20:00–21:00.
        var v = new TimeSeries(Enumerable.Range(0, 24 * 60 + 1).Select(i =>
        {
            var h = i / 60.0;
            var val = h is >= 12 and < 13 ? 260 : h is >= 15 and < 15.5 ? 200 : h is >= 20 and < 21 ? 0 : 230;
            return new Point(d.AddMinutes(i), val);
        }));
        var f = TD.Constant(d, TimeSpan.FromHours(24), Min, 50).Where(p => p.TsUtc < d.AddHours(6) || p.TsUtc >= d.AddHours(7))
            .Points.Concat(TD.Constant(d.AddHours(6), TimeSpan.FromMinutes(59), Min, 50.8).Points);
        var r = PowerQualityAnalyzer.Analyze(v, new TimeSeries(f), TD.Ict);
        r.SwellCount.Should().Be(1);
        r.SagCount.Should().Be(1);
        r.PctTimeVoltageOutside!.Value.Should().BeApproximately(90.0 / (23 * 60) * 100, 0.2);   // outage hour excluded
        r.MaxV.Should().Be(260);
        r.MinV.Should().Be(200);
        r.Hours.Should().HaveCount(24);   // 23 live hours + the 00:00 sample of the next day
        r.Hours.Single(h => h.LocalHourStart.Hour == 12 && h.LocalHourStart.Day == 1).Avg.Should().Be(260);
        r.PctTimeFrequencyOutside!.Value.Should().BeApproximately(100.0 / 24, 0.2);
        r.Grade.Should().Be(KpiGrade.Poor);
    }

    [Fact]
    public void No_data_is_not_available()
    {
        var r = PowerQualityAnalyzer.Analyze(null, null, TD.Ict);
        r.Grade.Should().Be(KpiGrade.NotAvailable);
        r.Notes.Should().NotBeEmpty();
    }
}

public class ThermalAnalyzerTests
{
    private static readonly TimeSpan Min = TimeSpan.FromMinutes(1);
    private static readonly DateTime T0 = TD.Local(2026, 4, 20, 11);

    private static InverterThermalInput Inverter(double temp, double power) => new()
    {
        Index = 1, RatingW = 5000,
        TempC = TD.Constant(T0, TimeSpan.FromMinutes(60), Min, temp),
        PowerW = TD.Constant(T0, TimeSpan.FromMinutes(30), Min, power),
    };

    [Fact]
    public void Hot_inverter_with_flat_output_below_rating_is_derating()
    {
        var r = ThermalAnalyzer.Analyze(new[] { Inverter(66, 4000) });
        var ev = r.Inverters.Single().Derating.Should().ContainSingle().Subject;
        ev.Minutes.Should().Be(30);
        ev.PctOfRating.Should().Be(80);
        ev.Confirmed.Should().BeFalse();
        r.Inverters[0].Grade.Should().Be(KpiGrade.Watch);
        r.Alerts.Should().ContainSingle(a => a.Contains("ความร้อน"));
    }

    [Fact]
    public void Cool_inverter_clipping_at_rating_or_sun_limited_is_not_derating()
    {
        ThermalAnalyzer.Analyze(new[] { Inverter(50, 4000) }).Inverters[0].Derating.Should().BeEmpty();
        ThermalAnalyzer.Analyze(new[] { Inverter(66, 4900) }).Inverters[0].Derating.Should().BeEmpty();   // ≥ 90 % = clipping
        var sunLimited = TD.Constant(T0, TimeSpan.FromMinutes(30), Min, 4100);
        ThermalAnalyzer.Analyze(new[] { Inverter(66, 4000) }, expectedPvW: sunLimited).Inverters[0].Derating.Should().BeEmpty();
        var sunny = TD.Constant(T0, TimeSpan.FromMinutes(30), Min, 6000);
        ThermalAnalyzer.Analyze(new[] { Inverter(66, 4000) }, expectedPvW: sunny).Inverters[0].Derating.Single().Confirmed.Should().BeTrue();
    }

    [Fact]
    public void Battery_temperature_alerts()
    {
        ThermalAnalyzer.Analyze(Array.Empty<InverterThermalInput>(), TD.Series(T0, Min, 30, 52)).Alerts.Should().ContainSingle(a => a.Contains("วิกฤต"));
        var warm = ThermalAnalyzer.Analyze(Array.Empty<InverterThermalInput>(), TD.Series(T0, Min, 30, 46));
        warm.Alerts.Should().ContainSingle(a => a.Contains("เฝ้าระวัง"));
        warm.Battery!.Grade.Should().Be(KpiGrade.Poor);
        ThermalAnalyzer.Analyze(Array.Empty<InverterThermalInput>()).Battery.Should().BeNull();
    }
}

public class DataQualityWatchdogTests
{
    private static readonly DateOnly Day = new(2026, 5, 1);
    private static readonly DateTime D0 = TD.Local(2026, 5, 1);
    private static readonly DateTime After = TD.Local(2026, 5, 2, 12);
    private static readonly TimeSpan Min = TimeSpan.FromMinutes(1);

    private static TimeSeries Pv(Func<int, double> f) => new(Enumerable.Range(0, 1440).Select(i => new Point(D0.AddMinutes(i), f(i))));

    private static double SolarShape(int minute) => minute is > 360 and < 1080 ? 3000 + 1000 * Math.Sin(minute / 30.0) : 0;

    [Fact]
    public void Complete_moving_channel_scores_100()
    {
        var r = DataQualityWatchdog.Evaluate(new[] { new ChannelData { Key = "pv.power_w", Series = Pv(SolarShape) } }, Day, After, TD.Ict);
        var c = r.Channels.Single();
        c.CoveragePct.Should().Be(100);
        c.Gaps.Should().Be(0);
        c.Score.Should().Be(100);
        r.Score.Should().Be(100);
        r.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Half_coverage_and_gaps_reduce_score()
    {
        var half = Pv(SolarShape).Where(p => p.TsUtc < D0.AddHours(12));
        var c = DataQualityWatchdog.Evaluate(new[] { new ChannelData { Key = "pv.power_w", Series = half } }, Day, After, TD.Ict).Channels.Single();
        c.CoveragePct.Should().BeApproximately(50, 0.1);
        c.Gaps.Should().Be(1);
        c.MaxGapMinutes.Should().BeApproximately(12 * 60 + 1, 1);
        c.Score.Should().BeLessThan(75);
        c.Issues.Select(i => i.Kind).Should().Contain(new[] { DataIssueKind.LowCoverage, DataIssueKind.Gap });
        c.Issues.Should().OnlyContain(i => i.MessageTh.Length > 0);
    }

    [Fact]
    public void Stuck_pv_in_daylight_is_flagged_but_flat_night_is_not()
    {
        var stuck = Pv(i => i is >= 600 and < 720 ? 2500 : SolarShape(i));   // 10:00–12:00 frozen
        var c = DataQualityWatchdog.Evaluate(new[] { new ChannelData { Key = "pv.string1.power_w", Series = stuck } }, Day, After, TD.Ict).Channels.Single();
        c.StuckMinutes.Should().BeApproximately(119, 1);
        c.Issues.Should().Contain(i => i.Kind == DataIssueKind.Stuck && i.MessageTh.Contains("ค้าง"));

        var night = DataQualityWatchdog.Evaluate(new[] { new ChannelData { Key = "pv.power_w", Series = Pv(SolarShape) } }, Day, After, TD.Ict);
        night.Channels.Single().StuckMinutes.Should().Be(0);   // 0 W all night is expected
        // SOC does not have to move.
        DataQualityWatchdog.Evaluate(new[] { new ChannelData { Key = "battery.soc_pct", Series = Pv(_ => 100) } }, Day, After, TD.Ict)
            .Channels.Single().StuckMinutes.Should().Be(0);
    }

    [Fact]
    public void Out_of_range_counter_reset_and_future_timestamps()
    {
        var soc = Pv(i => i == 100 ? 120 : 50);
        var temp = Pv(i => i == 5 ? -40 : 30);
        var counter = Pv(i => i < 700 ? 1000 + i : i - 700);
        var now = D0.AddHours(12);
        var r = DataQualityWatchdog.Evaluate(new[]
        {
            new ChannelData { Key = "battery.soc_pct", Series = soc },
            new ChannelData { Key = "battery.pack1.cell_temp_max_c", Series = temp },
            new ChannelData { Key = "grid.import_total_kwh", Series = counter },
        }, Day, now, TD.Ict);
        r.Channels[0].OutOfRange.Should().Be(1);
        r.Channels[1].OutOfRange.Should().Be(1);
        r.Channels[2].CounterResets.Should().Be(1);
        r.Channels[0].FutureSamples.Should().Be(1440 - 12 * 60 - 1 - 5);   // beyond now + 5 min
        r.Channels[0].CoveragePct.Should().Be(100);   // today: expected samples only up to now
        r.Issues.Select(i => i.Kind).Should().Contain(new[] { DataIssueKind.OutOfRange, DataIssueKind.CounterReset, DataIssueKind.FutureTimestamp });
        r.Score.Should().BeLessThan(100);
    }

    [Fact]
    public void Empty_channel_has_no_data_issue_and_report_round_trips()
    {
        var r = DataQualityWatchdog.Evaluate(new[] { new ChannelData { Key = "load.power_w", Series = TimeSeries.Empty, ExpectedInterval = TimeSpan.FromMinutes(5) } }, Day, After, TD.Ict);
        var c = r.Channels.Single();
        c.ExpectedSamples.Should().Be(288);
        c.CoveragePct.Should().Be(0);
        c.Score.Should().Be(35);   // −60 coverage, −5 for the day-long gap
        c.Issues.Should().Contain(i => i.Kind == DataIssueKind.NoData);
        var json = AnalyticsJson.Serialize(r);
        AnalyticsJson.Serialize(AnalyticsJson.Deserialize<DataQualityReport>(json)).Should().Be(json);
        DataQualityWatchdog.Evaluate(Array.Empty<ChannelData>(), Day, After, TD.Ict).Score.Should().Be(0);
    }
}
