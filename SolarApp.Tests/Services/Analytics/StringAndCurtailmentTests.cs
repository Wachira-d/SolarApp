using FluentAssertions;
using SolarApp.Services.Analytics;
using Xunit;

namespace SolarApp.Tests.Services.Analytics;

public class StringComparatorTests
{
    private static readonly TimeSpan Five = TimeSpan.FromMinutes(5);
    private const double StringW = 4000, SystemKwp = 20;   // 4 strings × 5 kWp

    /// <summary>
    /// 4 strings, <paramref name="days"/> days, samples 09:00–15:00 local. <paramref name="factor"/>
    /// scales string s on day d at sample index i.
    /// </summary>
    private static List<StringInput> Strings(int days, Func<int, int, int, double> factor, int? panels = null)
    {
        var lists = Enumerable.Range(1, 4).Select(_ => new List<Point>()).ToArray();
        for (var d = 0; d < days; d++)
        {
            var t0 = TD.Local(2026, 2, 1 + d, 9);
            for (var i = 0; i <= 72; i++)
                for (var s = 1; s <= 4; s++)
                    lists[s - 1].Add(new Point(t0 + Five * i, StringW * factor(s, d, i)));
        }
        return lists.Select((l, k) => new StringInput { Index = k + 1, PowerW = new TimeSeries(l), Kwp = 5, PanelsInString = panels }).ToList();
    }

    [Fact]
    public void Healthy_strings_have_ratio_one_and_no_findings()
    {
        var r = StringComparator.Compare(Strings(5, (_, _, _) => 1), SystemKwp, timeZone: TD.Ict);
        r.PerKwp.Should().BeTrue();
        r.Days.Should().HaveCount(5);
        r.Days[0].ValidSamples.Should().Be(48);   // 10:00–14:00 at 5 min, end exclusive
        r.Days.SelectMany(d => d.Ratio.Values).Should().OnlyContain(v => Math.Abs(v!.Value - 1) < 1e-9);
        r.Findings.Should().BeEmpty();
        r.Strings.Should().OnlyContain(s => s.Grade == KpiGrade.Good);
    }

    [Fact]
    public void Step_down_of_one_panel_is_a_panel_fault()
    {
        // String 3 loses 1 of 20 panels (5 %) from day 8.
        var r = StringComparator.Compare(Strings(14, (s, d, _) => s == 3 && d >= 8 ? 0.95 : 1, panels: 20), SystemKwp, timeZone: TD.Ict);
        var f = r.Findings.Should().ContainSingle().Subject;
        f.StringIndex.Should().Be(3);
        f.Issue.Should().Be(StringIssue.PanelFault);
        f.Since.Should().Be(new DateOnly(2026, 2, 9));
        f.Days.Should().Be(6);
        f.ChangePct!.Value.Should().BeApproximately(-5, 0.01);
        f.MessageTh.Should().Contain("แผง");
    }

    [Fact]
    public void Gradual_decline_that_recovers_after_rain_is_soiling()
    {
        // String 2 loses 0.5 %/day from day 3 to day 14, rain on day 15 washes it clean.
        double F(int s, int d, int _) => s == 2 && d is >= 3 and < 15 ? 1 - 0.005 * (d - 2) : 1;
        var rain = new[] { new DateOnly(2026, 2, 16) };
        var r = StringComparator.Compare(Strings(18, F), SystemKwp, timeZone: TD.Ict, rainOrCleaningDays: rain);
        var f = r.Findings.Should().ContainSingle().Subject;
        f.StringIndex.Should().Be(2);
        f.Issue.Should().Be(StringIssue.Soiling);
        f.Recovered.Should().BeTrue();
        f.MessageTh.Should().Contain("สตริง 2").And.Contain("ล้างแผง");
    }

    [Fact]
    public void Intermittent_drops_to_zero_are_a_loose_connection()
    {
        // String 4 drops to 0 for 4 samples (20 min) each day.
        var r = StringComparator.Compare(Strings(5, (s, _, i) => s == 4 && i is >= 30 and < 34 ? 0 : 1), SystemKwp, timeZone: TD.Ict);
        var f = r.Findings.Should().ContainSingle().Subject;
        f.StringIndex.Should().Be(4);
        f.Issue.Should().Be(StringIssue.LooseConnection);
        f.Days.Should().Be(5);
        r.Days[0].DropFraction[4].Should().BeApproximately(4 / 48.0, 1e-9);
        r.Days[0].Ratio[4].Should().Be(1);   // the median of the day is unaffected
    }

    [Fact]
    public void Short_deviation_under_three_days_is_not_reported()
    {
        var r = StringComparator.Compare(Strings(10, (s, d, _) => s == 1 && d is 4 or 5 ? 0.8 : 1), SystemKwp, timeZone: TD.Ict);
        r.Findings.Should().BeEmpty();
    }

    [Fact]
    public void Filters_low_light_curtailed_and_missing_strings()
    {
        // Low light: 4 × 1 kW = 4 kW < 30 % of 20 kWp.
        StringComparator.Compare(Strings(3, (_, _, _) => 0.25), SystemKwp, timeZone: TD.Ict).Days.Should().BeEmpty();

        // Curtailed all day.
        var mask = Enumerable.Range(0, 3).Select(d => new TimeRange(TD.Local(2026, 2, 1 + d, 9), TD.Local(2026, 2, 1 + d, 15))).ToList();
        var cur = StringComparator.Compare(Strings(3, (_, _, _) => 1), SystemKwp, curtailed: mask, timeZone: TD.Ict);
        cur.Days.Should().BeEmpty();
        cur.Notes.Should().NotBeEmpty();

        // String 2 not reporting on day 1.
        var s = Strings(3, (_, _, _) => 1);
        s[1] = new StringInput { Index = 2, Kwp = 5, PowerW = s[1].PowerW.Where(p => p.TsUtc < TD.Local(2026, 2, 2) || p.TsUtc >= TD.Local(2026, 2, 3)) };
        StringComparator.Compare(s, SystemKwp, timeZone: TD.Ict).Days.Select(d => d.Date)
            .Should().Equal(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 3));
    }

    [Fact]
    public void Needs_two_strings()
    {
        var one = Strings(2, (_, _, _) => 1).Take(1).ToList();
        StringComparator.Compare(one, SystemKwp, timeZone: TD.Ict).Notes.Should().ContainSingle();
        StringComparator.Compare(Array.Empty<StringInput>(), SystemKwp).Findings.Should().BeEmpty();
    }

    [Fact]
    public void Report_round_trips_through_json()
    {
        var r = StringComparator.Compare(Strings(14, (s, d, _) => s == 3 && d >= 8 ? 0.95 : 1, panels: 20), SystemKwp, timeZone: TD.Ict);
        var json = AnalyticsJson.Serialize(r);
        json.Should().Contain("\"PanelFault\"");
        AnalyticsJson.Serialize(AnalyticsJson.Deserialize<StringComparisonReport>(json)).Should().Be(json);
    }
}

public class CurtailmentDetectorTests
{
    private static readonly TimeSpan Five = TimeSpan.FromMinutes(5);
    private static readonly DateTime D = TD.Local(2026, 3, 10);

    private static CurtailmentInput Day(double pvW, double gridW, double expectedW, TimeSeries? soc = null, TimeSeries? bp = null,
        int fromHour = 10, int toHour = 14) => new()
    {
        PvPowerW = TD.Constant(D.AddHours(fromHour), TimeSpan.FromHours(toHour - fromHour), Five, pvW),
        GridPowerW = TD.Constant(D.AddHours(fromHour), TimeSpan.FromHours(toHour - fromHour), Five, gridW),
        ExpectedPvW = TD.Constant(D.AddHours(fromHour), TimeSpan.FromHours(toHour - fromHour), Five, expectedW),
        BatterySoc = soc,
        BatteryPowerW = bp,
        InverterRatingW = 10_000,
        FromUtc = D.AddHours(fromHour),
        ToUtc = D.AddHours(toHour),
        TimeZone = TD.Ict,
    };

    [Fact]
    public void Zero_export_and_no_battery_with_pv_below_expected_is_curtailed()
    {
        var r = CurtailmentDetector.Detect(Day(3000, -20, 6000), new CurtailmentOptions { RateThbPerKwh = 4.5 });
        r.Available.Should().BeTrue();
        r.CurtailedMinutes.Should().Be(240);
        r.LostKwh.Should().BeApproximately(12, 1e-9);
        r.LostThb.Should().BeApproximately(54, 1e-9);
        r.Intervals.Should().ContainSingle().Which.StartUtc.Should().Be(D.AddHours(10));
        r.Days.Should().ContainSingle().Which.LostKwh.Should().BeApproximately(12, 1e-9);
        r.LostKwhByHour[10].Should().BeApproximately(3, 1e-9);
        r.TopHours.Should().HaveCount(3);
        r.TopHours[0].MessageTh.Should().Contain("เหมาะเปิดเครื่องใช้ไฟฟ้า");
        r.Notes.Should().Contain(n => n.Contains("ไม่มีแบตเตอรี่"));
        r.Mask().Should().ContainSingle().Which.Should().Be(new TimeRange(D.AddHours(10), D.AddHours(14)));
    }

    [Fact]
    public void Exporting_or_pv_near_expected_is_not_curtailed()
    {
        CurtailmentDetector.Detect(Day(3000, -2000, 6000)).CurtailedMinutes.Should().Be(0);
        CurtailmentDetector.Detect(Day(5000, -20, 6000)).CurtailedMinutes.Should().Be(0);   // 83 % of expected
        CurtailmentDetector.Detect(Day(3000, 1500, 6000)).CurtailedMinutes.Should().Be(0);  // importing: loads, not limiting
    }

    [Fact]
    public void Battery_that_can_still_charge_means_not_curtailed()
    {
        var span = TimeSpan.FromHours(4);
        var charging = Day(3000, -20, 6000, TD.Constant(D.AddHours(10), span, Five, 60), TD.Constant(D.AddHours(10), span, Five, 2000));
        CurtailmentDetector.Detect(charging).CurtailedMinutes.Should().Be(0);

        var full = Day(3000, -20, 6000, TD.Constant(D.AddHours(10), span, Five, 99), TD.Constant(D.AddHours(10), span, Five, 0));
        CurtailmentDetector.Detect(full).CurtailedMinutes.Should().Be(240);

        var notAccepting = Day(3000, -20, 6000, TD.Constant(D.AddHours(10), span, Five, 70), TD.Constant(D.AddHours(10), span, Five, 20));
        CurtailmentDetector.Detect(notAccepting).CurtailedMinutes.Should().Be(240);
    }

    [Fact]
    public void All_curtailed_day_with_model_expectation_and_top_hours()
    {
        const double lat = 13.75, lon = 100.5;
        var train = new List<Point>();
        for (var t = TD.Local(2026, 3, 1); t < TD.Local(2026, 3, 8); t += Five) train.Add(new(t, ClearSky.Ghi(t, lat, lon) * 10 * 0.8));
        var model = ExpectedPvModel.Train(new TimeSeries(train), 10, lat, lon, timeZone: TD.Ict);

        // Zero-export site, battery full all day, PV pinned at 1.5 kW.
        var input = new CurtailmentInput
        {
            PvPowerW = TD.Constant(D, TimeSpan.FromDays(1), Five, 1500),
            GridPowerW = TD.Constant(D, TimeSpan.FromDays(1), Five, 10),
            BatterySoc = TD.Constant(D, TimeSpan.FromDays(1), Five, 100),
            Model = model,
            InverterRatingW = 10_000,
            FromUtc = D, ToUtc = D.AddDays(1), TimeZone = TD.Ict,
        };
        var r = CurtailmentDetector.Detect(input);
        r.Days.Should().ContainSingle();
        r.CurtailedMinutes.Should().BeGreaterThan(6 * 60);
        r.TopHours.Select(h => h.LocalHour).Should().OnlyContain(h => h >= 10 && h <= 14);
        r.LostKwh.Should().BeApproximately(r.LostKwhByHour.Sum(), 1e-9);
    }

    [Fact]
    public void Without_expected_pv_the_report_is_not_available()
    {
        var input = Day(3000, -20, 6000);
        var r = CurtailmentDetector.Detect(new CurtailmentInput { PvPowerW = input.PvPowerW, GridPowerW = input.GridPowerW });
        r.Available.Should().BeFalse();
        r.Notes.Should().NotBeEmpty();
        CurtailmentDetector.Detect(new CurtailmentInput { ExpectedPvW = input.ExpectedPvW }).Available.Should().BeFalse();
    }

    [Fact]
    public void Report_round_trips_through_json()
    {
        var r = CurtailmentDetector.Detect(Day(3000, -20, 6000));
        var json = AnalyticsJson.Serialize(r);
        AnalyticsJson.Serialize(AnalyticsJson.Deserialize<CurtailmentReport>(json)).Should().Be(json);
    }
}
