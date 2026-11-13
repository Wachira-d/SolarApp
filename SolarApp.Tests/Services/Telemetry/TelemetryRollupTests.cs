using FluentAssertions;
using SolarApp.Models.Enums;
using SolarApp.Services.Telemetry;
using Xunit;

namespace SolarApp.Tests.Services.Telemetry;

public class TelemetryRollupTests
{
    private static readonly TimeZoneInfo Bangkok = TimeZoneInfo.FindSystemTimeZoneById("Asia/Bangkok");
    private static DateTime T(int h, int m, int s = 0) => new(2026, 11, 13, h, m, s, DateTimeKind.Utc);
    private static RawSample S(int ch, DateTime ts, double v) => new(ch, ts, v);
    private static bool None(int _) => false;
    private static bool All(int _) => true;

    [Fact]
    public void Five_minute_bucket_has_min_max_avg_first_last_count()
    {
        var pts = TelemetryRollup.ToFiveMinutes(new[]
        {
            S(1, T(10, 1), 3), S(1, T(10, 0), 1), S(1, T(10, 4, 59), 2),   // out of order on purpose
            S(1, T(10, 5), 10),
        }, None);

        pts.Should().HaveCount(2);
        var a = pts.Single(p => p.BucketStartUtc == T(10, 0));
        a.Resolution.Should().Be(AggregateResolution.FiveMinutes);
        (a.Min, a.Max, a.Avg, a.First, a.Last, a.Count).Should().Be((1d, 3d, 2d, 1d, 2d, 3));
        var b = pts.Single(p => p.BucketStartUtc == T(10, 5));
        (b.Count, b.First, b.Last).Should().Be((1, 10d, 10d));
    }

    [Fact]
    public void Empty_spans_produce_no_bucket_and_emit_window_is_respected()
    {
        var pts = TelemetryRollup.ToFiveMinutes(new[] { S(1, T(9, 58), 5), S(1, T(10, 2), 1), S(1, T(10, 20), 2) },
            None, T(10, 0), T(10, 15));
        pts.Select(p => p.BucketStartUtc).Should().Equal(T(10, 0));
    }

    [Fact]
    public void Counter_bucket_opens_at_previous_reading_so_buckets_sum_to_total()
    {
        // 1-minute samples of a meter rising 0.1 kWh/min for 15 minutes.
        var samples = Enumerable.Range(0, 15).Select(i => S(7, T(10, i), 1000 + 0.1 * i)).ToList();
        var pts = TelemetryRollup.ToFiveMinutes(samples, All);

        pts.Should().HaveCount(3);
        pts.Sum(TelemetryRollup.Increase).Should().BeApproximately(1.4, 1e-9);   // 1000 → 1001.4
        var second = pts.Single(p => p.BucketStartUtc == T(10, 5));
        second.First.Should().BeApproximately(1000.4, 1e-9);                   // the 10:04 reading
        TelemetryRollup.Increase(second).Should().BeApproximately(0.5, 1e-9);   // includes the 10:04→10:05 step
    }

    [Fact]
    public void Counter_reset_is_bridged()
    {
        var pts = TelemetryRollup.ToFiveMinutes(new[]
        {
            S(7, T(10, 0), 100), S(7, T(10, 1), 101), S(7, T(10, 2), 102),
            S(7, T(10, 5), 103), S(7, T(10, 6), 0.5), S(7, T(10, 7), 1.5),   // meter restarted from 0
            S(7, T(10, 10), 2.5),
        }, All);

        // Real energy: 100→103 (3) + 0→2.5 (2.5) = 5.5, never negative.
        pts.Sum(TelemetryRollup.Increase).Should().BeApproximately(5.5, 1e-9);
        var resetBucket = pts.Single(p => p.BucketStartUtc == T(10, 5));
        TelemetryRollup.Increase(resetBucket).Should().BeApproximately(1 + 0.5 + 1, 1e-9);
        resetBucket.Min.Should().Be(0.5);    // readings stay real
        resetBucket.Max.Should().Be(103);

        // …and the hour keeps the same total.
        var hour = TelemetryRollup.RollUp(pts, AggregateResolution.Hour, t => TelemetryRollup.HourStart(t, Bangkok), All);
        TelemetryRollup.Increase(hour.Single()).Should().BeApproximately(5.5, 1e-9);
    }

    [Fact]
    public void A_bad_read_of_zero_is_not_a_reset()
    {
        // HA Modbus reporting 0 for one failed poll must not count the whole meter as new energy.
        var pts = TelemetryRollup.ToFiveMinutes(new[]
        {
            S(7, T(10, 0), 50000), S(7, T(10, 1), 0), S(7, T(10, 2), 50000.3), S(7, T(10, 3), 50000.5),
        }, All);
        pts.Sum(TelemetryRollup.Increase).Should().BeApproximately(0.5, 1e-6);
        pts.Single().Min.Should().Be(50000);   // the glitch is excluded from the readings too
    }

    [Fact]
    public void Jitter_dips_do_not_double_count()
    {
        TelemetryRollup.IncreaseOfReadings(new[] { 100, 99.99, 100.2 }).Should().BeApproximately(0.2, 1e-9);
        // A daily counter: yesterday's 30 kWh held overnight, reset at wake-up, then 4.2 today.
        TelemetryRollup.IncreaseOfReadings(new[] { 30, 30, 0.1, 2.0, 4.2 }).Should().BeApproximately(4.2, 1e-9);
    }

    [Fact]
    public void Rollup_weights_average_by_count_and_keeps_extremes()
    {
        var kids = new[]
        {
            new AggregatePoint(1, AggregateResolution.FiveMinutes, T(10, 0), 5, 15, 10, 6, 14, 5),
            new AggregatePoint(1, AggregateResolution.FiveMinutes, T(10, 5), 18, 22, 20, 18, 21, 1),
        };
        var h = TelemetryRollup.RollUp(kids, AggregateResolution.Hour, t => TelemetryRollup.HourStart(t, Bangkok), None).Single();
        h.BucketStartUtc.Should().Be(T(10, 0));
        h.Avg.Should().BeApproximately((10 * 5 + 20) / 6d, 1e-9);
        (h.Min, h.Max, h.First, h.Last, h.Count).Should().Be((5d, 22d, 6d, 21d, 6));
    }

    [Fact]
    public void Day_boundaries_are_site_local()
    {
        // 23:59 and 00:01 Thai time are two minutes apart but on different days.
        var before = new DateTime(2026, 11, 13, 16, 59, 0, DateTimeKind.Utc);   // 2026-11-13 23:59 +07
        var after = new DateTime(2026, 11, 13, 17, 1, 0, DateTimeKind.Utc);     // 2026-11-14 00:01 +07

        TelemetryRollup.DayStart(before, Bangkok).Should().Be(new DateTime(2026, 11, 12, 17, 0, 0, DateTimeKind.Utc));
        TelemetryRollup.DayStart(after, Bangkok).Should().Be(new DateTime(2026, 11, 13, 17, 0, 0, DateTimeKind.Utc));

        var fives = TelemetryRollup.ToFiveMinutes(new[] { S(1, before, 1), S(1, after, 2) }, None);
        var hours = TelemetryRollup.RollUp(fives, AggregateResolution.Hour, t => TelemetryRollup.HourStart(t, Bangkok), None);
        var days = TelemetryRollup.RollUp(hours, AggregateResolution.Day, t => TelemetryRollup.DayStart(t, Bangkok), None);
        days.Should().HaveCount(2);
        days.Select(d => TelemetryRollup.LocalDate(d.BucketStartUtc, Bangkok))
            .Should().Equal(new DateTime(2026, 11, 13), new DateTime(2026, 11, 14));
    }

    [Fact]
    public void Mixed_sign_power_bucket_splits_import_and_export_consistently()
    {
        // Grid flipping between +2000 W and −1000 W, average +500 W over 5 minutes.
        var p = new AggregatePoint(1, AggregateResolution.FiveMinutes, T(10, 0), -1000, 2000, 500, 0, 0, 5);
        var (pos, neg) = TelemetryRollup.SplitEnergyKwh(p, TelemetryRollup.FiveMinutes);
        (pos - neg).Should().BeApproximately(500 * 5 / 60d / 1000, 1e-12);   // net matches Avg × time
        pos.Should().BeApproximately(0.5 * 2000 * 5 / 60d / 1000, 1e-12);    // half the time at +2000
        neg.Should().BeApproximately(0.5 * 1000 * 5 / 60d / 1000, 1e-12);
    }
}

public class EnergyDailyBridgeTests
{
    private static readonly DateTime Day = new(2026, 11, 12, 17, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyList<AggregatePoint> Hourly(params (double First, double Last)[] hours) =>
        hours.Select((h, i) => new AggregatePoint(1, AggregateResolution.Hour, Day.AddHours(i), h.First, h.Last,
            (h.First + h.Last) / 2, h.First, h.Last, 60)).ToList();

    private static IReadOnlyList<AggregatePoint> FiveMin(int buckets, double min, double max, double avg) =>
        Enumerable.Range(0, buckets).Select(i => new AggregatePoint(1, AggregateResolution.FiveMinutes,
            Day.AddMinutes(5 * i), min, max, avg, avg, avg, 5)).ToList();

    private static readonly Dictionary<string, IReadOnlyList<AggregatePoint>> Empty = new();

    [Fact]
    public void Counters_give_the_day_figures()
    {
        var e = EnergyDailyBridge.Compute(new Dictionary<string, IReadOnlyList<AggregatePoint>>
        {
            ["pv.energy_total_kwh"] = Hourly((1000, 1002), (1002, 1005.5)),
            ["grid.import_total_kwh"] = Hourly((50, 51), (51, 51.5)),
            ["grid.export_total_kwh"] = Hourly((20, 22)),
            ["battery.charge_total_kwh"] = Hourly((5, 7)),
            ["battery.discharge_total_kwh"] = Hourly((4, 4.5)),
        }, Empty);

        e.GenerationKwh.Should().BeApproximately(5.5, 1e-9);
        e.GridImportKwh.Should().BeApproximately(1.5, 1e-9);
        e.GridExportKwh.Should().BeApproximately(2, 1e-9);
        e.SelfConsumptionKwh.Should().BeApproximately(3.5, 1e-9);
        e.BatteryChargeKwh.Should().BeApproximately(2, 1e-9);
        e.BatteryDischargeKwh.Should().BeApproximately(0.5, 1e-9);
        // No load channel: balance 5.5 + 1.5 − 2 + 0.5 − 2
        e.LoadKwh.Should().BeApproximately(3.5, 1e-9);
    }

    [Fact]
    public void Power_integration_when_counters_are_absent()
    {
        var e = EnergyDailyBridge.Compute(Empty, new Dictionary<string, IReadOnlyList<AggregatePoint>>
        {
            ["pv.power_w"] = FiveMin(12, 1000, 1000, 1000),           // 1 kW for an hour → 1 kWh
            ["grid.power_w"] = FiveMin(12, -500, -500, -500)            // exporting 0.5 kW for an hour
                .Concat(FiveMin(6, 2000, 2000, 2000).Select(p => p with { BucketStartUtc = p.BucketStartUtc.AddHours(2) }))
                .ToList(),                                              // importing 2 kW for half an hour
            ["battery.power_w"] = FiveMin(12, 300, 300, 300),         // charging 0.3 kW for an hour
        });

        e.GenerationKwh.Should().BeApproximately(1, 1e-9);
        e.GridExportKwh.Should().BeApproximately(0.5, 1e-9);
        e.GridImportKwh.Should().BeApproximately(1, 1e-9);
        e.BatteryChargeKwh.Should().BeApproximately(0.3, 1e-9);
        e.BatteryDischargeKwh.Should().Be(0);
    }

    [Fact]
    public void Counter_wins_over_power_and_missing_sources_stay_null()
    {
        var e = EnergyDailyBridge.Compute(
            new Dictionary<string, IReadOnlyList<AggregatePoint>> { ["pv.energy_total_kwh"] = Hourly((10, 12)) },
            new Dictionary<string, IReadOnlyList<AggregatePoint>> { ["pv.power_w"] = FiveMin(12, 5000, 5000, 5000) });
        e.GenerationKwh.Should().BeApproximately(2, 1e-9);
        e.GridImportKwh.Should().BeNull();
        e.BatteryChargeKwh.Should().BeNull();
        e.IsEmpty.Should().BeFalse();
        EnergyDailyBridge.Compute(Empty, Empty).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Daily_counter_is_bridged_across_its_reset()
    {
        // pv.energy_today_kwh (not cumulative): yesterday's 28 held until the inverter wakes, then 0 → 6.
        var e = EnergyDailyBridge.Compute(new Dictionary<string, IReadOnlyList<AggregatePoint>>
        {
            ["pv.energy_today_kwh"] = Hourly((28, 28), (28, 0.2), (0.2, 3), (3, 6)),
        }, Empty);
        e.GenerationKwh.Should().BeApproximately(6, 1e-9);
    }

    [Fact]
    public void Inverter_totals_are_summed_when_there_is_no_site_counter()
    {
        var e = EnergyDailyBridge.Compute(new Dictionary<string, IReadOnlyList<AggregatePoint>>
        {
            ["inverter1.energy_total_kwh"] = Hourly((100, 103)),
            ["inverter2.energy_total_kwh"] = Hourly((200, 202)),
        }, Empty);
        e.GenerationKwh.Should().BeApproximately(5, 1e-9);
    }
}

public class TelemetrySourcePolicyTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 5, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Local_gateway_sites_are_not_polled()
    {
        TelemetrySourcePolicy.PollsCloud(TelemetrySource.LocalGateway).Should().BeFalse();
        TelemetrySourcePolicy.PollsCloud(TelemetrySource.Hybrid).Should().BeTrue();
        TelemetrySourcePolicy.PollsCloud(TelemetrySource.Cloud).Should().BeTrue();
    }

    [Theory]
    [InlineData(TelemetrySource.Cloud, 1, true)]
    [InlineData(TelemetrySource.Hybrid, 5, false)]      // gateway live → it owns EnergyData
    [InlineData(TelemetrySource.Hybrid, 25, true)]      // gateway quiet → cloud falls back in
    [InlineData(TelemetrySource.Hybrid, null, true)]    // never pushed
    [InlineData(TelemetrySource.LocalGateway, 60, false)]
    public void Cloud_writes_energy_only_when_the_gateway_is_not_live(TelemetrySource source, int? minutesAgo, bool expected)
    {
        DateTime? seen = minutesAgo is int m ? Now.AddMinutes(-m) : null;
        TelemetrySourcePolicy.CloudWritesEnergyData(source, seen, Now).Should().Be(expected);
    }
}
