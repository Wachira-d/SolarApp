using System.Text.Json;
using FluentAssertions;
using SolarApp.Services.Analytics;
using Xunit;

namespace SolarApp.Tests.Services.Analytics;

public class TimeSeriesTests
{
    private static readonly DateTime T0 = TD.Utc(2026, 1, 1);

    [Fact]
    public void Ctor_sorts_dedupes_keeping_last_and_drops_non_finite()
    {
        var s = new TimeSeries(new[]
        {
            new Point(T0.AddMinutes(2), 3),
            new Point(T0, 1),
            new Point(T0.AddMinutes(1), double.NaN),
            new Point(T0.AddMinutes(2), 30),        // later duplicate wins
            new Point(T0.AddMinutes(1), 2),
            new Point(T0.AddMinutes(3), double.PositiveInfinity),
        });
        s.Points.Select(p => p.Value).Should().Equal(1, 2, 30);
        s.Points.Should().BeInAscendingOrder(p => p.TsUtc);
        s.Points.Should().OnlyContain(p => p.TsUtc.Kind == DateTimeKind.Utc);
    }

    [Fact]
    public void ValueAt_is_step_hold_and_respects_max_age()
    {
        var s = TD.Series(T0, TimeSpan.FromMinutes(10), 1, 2, 3);
        s.ValueAt(T0.AddMinutes(-1)).Should().BeNull();
        s.ValueAt(T0).Should().Be(1);
        s.ValueAt(T0.AddMinutes(9)).Should().Be(1);
        s.ValueAt(T0.AddMinutes(10)).Should().Be(2);
        s.ValueAt(T0.AddHours(5)).Should().Be(3);
        s.ValueAt(T0.AddHours(5), TimeSpan.FromMinutes(15)).Should().BeNull();
        s.InterpolateAt(T0.AddMinutes(5)).Should().BeApproximately(1.5, 1e-9);
    }

    [Fact]
    public void Between_is_half_open()
    {
        var s = TD.Series(T0, TimeSpan.FromMinutes(1), 0, 1, 2, 3, 4);
        s.Between(T0.AddMinutes(1), T0.AddMinutes(3)).Points.Select(p => p.Value).Should().Equal(1, 2);
        s.Between(T0.AddMinutes(10), T0.AddMinutes(20)).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void IntegrateKwh_trapezoid_of_watts()
    {
        var s = TD.Constant(T0, TimeSpan.FromHours(1), TimeSpan.FromMinutes(5), 1000);
        s.IntegrateKwh(T0, T0.AddHours(1)).Should().BeApproximately(1.0, 1e-9);
        // Ramp 0 → 2000 W over an hour = 1 kWh.
        TD.Series(T0, TimeSpan.FromHours(1), 0, 2000).IntegrateKwh(T0, T0.AddHours(1), TimeSpan.FromHours(2)).Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void IntegrateKwh_clips_to_window_and_applies_transform()
    {
        var s = TD.Series(T0, TimeSpan.FromHours(1), 1000, 1000, -500, -500);
        var gap = TimeSpan.FromHours(2);
        // 30–60 min at 1000 W (0.5 kWh) + 60–90 min ramping 1000 → 250 W (0.3125 kWh).
        s.IntegrateKwh(T0.AddMinutes(30), T0.AddMinutes(90), gap).Should().BeApproximately(0.8125, 1e-9);
        s.IntegrateKwh(T0.AddHours(2), T0.AddHours(3), v => v < 0 ? -v : 0, gap).Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public void IntegrateKwh_does_not_bridge_gaps_longer_than_max_gap()
    {
        var s = new TimeSeries(new[]
        {
            new Point(T0, 1000), new Point(T0.AddMinutes(10), 1000),
            new Point(T0.AddHours(3), 1000), new Point(T0.AddHours(3).AddMinutes(10), 1000),
        });
        // Only the two 10-minute segments count; the 2 h 50 min hole does not.
        s.IntegrateKwh(T0, T0.AddHours(4)).Should().BeApproximately(2 * 1000 * 10 / 60.0 / 1000, 1e-9);
        s.IntegrateKwh(T0, T0.AddHours(4), TimeSpan.FromHours(4)).Should().BeApproximately(1000 * (3 * 60 + 10) / 60.0 / 1000, 1e-9);
    }

    [Fact]
    public void Empty_and_single_point_series_are_safe()
    {
        TimeSeries.Empty.IntegrateKwh(T0, T0.AddDays(1)).Should().Be(0);
        TimeSeries.Empty.ValueAt(T0).Should().BeNull();
        TimeSeries.Empty.Max().Should().BeNull();
        var one = TD.Series(T0, TimeSpan.FromMinutes(1), 5);
        one.IntegrateKwh(T0, T0.AddDays(1)).Should().Be(0);
        one.DurationWhere(_ => true, T0, T0.AddDays(1)).Should().Be(TimeSpan.Zero);
        new TimeSeries(null).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void DurationWhere_counts_step_hold_time_inside_window()
    {
        var s = TD.Series(T0, TimeSpan.FromMinutes(10), 100, 100, 50, 100, 100);
        s.DurationWhere(v => v >= 99.5, T0, T0.AddMinutes(40)).Should().Be(TimeSpan.FromMinutes(30));
        s.DurationWhere(v => v >= 99.5, T0.AddMinutes(5), T0.AddMinutes(15)).Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void Json_round_trip_with_analytics_options()
    {
        var s = TD.Series(T0, TimeSpan.FromMinutes(1), 1.5, 2.5);
        var json = AnalyticsJson.Serialize(s);
        json.Should().Contain("\"points\"").And.Contain("\"tsUtc\"");
        var back = AnalyticsJson.Deserialize<TimeSeries>(json)!;
        back.Points.Should().Equal(s.Points);
        // Default options too.
        JsonSerializer.Deserialize<TimeSeries>(JsonSerializer.Serialize(s))!.Points.Should().Equal(s.Points);
    }

    [Fact]
    public void AnalyticsJson_writes_enums_as_strings_and_survives_NaN()
    {
        var json = AnalyticsJson.Serialize(new { grade = KpiGrade.Watch, v = double.NaN });
        json.Should().Contain("\"Watch\"").And.Contain("NaN");
    }

    [Theory]
    [InlineData(20, KpiGrade.Good)]
    [InlineData(20.1, KpiGrade.Watch)]
    [InlineData(50, KpiGrade.Watch)]
    [InlineData(50.1, KpiGrade.Poor)]
    public void Grading_lower_is_better_boundaries(double v, KpiGrade expected) =>
        KpiGrades.LowerIsBetter(v, 20, 50).Should().Be(expected);

    [Fact]
    public void Grading_exclusive_good_limit()
    {
        KpiGrades.LowerIsBetter(50, 50, 150, goodInclusive: false).Should().Be(KpiGrade.Watch);
        KpiGrades.LowerIsBetter(49.9, 50, 150, goodInclusive: false).Should().Be(KpiGrade.Good);
        KpiGrades.LowerIsBetter(null, 50, 150).Should().Be(KpiGrade.NotAvailable);
    }
}

public class RainflowTests
{
    [Fact]
    public void Worked_example_100_40_45_25_100_is_one_75_and_one_5_percent_cycle()
    {
        var cycles = Rainflow.Count(new double[] { 100, 40, 45, 25, 100 });
        cycles.Where(c => c.Range == 75).Sum(c => c.Count).Should().Be(1.0);
        cycles.Where(c => c.Range == 5).Sum(c => c.Count).Should().Be(1.0);
        cycles.Sum(c => c.Count).Should().Be(2.0);

        var h = Rainflow.Histogram(cycles);
        h.Bins.Should().HaveCount(10);
        h.Bins[7].Should().Be(new DodBin(70, 80, 1.0));
        h.Bins[0].Cycles.Should().Be(1.0);
        h.TotalCycles.Should().Be(2.0);
    }

    [Fact]
    public void Turning_points_drop_flats_and_monotone_runs()
    {
        Rainflow.TurningPoints(new double[] { 100, 100, 90, 80, 80, 85, 90, 70 }).Should().Equal(100, 80, 90, 70);
    }

    [Fact]
    public void Half_cycles_from_residue_and_full_swing_goes_in_last_bin()
    {
        var cycles = Rainflow.Count(new double[] { 0, 100 });
        cycles.Should().ContainSingle().Which.Should().Be(new RainflowCycle(100, 50, 0.5));
        Rainflow.Histogram(cycles).Bins[9].Cycles.Should().Be(0.5);
        Rainflow.Count(Array.Empty<double>()).Should().BeEmpty();
        Rainflow.Count(new double[] { 50, 50, 50 }).Should().BeEmpty();
    }
}
