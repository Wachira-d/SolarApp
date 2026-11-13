using FluentAssertions;
using SolarApp.Services.Analytics;
using Xunit;

namespace SolarApp.Tests.Services.Analytics;

public class BatteryAnalyzerTests
{
    private const double Cap = 30.72;   // 3 × 10.24 kWh
    private static readonly TimeSpan Min = TimeSpan.FromMinutes(1);

    private static BatteryInput Input(BatterySim sim, Action<BatteryInputBuilder>? more = null)
    {
        var b = new BatteryInputBuilder { Soc = sim.SocSeries, Power = sim.PowerSeries };
        more?.Invoke(b);
        return b.Build();
    }

    internal sealed class BatteryInputBuilder
    {
        public TimeSeries Soc = TimeSeries.Empty, Power = TimeSeries.Empty;
        public List<BatteryPackInput> Packs = new();
        public TimeSeries? Soh, Grid, Temp;
        public BatteryWarranty? Warranty;
        public DateTime? Install;
        public double Capacity = Cap;
        public BatteryInput Build() => new()
        {
            Soc = Soc, PowerW = Power, Packs = Packs, SystemSoh = Soh, GridPresent = Grid, TemperatureC = Temp,
            NominalCapacityKwh = Capacity, Warranty = Warranty, InstallDateUtc = Install, TimeZone = TD.Ict,
        };
    }

    // ───────────── Worked example: 100 % → 25 % delivering 22.8 kWh ─────────────

    [Fact]
    public void Worked_example_capacity_is_30_4_kwh_99_percent()
    {
        var r = BatteryAnalyzer.Analyze(Input(BatterySim.WorkedExample()));
        var m = r.Capacity.Measurements.Should().ContainSingle().Subject;
        m.CapacityKwh.Should().BeApproximately(30.4, 0.01);
        Math.Round(m.CapacityKwh, 1).Should().Be(30.4);
        Math.Round(m.PctOfNominal).Should().Be(99);
        m.SocEnd.Should().Be(25);
        m.ChargedInsideKwh.Should().BeLessThan(0.2);
        r.Capacity.MedianKwh.Should().BeApproximately(30.4, 0.01);
        r.Capacity.PerCycleErrorPct.Should().Be("±3–6");
        r.Capacity.MedianErrorPct.Should().Be("±1–2");
        r.Capacity.Notes.Should().Contain(n => n.Contains("จำนวนเต็ม"));
    }

    [Fact]
    public void Worked_example_with_integer_soc_stays_within_stated_error()
    {
        var sim = BatterySim.WorkedExample();
        var r = BatteryAnalyzer.Analyze(new BatteryInput { Soc = sim.SocRounded, PowerW = sim.PowerSeries, NominalCapacityKwh = Cap, TimeZone = TD.Ict });
        var m = r.Capacity.Measurements.Should().ContainSingle().Subject;
        m.CapacityKwh.Should().BeApproximately(30.4, 30.4 * 0.03);
        m.QuantizationErrorPct.Should().BeInRange(2, 6);
    }

    [Fact]
    public void Worked_example_is_one_deep_full_cycle()
    {
        var r = BatteryAnalyzer.Analyze(Input(BatterySim.WorkedExample()));
        r.Cycles.Deep.Should().Be(1);
        r.Cycles.Mid.Should().Be(0);
        r.Cycles.Shallow.Should().Be(0);
        var c = r.Cycles.Items.Single();
        c.MinSoc.Should().Be(25);
        c.StartSoc.Should().Be(100);
        c.DodPct.Should().Be(75);
    }

    [Fact]
    public void Worked_example_sessions_efc_and_round_trip()
    {
        var r = BatteryAnalyzer.Analyze(Input(BatterySim.WorkedExample()));
        r.Sessions.DischargeCount.Should().Be(1);
        r.Sessions.ChargeCount.Should().Be(1);
        r.Sessions.DischargeKwh.Should().BeApproximately(22.8, 0.05);
        r.TotalDischargedKwh.Should().BeApproximately(22.8, 1e-6);
        r.Efc.Should().BeApproximately(22.8 / Cap, 1e-6);

        var rt = r.Efficiency.Items.Should().ContainSingle().Subject;
        rt.EfficiencyPct!.Value.Should().BeApproximately(22.8 / 23.9667 * 100, 0.1);
        rt.Grade.Should().Be(EfficiencyGrade.Good);
        r.Efficiency.Grade.Should().Be(EfficiencyGrade.Good);
    }

    [Fact]
    public void Worked_example_overnight_discharge_text()
    {
        var r = BatteryAnalyzer.Analyze(Input(BatterySim.WorkedExample()));
        var d = r.Days.First(x => x.Date == new DateOnly(2026, 1, 5));
        d.OvernightFromSoc.Should().Be(100);
        d.OvernightToSoc.Should().Be(25);
        d.OvernightKwh!.Value.Should().BeApproximately(22.8, 0.02);
        d.OvernightText.Should().Be("100 % → 25 %, 22.8 kWh");
    }

    // ───────────── Sessions ─────────────

    [Fact]
    public void Zero_export_noon_oscillation_is_not_a_session()
    {
        var t0 = TD.Local(2026, 1, 5, 11);
        var flip = TimeSeries.FromValues(t0, Min, Enumerable.Range(0, 120).Select(i => i % 2 == 0 ? 800.0 : -800.0));
        BatteryAnalyzer.FindSessions(flip).Should().BeEmpty();
        var flip2 = TimeSeries.FromValues(t0, Min, Enumerable.Range(0, 120).Select(i => i / 2 % 2 == 0 ? 800.0 : -800.0));
        BatteryAnalyzer.FindSessions(flip2).Should().BeEmpty();
    }

    [Fact]
    public void Session_needs_more_than_three_minutes_above_300_w()
    {
        var t0 = TD.Local(2026, 1, 5, 20);
        BatteryAnalyzer.FindSessions(TD.Series(t0, Min, 0, -1000, -1000, -1000, 0, 0)).Should().BeEmpty();        // 3 min
        BatteryAnalyzer.FindSessions(TD.Series(t0, Min, 0, -1000, -1000, -1000, -1000, 0, 0)).Should().ContainSingle()   // 4 min
            .Which.Direction.Should().Be(SessionDirection.Discharge);
        BatteryAnalyzer.FindSessions(TD.Series(t0, Min, 0, -250, -250, -250, -250, -250, 0)).Should().BeEmpty();     // below 300 W
    }

    [Fact]
    public void Pauses_shorter_than_seven_minutes_are_merged()
    {
        var t0 = TD.Local(2026, 1, 5, 20);
        double[] Build(int pause) => Enumerable.Repeat(-1000.0, 10).Concat(Enumerable.Repeat(0.0, pause)).Concat(Enumerable.Repeat(-1000.0, 10)).Append(0).ToArray();
        BatteryAnalyzer.FindSessions(TD.Series(t0, Min, Build(5))).Should().ContainSingle();
        BatteryAnalyzer.FindSessions(TD.Series(t0, Min, Build(8))).Should().HaveCount(2);
        // Configurable, but clamped to 5–10 min.
        new BatteryOptions { SessionMergeGap = TimeSpan.FromMinutes(2) }.SessionMergeGap.Should().Be(TimeSpan.FromMinutes(5));
        new BatteryOptions { SessionMergeGap = TimeSpan.FromMinutes(20) }.SessionMergeGap.Should().Be(TimeSpan.FromMinutes(10));
        BatteryAnalyzer.FindSessions(TD.Series(t0, Min, Build(8)), new BatteryOptions { SessionMergeGap = TimeSpan.FromMinutes(10) })
            .Should().ContainSingle();
    }

    [Fact]
    public void Opposite_direction_session_is_not_merged_across()
    {
        var t0 = TD.Local(2026, 1, 5, 12);
        var v = Enumerable.Repeat(-1000.0, 10).Concat(Enumerable.Repeat(1000.0, 5)).Concat(Enumerable.Repeat(-1000.0, 10)).Append(0).ToArray();
        var s = BatteryAnalyzer.FindSessions(TD.Series(t0, Min, v));
        s.Select(x => x.Direction).Should().Equal(SessionDirection.Discharge, SessionDirection.Charge, SessionDirection.Discharge);
    }

    // ───────────── Full cycles ─────────────

    [Fact]
    public void Full_cycles_are_graded_deep_mid_shallow_by_minimum_between_fulls()
    {
        var t0 = TD.Local(2026, 2, 1);
        var soc = TD.Series(t0, TimeSpan.FromHours(1), 100, 70, 50, 80, 98, 85, 70, 99, 60, 25, 40, 99, 90, 31, 100);
        var r = BatteryAnalyzer.Analyze(new BatteryInput { Soc = soc, NominalCapacityKwh = Cap, TimeZone = TD.Ict });
        r.Cycles.Items.Select(c => c.Depth).Should().Equal(CycleDepth.Mid, CycleDepth.Shallow, CycleDepth.Deep, CycleDepth.Mid);
        r.Cycles.Items.Select(c => c.MinSoc).Should().Equal(50, 70, 25, 31);
        (r.Cycles.Deep, r.Cycles.Mid, r.Cycles.Shallow).Should().Be((1, 2, 1));
    }

    [Fact]
    public void Staying_at_full_is_not_a_cycle_and_unfinished_cycle_is_not_counted()
    {
        var t0 = TD.Local(2026, 2, 1);
        var r = BatteryAnalyzer.Analyze(new BatteryInput { Soc = TD.Series(t0, TimeSpan.FromHours(1), 100, 99, 100, 98, 100, 40, 30), NominalCapacityKwh = Cap, TimeZone = TD.Ict });
        r.Cycles.Items.Should().BeEmpty();
    }

    [Fact]
    public void Report_includes_rainflow_histogram_of_soc()
    {
        var t0 = TD.Local(2026, 2, 1);
        var r = BatteryAnalyzer.Analyze(new BatteryInput { Soc = TD.Series(t0, TimeSpan.FromHours(1), 100, 40, 45, 25, 100), NominalCapacityKwh = Cap, TimeZone = TD.Ict });
        r.DodHistogram.Bins[7].Cycles.Should().Be(1);
        r.DodHistogram.Bins[0].Cycles.Should().Be(1);
    }

    // ───────────── EFC ─────────────

    [Fact]
    public void Efc_system_and_per_pack()
    {
        var sim = BatterySim.WorkedExample();
        var packs = Enumerable.Range(1, 3).Select(i => new BatteryPackInput
        {
            Index = i, NominalCapacityKwh = 10.24, PowerW = sim.PowerSeries.Map(v => v / 3), Soc = sim.SocSeries,
        }).ToList();
        var r = BatteryAnalyzer.Analyze(Input(sim, b => { b.Packs = packs; b.Capacity = 0; }));
        r.NominalCapacityKwh.Should().BeApproximately(30.72, 1e-9);   // sum of packs
        r.Efc.Should().BeApproximately(22.8 / 30.72, 1e-6);
        r.Packs.Should().HaveCount(3).And.OnlyContain(p => Math.Abs(p.Efc!.Value - 7.6 / 10.24) < 1e-6);
        r.Packs[0].Capacity!.Measurements.Should().ContainSingle().Which.CapacityKwh.Should().BeApproximately(10.1333, 0.01);
    }

    // ───────────── Measured capacity filters and summary ─────────────

    [Fact]
    public void Capacity_requires_start_within_30_minutes_of_full()
    {
        var t0 = TD.Local(2026, 1, 5, 14);
        // Below 98 % from ~14:14; the discharge starts at 15:01, more than 30 min later.
        var sim = new BatterySim(t0, 100, 0).Until(t0.AddMinutes(20), 0, 97)
            .Until(t0.AddMinutes(60), 0, 97)
            .Until(t0.AddHours(12), -1900, 25).Until(t0.AddHours(13), 0, 25);
        var r = BatteryAnalyzer.Analyze(Input(sim));
        r.Capacity.Measurements.Should().BeEmpty();
    }

    [Fact]
    public void Capacity_rejects_small_delta_soc_charging_inside_and_hot_battery()
    {
        var t0 = TD.Local(2026, 1, 5, 18);
        // Only 40 points.
        var small = new BatterySim(t0, 100, 0).Until(t0.AddHours(6), -2000, 60).Until(t0.AddHours(7), 0, 60);
        BatteryAnalyzer.Analyze(Input(small)).Capacity.Rejected.Should().ContainKey("delta_soc_too_small");

        // A 30-minute 2 kW charge in the middle ends the segment before 50 points.
        var charged = new BatterySim(t0, 100, 0).Until(t0.AddHours(4), -2000, 75).Until(t0.AddHours(4.5), 2000, 78)
            .Until(t0.AddHours(10), -2000, 30).Until(t0.AddHours(11), 0, 30);
        BatteryAnalyzer.Analyze(Input(charged)).Capacity.Measurements.Should().BeEmpty();

        // Worked example but the cells are at 45 °C.
        var sim = BatterySim.WorkedExample();
        var hot = TD.Constant(sim.SocSeries.StartUtc!.Value, TimeSpan.FromHours(24), TimeSpan.FromMinutes(5), 45);
        var r = BatteryAnalyzer.Analyze(Input(sim, b => b.Temp = hot));
        r.Capacity.Measurements.Should().BeEmpty();
        r.Capacity.Rejected.Should().ContainKey("temperature_out_of_range");
    }

    [Fact]
    public void Capacity_summary_median_trend_vs_90_days_and_soh_gap_flag()
    {
        // 130 days; capacity 30.4 kWh for the first 60, then 29.0 kWh.
        var sim = BatterySim.WorkedExample(days: 130, step: TimeSpan.FromMinutes(5), dischargeForDay: i => i < 60 ? 22.8 : 29.0 * 0.75);
        var soh = TD.Constant(sim.SocSeries.StartUtc!.Value, TimeSpan.FromDays(131), TimeSpan.FromDays(1), 100);
        var r = BatteryAnalyzer.Analyze(Input(sim, b => b.Soh = soh));
        var c = r.Capacity;
        c.WindowCount.Should().BeInRange(29, 31);
        c.MedianKwh!.Value.Should().BeApproximately(29.0, 0.02);
        c.PriorMedianKwh!.Value.Should().BeApproximately(30.4, 0.02);
        c.ChangeKwh!.Value.Should().BeApproximately(-1.4, 0.05);
        c.Trend.Should().Be("▼");
        c.SpreadKwh!.Value.Should().BeApproximately(0, 0.01);
        c.BmsSohPct.Should().Be(100);
        c.GapToSohPts!.Value.Should().BeApproximately(29.0 / 30.72 * 100 - 100, 0.1);   // ≈ −5.6
        c.SohGapFlag.Should().BeTrue();
    }

    [Fact]
    public void No_soh_gap_flag_when_bms_agrees()
    {
        var sim = BatterySim.WorkedExample();
        var soh = TD.Constant(sim.SocSeries.StartUtc!.Value, TimeSpan.FromDays(2), TimeSpan.FromHours(1), 98);
        var r = BatteryAnalyzer.Analyze(Input(sim, b => b.Soh = soh));
        r.Capacity.GapToSohPts!.Value.Should().BeApproximately(0.96, 0.1);
        r.Capacity.SohGapFlag.Should().BeFalse();
    }

    // ───────────── Round-trip efficiency ─────────────

    [Fact]
    public void Round_trip_over_100_percent_is_a_data_error_not_efficiency()
    {
        var t0 = TD.Local(2026, 1, 5, 18);
        var sim = new BatterySim(t0, 100, 0).Until(t0.AddHours(12), -2000, 25).Until(t0.AddHours(14), 0, 25)
            .Until(t0.AddHours(18), 4000, 98).Until(t0.AddHours(19), 0, 100);   // 16 kWh in, 24 kWh out
        var r = BatteryAnalyzer.Analyze(Input(sim));
        var rt = r.Efficiency.Items.Should().ContainSingle().Subject;
        rt.Grade.Should().Be(EfficiencyGrade.DataError);
        rt.EfficiencyPct.Should().BeGreaterThan(100);
        r.Efficiency.MedianPct.Should().BeNull();
    }

    [Fact]
    public void Round_trip_across_a_data_gap_is_a_data_error()
    {
        var sim = BatterySim.WorkedExample();
        var gappy = sim.PowerSeries.Where(p => p.TsUtc < TD.Local(2026, 1, 5, 22) || p.TsUtc > TD.Local(2026, 1, 6, 1));
        var r = BatteryAnalyzer.Analyze(new BatteryInput { Soc = sim.SocSeries, PowerW = gappy, NominalCapacityKwh = Cap, TimeZone = TD.Ict });
        r.Efficiency.Items.Should().ContainSingle().Which.Grade.Should().Be(EfficiencyGrade.DataError);
    }

    [Theory]
    [InlineData(95, EfficiencyGrade.Good)]
    [InlineData(92, EfficiencyGrade.Good)]
    [InlineData(90, EfficiencyGrade.Watch)]
    [InlineData(88, EfficiencyGrade.Watch)]
    [InlineData(87.9, EfficiencyGrade.Poor)]
    [InlineData(101, EfficiencyGrade.DataError)]
    public void Efficiency_grades(double pct, EfficiencyGrade g) => BatteryAnalyzer.GradeEfficiency(pct).Should().Be(g);

    // ───────────── Daily metrics ─────────────

    [Fact]
    public void Daily_heat_and_full_flag_daytime_full_hours_and_low_soc_hours()
    {
        var d0 = TD.Local(2026, 4, 10, 0);
        var sim = new BatterySim(d0, 15, 0)
            .Until(TD.Local(2026, 4, 10, 3), 0, 15)        // 3 h below 20 %
            .Until(TD.Local(2026, 4, 10, 8), 3000, 100)
            .Until(TD.Local(2026, 4, 10, 16), 0, 100)      // 8 h at 100 %
            .Until(TD.Local(2026, 4, 10, 23, 59), -1000, 60);
        TimeSeries Temp(double c) => TD.Constant(d0, TimeSpan.FromHours(24), TimeSpan.FromMinutes(5), c);
        var pack = new BatteryPackInput { Index = 1, NominalCapacityKwh = 10.24, CellTempMaxC = Temp(38), CellTempMinC = Temp(34) };
        var day = BatteryAnalyzer.Analyze(Input(sim, b => b.Packs = new() { pack })).Days.Single(x => x.Date == new DateOnly(2026, 4, 10));
        day.HoursFullDaytime.Should().BeApproximately(8.1, 0.1);   // ≥ 99.5 % from ~07:58 until the 16:00 sample
        day.HoursAtFull.Should().BeGreaterThan(5);
        day.HoursBelowLow.Should().BeApproximately(3.3, 0.05);   // 3 h at 15 % + ~18 min ramping to 20 %
        day.CellTempMaxC.Should().Be(38);
        day.CellTempMinC.Should().Be(34);
        day.HeatAndFull.Should().BeTrue();
        day.SocMax.Should().Be(100);
        day.SocMin.Should().Be(15);
        day.DodPct.Should().Be(85);
        day.CRateMax!.Value.Should().BeApproximately(3000 / 30720.0, 1e-6);
        day.CRateOk.Should().BeTrue();

        var cool = new BatteryPackInput { Index = 1, NominalCapacityKwh = 10.24, CellTempMaxC = Temp(33) };
        BatteryAnalyzer.Analyze(Input(sim, b => b.Packs = new() { cool })).Days.Single(x => x.Date == new DateOnly(2026, 4, 10))
            .HeatAndFull.Should().BeFalse();
    }

    [Fact]
    public void Daily_charged_discharged_efc_and_outage_marker()
    {
        var sim = BatterySim.WorkedExample();
        var grid = new TimeSeries(new[]
        {
            new Point(TD.Local(2026, 1, 6, 0), 1), new Point(TD.Local(2026, 1, 6, 2), 0), new Point(TD.Local(2026, 1, 6, 3), 1),
        });
        var r = BatteryAnalyzer.Analyze(Input(sim, b => b.Grid = grid));
        var d6 = r.Days.Single(d => d.Date == new DateOnly(2026, 1, 6));
        d6.ChargedKwh.Should().BeApproximately(23.97 + 0.667, 0.05);
        d6.DischargedKwh.Should().BeApproximately(1.9 * 6 + 0.0158, 0.02);   // 00:00–06:00 part of the night
        d6.EfcToday.Should().BeApproximately(d6.DischargedKwh / Cap, 1e-9);
        d6.HadOutage.Should().BeTrue();
        r.Days.Single(d => d.Date == new DateOnly(2026, 1, 5)).HadOutage.Should().BeFalse();
        r.History.Should().HaveCount(r.Days.Count);
        r.History.Single(h => h.Date == new DateOnly(2026, 1, 6)).HadOutage.Should().BeTrue();
    }

    // ───────────── Health KPIs ─────────────

    private static BatteryPackInput PackWithCells(DateTime t0, double restDvMv, double fullDvMv, double tMax = 30, double tMin = 28)
    {
        // 2 h at rest (SOC 50, P = 0) then 2 h full (SOC 100), 1-min samples.
        var vmax = new List<Point>();
        var vmin = new List<Point>();
        var soc = new List<Point>();
        var pw = new List<Point>();
        for (var i = 0; i < 240; i++)
        {
            var t = t0.AddMinutes(i);
            var full = i >= 120;
            var dv = (full ? fullDvMv : restDvMv) / 1000;
            vmax.Add(new(t, 3.30 + dv));
            vmin.Add(new(t, 3.30));
            soc.Add(new(t, full ? 100 : 50));
            pw.Add(new(t, 0));
        }
        return new BatteryPackInput
        {
            Index = 1, NominalCapacityKwh = 10.24, CellVMax = new(vmax), CellVMin = new(vmin), Soc = new(soc), PowerW = new(pw),
            CellTempMaxC = TD.Constant(t0, TimeSpan.FromHours(4), Min, tMax), CellTempMinC = TD.Constant(t0, TimeSpan.FromHours(4), Min, tMin),
        };
    }

    private static HealthKpi Kpi(BatteryReport r, string key, int pack = 1) => r.Packs.Single(p => p.Index == pack).Kpis.Single(k => k.Key == key);

    [Theory]
    [InlineData(15, KpiGrade.Good)]
    [InlineData(35, KpiGrade.Watch)]
    [InlineData(60, KpiGrade.Poor)]
    public void Cell_dv_at_rest_grades(double mv, KpiGrade g)
    {
        var t0 = TD.Local(2026, 3, 1, 1);
        var r = BatteryAnalyzer.Analyze(new BatteryInput { Soc = TD.Constant(t0, TimeSpan.FromHours(4), Min, 50), Packs = new[] { PackWithCells(t0, mv, 40) }, NominalCapacityKwh = Cap, TimeZone = TD.Ict });
        var k = Kpi(r, "cell_dv_rest");
        k.Value!.Value.Should().BeApproximately(mv, 0.01);
        k.Grade.Should().Be(g);
    }

    [Theory]
    [InlineData(40, KpiGrade.Good)]
    [InlineData(100, KpiGrade.Watch)]
    [InlineData(200, KpiGrade.Poor)]
    public void Cell_dv_at_full_grades(double mv, KpiGrade g)
    {
        var t0 = TD.Local(2026, 3, 1, 1);
        var r = BatteryAnalyzer.Analyze(new BatteryInput { Soc = TD.Constant(t0, TimeSpan.FromHours(4), Min, 50), Packs = new[] { PackWithCells(t0, 10, mv) }, NominalCapacityKwh = Cap, TimeZone = TD.Ict });
        Kpi(r, "cell_dv_full").Grade.Should().Be(g);
    }

    [Fact]
    public void Cell_voltage_given_in_millivolts_is_detected()
    {
        var t0 = TD.Local(2026, 3, 1, 1);
        var p = PackWithCells(t0, 15, 40);
        var mv = new BatteryPackInput
        {
            Index = 1, NominalCapacityKwh = 10.24, Soc = p.Soc, PowerW = p.PowerW,
            CellVMax = p.CellVMax!.Map(v => v * 1000), CellVMin = p.CellVMin!.Map(v => v * 1000),
        };
        var r = BatteryAnalyzer.Analyze(new BatteryInput { Soc = p.Soc!, Packs = new[] { mv }, NominalCapacityKwh = Cap, TimeZone = TD.Ict });
        Kpi(r, "cell_dv_rest").Value!.Value.Should().BeApproximately(15, 0.01);
    }

    [Theory]
    [InlineData(33, 31, KpiGrade.Good, KpiGrade.Good)]
    [InlineData(40, 34, KpiGrade.Watch, KpiGrade.Watch)]
    [InlineData(47, 38, KpiGrade.Poor, KpiGrade.Poor)]
    public void Temperature_kpis(double tMax, double tMin, KpiGrade maxGrade, KpiGrade spreadGrade)
    {
        var t0 = TD.Local(2026, 3, 1, 1);
        var r = BatteryAnalyzer.Analyze(new BatteryInput { Soc = TD.Constant(t0, TimeSpan.FromHours(4), Min, 50), Packs = new[] { PackWithCells(t0, 10, 30, tMax, tMin) }, NominalCapacityKwh = Cap, TimeZone = TD.Ict });
        Kpi(r, "cell_temp_max").Grade.Should().Be(maxGrade);
        Kpi(r, "cell_temp_spread").Grade.Should().Be(spreadGrade);
    }

    [Fact]
    public void Temperature_alerts_at_45_warning_and_50_critical_and_grade_C()
    {
        var t0 = TD.Local(2026, 3, 1, 1);
        var warm = BatteryAnalyzer.Analyze(new BatteryInput { Soc = TD.Constant(t0, TimeSpan.FromHours(4), Min, 50), Packs = new[] { PackWithCells(t0, 10, 30, 46, 44) }, NominalCapacityKwh = Cap, TimeZone = TD.Ict });
        warm.Alerts.Should().ContainSingle().Which.Level.Should().Be("warning");

        var hot = BatteryAnalyzer.Analyze(new BatteryInput { Soc = TD.Constant(t0, TimeSpan.FromHours(4), Min, 50), Packs = new[] { PackWithCells(t0, 10, 30, 52, 50) }, NominalCapacityKwh = Cap, TimeZone = TD.Ict });
        hot.Alerts.Should().Contain(a => a.Level == "critical");
        hot.Overall.Grade.Should().Be("C");
        hot.Overall.Label.Should().Be("ต้องแจ้งช่าง");
        hot.Overall.Reasons.Should().Contain(x => x.Contains("°C"));
    }

    [Fact]
    public void Soh_trend_per_year_over_90_days()
    {
        var t0 = TD.Local(2026, 1, 1);
        var soh = new TimeSeries(Enumerable.Range(0, 101).Select(d => new Point(t0.AddDays(d), 98 - d / 100.0)));   // −1 pt / 100 days
        var pack = new BatteryPackInput { Index = 1, NominalCapacityKwh = 10.24, Soh = soh };
        var r = BatteryAnalyzer.Analyze(new BatteryInput { Soc = TD.Constant(t0, TimeSpan.FromDays(100), TimeSpan.FromHours(1), 50), Packs = new[] { pack }, NominalCapacityKwh = Cap, TimeZone = TD.Ict });
        var k = Kpi(r, "soh_trend");
        k.Value!.Value.Should().BeApproximately(0.9 * 365 / 90, 0.01);   // 3.65 pts / year
        k.Grade.Should().Be(KpiGrade.Watch);

        var shortSoh = BatteryAnalyzer.Analyze(new BatteryInput { Soc = TD.Constant(t0, TimeSpan.FromDays(10), TimeSpan.FromHours(1), 50), Packs = new[] { new BatteryPackInput { Index = 1, Soh = soh.Between(t0, t0.AddDays(10)) } }, NominalCapacityKwh = Cap, TimeZone = TD.Ict });
        Kpi(shortSoh, "soh_trend").Grade.Should().Be(KpiGrade.NotAvailable);
    }

    [Fact]
    public void Pack_vs_mean_of_packs_on_discharged_kwh()
    {
        var t0 = TD.Local(2026, 3, 1, 19);
        BatteryPackInput P(int i, double w) => new() { Index = i, NominalCapacityKwh = 10.24, PowerW = TD.Constant(t0, TimeSpan.FromHours(5), Min, w) };
        var r = BatteryAnalyzer.Analyze(new BatteryInput
        {
            Soc = TD.Constant(t0, TimeSpan.FromHours(5), Min, 60), PowerW = TD.Constant(t0, TimeSpan.FromHours(5), Min, -2940),
            Packs = new[] { P(1, -1000), P(2, -1000), P(3, -940) }, NominalCapacityKwh = Cap, TimeZone = TD.Ict,
        });
        Kpi(r, "pack_vs_packs", 1).Value!.Value.Should().BeApproximately(20.0 / 980 * 100, 0.01);
        Kpi(r, "pack_vs_packs", 1).Grade.Should().Be(KpiGrade.Good);
        Kpi(r, "pack_vs_packs", 3).Grade.Should().Be(KpiGrade.Watch);
    }

    [Fact]
    public void Internal_resistance_vs_first_month_or_not_available()
    {
        var t0 = TD.Local(2026, 1, 1, 12);
        List<Point> cur = new(), volt = new();
        void Steps(DateTime at, double rOhm)
        {
            for (var i = 0; i < 10; i++)
            {
                var amps = i % 2 == 0 ? 0 : 20;
                cur.Add(new(at.AddMinutes(i), amps));
                volt.Add(new(at.AddMinutes(i), 52 + amps * rOhm));
            }
        }
        Steps(t0.AddDays(1), 0.050);
        Steps(t0.AddDays(70), 0.065);   // +30 %
        var pack = new BatteryPackInput { Index = 1, NominalCapacityKwh = 10.24, CurrentA = new(cur), VoltageV = new(volt) };
        var soc = TD.Constant(t0, TimeSpan.FromDays(71), TimeSpan.FromHours(1), 50);
        var r = BatteryAnalyzer.Analyze(new BatteryInput { Soc = soc, Packs = new[] { pack }, NominalCapacityKwh = Cap, TimeZone = TD.Ict });
        var k = Kpi(r, "internal_resistance");
        k.Value!.Value.Should().BeApproximately(30, 0.5);
        k.Grade.Should().Be(KpiGrade.Watch);

        var none = BatteryAnalyzer.Analyze(new BatteryInput { Soc = soc, Packs = new[] { new BatteryPackInput { Index = 1 } }, NominalCapacityKwh = Cap, TimeZone = TD.Ict });
        Kpi(none, "internal_resistance").Grade.Should().Be(KpiGrade.NotAvailable);
        Kpi(none, "internal_resistance").GradeText.Should().Be("ไม่มีข้อมูล");
    }

    // ───────────── Overall grade ─────────────

    [Fact]
    public void Overall_grade_A_when_soh_capacity_efficiency_and_cell_dv_are_good()
    {
        var sim = BatterySim.WorkedExample(days: 2);
        var t0 = sim.SocSeries.StartUtc!.Value;
        var cells = PackWithCells(TD.Local(2026, 1, 6, 1), 15, 40);
        var soh = TD.Constant(t0, TimeSpan.FromDays(3), TimeSpan.FromHours(1), 97);
        // The pack carries its own SOC/power so its rest window (SOC 50 %, P = 0) is well defined.
        var pack = new BatteryPackInput { Index = 1, NominalCapacityKwh = 10.24, CellVMax = cells.CellVMax, CellVMin = cells.CellVMin, Soc = cells.Soc, PowerW = cells.PowerW };
        var r = BatteryAnalyzer.Analyze(Input(sim, b => { b.Soh = soh; b.Packs = new() { pack }; }));
        r.Overall.Grade.Should().Be("A", string.Join(" | ", r.Overall.Reasons));
        r.Overall.Label.Should().Be("ดีมาก");
    }

    [Fact]
    public void Overall_grade_B_when_soh_is_low_but_nothing_is_poor()
    {
        var sim = BatterySim.WorkedExample();
        var soh = TD.Constant(sim.SocSeries.StartUtc!.Value, TimeSpan.FromDays(2), TimeSpan.FromHours(1), 85);
        var r = BatteryAnalyzer.Analyze(Input(sim, b => b.Soh = soh));
        r.Overall.Grade.Should().Be("B");
        r.Overall.Reasons.Should().Contain(x => x.StartsWith("SOH") && x.Contains("ไม่ผ่าน"));
    }

    // ───────────── Warranty ─────────────

    [Fact]
    public void Warranty_throughput_years_and_expected_remaining_life()
    {
        var asOf = TD.Utc(2026, 6, 1);
        var counter = new TimeSeries(new[] { new Point(asOf.AddDays(-400), 3000), new Point(asOf.AddDays(-365), 4000), new Point(asOf, 10000) });
        var input = new BatteryInput
        {
            Soc = TD.Constant(asOf.AddDays(-1), TimeSpan.FromDays(1), TimeSpan.FromHours(1), 50),
            NominalCapacityKwh = Cap, TimeZone = TD.Ict, DischargeTotalKwh = counter,
            Warranty = new BatteryWarranty(10, 33.4, 70), InstallDateUtc = asOf.AddDays(-2 * 365.25),
        };
        var w = BatteryAnalyzer.Analyze(input, new BatteryOptions { AsOfUtc = asOf }).Warranty!;
        w.ThroughputMwh.Should().BeApproximately(10, 1e-9);
        w.ThroughputUsedPct.Should().BeApproximately(10 / 33.4 * 100, 1e-6);
        w.YearsElapsed!.Value.Should().BeApproximately(2, 1e-6);
        w.YearsLeft!.Value.Should().BeApproximately(8, 1e-6);
        w.TrailingYearMwh.Should().BeApproximately(6, 1e-6);
        w.TrailingYearAnnualised.Should().BeFalse();
        w.YearsLeftByThroughput!.Value.Should().BeApproximately(23.4 / 6, 1e-6);
        w.ExpectedRemainingYears!.Value.Should().BeApproximately(3.9, 1e-6);
        w.LimitedBy.Should().Be("throughput");
    }

    [Fact]
    public void Warranty_annualises_less_than_a_year_of_integrated_data()
    {
        var sim = BatterySim.WorkedExample(days: 10, step: TimeSpan.FromMinutes(5));
        var r = BatteryAnalyzer.Analyze(Input(sim, b => { b.Warranty = new BatteryWarranty(10, 33.4, 70); b.Install = sim.SocSeries.StartUtc; }));
        var w = r.Warranty!;
        w.TrailingYearAnnualised.Should().BeTrue();
        w.TrailingYearMwh.Should().BeApproximately(22.8 * 365 / 1000, 0.5);
        w.YearsLeftByThroughput!.Value.Should().BeApproximately((33.4 - w.ThroughputMwh) / w.TrailingYearMwh, 1e-9);
        w.LimitedBy.Should().Be("throughput");   // 8.3 MWh/yr would use the 33.4 MWh in ~4 years
    }

    // ───────────── History, heatmap ─────────────

    [Fact]
    public void History_switches_to_weekly_bars_beyond_365_days()
    {
        var start = new DateOnly(2025, 1, 6);   // a Monday
        var days = Enumerable.Range(0, 400).Select(i => new DailyHistoryPoint(start.AddDays(i), 10, 9, 20 + i % 7, 100, i == 3)).ToList();
        var weekly = BatteryHistory.Buckets(days);
        weekly.Resolution.Should().Be("week");
        weekly.Buckets[0].Start.Should().Be(start);
        weekly.Buckets[0].Days.Should().Be(7);
        weekly.Buckets[0].ChargedKwh.Should().Be(70);
        weekly.Buckets[0].SocMin.Should().Be(20);
        weekly.Buckets[0].HadOutage.Should().BeTrue();
        weekly.Buckets.Should().HaveCount(58);

        var daily = BatteryHistory.Buckets(days.Take(365).ToList());
        daily.Resolution.Should().Be("day");
        daily.Buckets.Should().HaveCount(365);
        BatteryHistory.Buckets(Array.Empty<DailyHistoryPoint>()).Buckets.Should().BeEmpty();
    }

    [Fact]
    public void Soc_heatmap_is_mean_soc_by_local_date_and_hour()
    {
        var t0 = TD.Local(2026, 1, 5, 10);
        var soc = TD.Series(t0, TimeSpan.FromMinutes(30), 40, 60, 80);
        var r = BatteryAnalyzer.Analyze(new BatteryInput { Soc = soc, NominalCapacityKwh = Cap, TimeZone = TD.Ict });
        r.Heatmap.Dates.Should().Equal(new DateOnly(2026, 1, 5));
        r.Heatmap.Values[0][10].Should().Be(50);
        r.Heatmap.Values[0][11].Should().Be(80);
        r.Heatmap.Values[0][9].Should().BeNull();
    }

    // ───────────── Edge cases and JSON ─────────────

    [Fact]
    public void Empty_input_returns_a_report_with_a_note()
    {
        var r = BatteryAnalyzer.Analyze(new BatteryInput { NominalCapacityKwh = Cap });
        r.Notes.Should().NotBeEmpty();
        r.Days.Should().BeEmpty();
        r.Overall.Grade.Should().Be("-");
        r.Warranty.Should().BeNull();
    }

    [Fact]
    public void Single_point_and_no_capacity_do_not_throw()
    {
        var one = new BatteryInput { Soc = TD.Series(TD.Utc(2026, 1, 1), Min, 50), TimeZone = TD.Ict };
        var r = BatteryAnalyzer.Analyze(one);
        r.Days.Should().ContainSingle();
        r.Efc.Should().Be(0);
        r.Notes.Should().Contain(n => n.Contains("ความจุ"));
    }

    [Fact]
    public void Report_round_trips_through_json()
    {
        var sim = BatterySim.WorkedExample(days: 2);
        var r = BatteryAnalyzer.Analyze(Input(sim, b => { b.Warranty = new BatteryWarranty(10, 33.4, 70); b.Install = sim.SocSeries.StartUtc; }));
        var json = AnalyticsJson.Serialize(r);
        json.Should().Contain("\"overall\"").And.Contain("\"Deep\"");
        var back = AnalyticsJson.Deserialize<BatteryReport>(json)!;
        AnalyticsJson.Serialize(back).Should().Be(json);
        back.Capacity.Measurements.Should().HaveCount(r.Capacity.Measurements.Count);
    }
}
