using FluentAssertions;
using SolarApp.Models.Enums;
using SolarApp.Services.Analytics;
using Xunit;

namespace SolarApp.Tests.Services.Analytics;

public class TariffTests
{
    [Fact]
    public void Progressive_bill_with_ft_service_and_vat()
    {
        var t = TariffPresets.PeaResidentialProgressive();
        var b = BillCalculator.Bill(2026, 1, 0, 500, t);
        b.EnergyCharge.Should().BeApproximately(150 * 3.2484 + 250 * 4.2218 + 100 * 4.4217, 1e-9);
        b.FtCharge.Should().BeApproximately(500 * 0.3672, 1e-9);
        b.ServiceCharge.Should().Be(24.62);
        b.Total.Should().BeApproximately(2346.617, 0.01);
        BillCalculator.Progressive(100, t.Tiers).Should().BeApproximately(324.84, 1e-9);
        BillCalculator.Progressive(0, t.Tiers).Should().Be(0);
    }

    [Fact]
    public void Progressive_energy_above_last_bounded_tier_uses_last_rate()
    {
        var tiers = new List<TariffTier> { new(100, 2), new(200, 3) };
        BillCalculator.Progressive(250, tiers).Should().Be(100 * 2 + 100 * 3 + 50 * 3);
    }

    [Fact]
    public void Tou_peak_window_weekdays_9_to_22_weekends_and_holidays_off_peak()
    {
        var t = TariffPresets.PeaResidentialTou();
        var holidays = new[] { new DateOnly(2026, 4, 13) };   // Songkran, a Monday
        t.IsPeak(new DateTime(2026, 4, 15, 9, 0, 0), holidays).Should().BeTrue();
        t.IsPeak(new DateTime(2026, 4, 15, 21, 59, 0), holidays).Should().BeTrue();
        t.IsPeak(new DateTime(2026, 4, 15, 22, 0, 0), holidays).Should().BeFalse();
        t.IsPeak(new DateTime(2026, 4, 15, 8, 59, 0), holidays).Should().BeFalse();
        t.IsPeak(new DateTime(2026, 4, 18, 12, 0, 0), holidays).Should().BeFalse();   // Saturday
        t.IsPeak(new DateTime(2026, 4, 13, 12, 0, 0), holidays).Should().BeFalse();   // holiday
        TariffPresets.Flat(4).IsPeak(new DateTime(2026, 4, 15, 12, 0, 0)).Should().BeFalse();
    }

    [Fact]
    public void Monthly_bills_split_peak_and_off_peak_in_local_time()
    {
        // 1 kWh at local 09:00 and 1 kWh at local 08:00 on a Wednesday.
        var import = new TimeSeries(new[] { new Point(TD.Local(2026, 4, 15, 9), 1), new Point(TD.Local(2026, 4, 15, 8), 1) });
        var b = BillCalculator.Monthly(import, TariffPresets.PeaResidentialTou(), TD.Ict).Single();
        (b.Year, b.Month, b.PeakKwh, b.OffPeakKwh).Should().Be((2026, 4, 1.0, 1.0));
        b.EnergyCharge.Should().BeApproximately(5.7982 + 2.6369, 1e-9);
    }

    [Fact]
    public void Presets_are_marked_unconfirmed_and_round_trip_through_json()
    {
        foreach (var t in new[] { TariffPresets.PeaResidentialProgressive(), TariffPresets.PeaResidentialTou() })
        {
            t.IsUnconfirmedDefault.Should().BeTrue();
            t.Note.Should().Contain("ตรวจสอบ");
            var json = t.ToJson();
            json.Should().Contain(t.Kind.ToString());
            var back = TariffConfig.FromJson(json);
            back.ToJson().Should().Be(json);
            back.Kind.Should().Be(t.Kind);
        }
        TariffConfig.FromJson("{\"kind\":\"TimeOfUse\",\"peakRate\":6}").PeakDays.Should().HaveCount(5);
        TariffPresets.PeaResidentialTou().Kind.Should().Be(TariffKind.TimeOfUse);
    }
}

public class TouAnalyzerTests
{
    /// <summary>Hourly imports for whole local months, <paramref name="kwh"/> per hour where <paramref name="when"/> holds.</summary>
    private static TimeSeries Imports(int months, Func<DateTime, bool> when, double kwh = 2)
    {
        var pts = new List<Point>();
        for (var l = new DateTime(2026, 1, 1); l < new DateTime(2026, 1 + months, 1); l = l.AddHours(1))
            if (when(l)) pts.Add(new Point(DateTime.SpecifyKind(l.AddHours(-7), DateTimeKind.Utc), kwh));
        return new TimeSeries(pts);
    }

    private static bool Night(DateTime l) => l.Hour >= 22 || l.Hour < 6;
    private static bool WeekdayDay(DateTime l) => l.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && l.Hour is >= 10 and < 18;

    [Fact]
    public void Battery_shifted_night_imports_recommend_tou()
    {
        var r = TouAnalyzer.Compare(Imports(3, Night), null, TariffPresets.PeaResidentialProgressive(), TariffPresets.PeaResidentialTou(), timeZone: TD.Ict);
        r.Months.Should().HaveCount(3);
        r.Months.Should().OnlyContain(m => m.Delta < 0 && m.PeakSharePct == 0);
        r.RecentSavingPct!.Value.Should().BeGreaterThan(3);
        r.RecommendSwitch.Should().BeTrue();
        r.RecommendationTh.Should().Contain("แนะนำเปลี่ยน").And.Contain("Off-Peak").And.Contain("แบตเตอรี่");
        r.UsesUnconfirmedRates.Should().BeTrue();
    }

    [Fact]
    public void Weekday_daytime_imports_do_not_recommend_tou()
    {
        var r = TouAnalyzer.Compare(Imports(3, WeekdayDay), null, TariffPresets.PeaResidentialProgressive(), TariffPresets.PeaResidentialTou(), timeZone: TD.Ict);
        r.RecommendSwitch.Should().BeFalse();
        r.RecentPeakSharePct!.Value.Should().BeApproximately(100, 1e-9);
        r.RecommendationTh.Should().Contain("ยังไม่คุ้ม");
    }

    [Fact]
    public void Needs_three_months_and_meter_cost_payback()
    {
        var two = TouAnalyzer.Compare(Imports(2, Night), null, TariffPresets.PeaResidentialProgressive(), TariffPresets.PeaResidentialTou(), timeZone: TD.Ict);
        two.RecommendSwitch.Should().BeFalse();
        two.RecommendationTh.Should().Contain("ยังไม่ครบ");

        var costly = TouAnalyzer.Compare(Imports(3, Night), null, TariffPresets.PeaResidentialProgressive(), TariffPresets.PeaResidentialTou(),
            timeZone: TD.Ict, options: new TouOptions { MeterChangeCostThb = 1_000_000, MaxPaybackMonths = 12 });
        costly.RecommendSwitch.Should().BeFalse();
        costly.RecommendationTh.Should().Contain("ไม่คุ้มค่าเปลี่ยนมิเตอร์");
    }

    [Fact]
    public void Export_credit_and_holidays_are_applied()
    {
        var import = Imports(3, l => l.Hour == 12);
        var export = Imports(3, l => l.Hour == 11, 1);
        var holidays = Enumerable.Range(0, 90).Select(i => new DateOnly(2026, 1, 1).AddDays(i)).ToList();   // everything off-peak
        var r = TouAnalyzer.Compare(import, export, TariffPresets.Flat(4.5), TariffPresets.PeaResidentialTou(), exportRateThb: 2.2,
            holidays: holidays, timeZone: TD.Ict);
        var jan = r.Months[0];
        jan.ExportKwh.Should().Be(31);
        jan.ExportCredit.Should().BeApproximately(31 * 2.2, 1e-9);
        jan.PeakSharePct.Should().Be(0);
        AnalyticsJson.Serialize(AnalyticsJson.Deserialize<TouComparison>(AnalyticsJson.Serialize(r))).Should().Be(AnalyticsJson.Serialize(r));
    }

    [Fact]
    public void Empty_imports_give_an_explanation()
    {
        var r = TouAnalyzer.Compare(TimeSeries.Empty, null, TariffPresets.Flat(4), TariffPresets.PeaResidentialTou(), timeZone: TD.Ict);
        r.Months.Should().BeEmpty();
        r.RecommendSwitch.Should().BeFalse();
        r.RecommendationTh.Should().NotBeNullOrEmpty();
    }
}

public class EconomicsCalculatorTests
{
    [Fact]
    public void Savings_self_sufficiency_co2_trees_and_payback()
    {
        var r = EconomicsCalculator.Calculate(new EnergyTotals(1000, 300, 200),
            new EconomicsOptions { ImportRateThb = 4, FeedInRateThb = 2.2, SystemPriceThb = 100_000 });
        r.SelfConsumedKwh.Should().Be(800);
        r.ImportAvoidedThb.Should().Be(3200);
        r.ExportIncomeThb.Should().BeApproximately(440, 1e-9);
        r.SavingsThb.Should().BeApproximately(3640, 1e-9);
        r.ConsumptionKwh.Should().Be(1100);
        r.SelfSufficiencyPct!.Value.Should().BeApproximately((1 - 300 / 1100.0) * 100, 1e-9);
        r.SelfConsumptionPct.Should().Be(80);
        r.Co2Kg.Should().Be(500);
        r.Trees.Should().BeApproximately(500 / 9.0, 1e-9);
        r.PaybackProgressPct!.Value.Should().BeApproximately(3.64, 1e-9);
    }

    [Fact]
    public void Load_meter_overrides_derived_consumption_and_zero_load_is_safe()
    {
        EconomicsCalculator.Calculate(new EnergyTotals(1000, 300, 200, LoadKwh: 1200)).SelfSufficiencyPct!.Value
            .Should().BeApproximately(75, 1e-9);
        var zero = EconomicsCalculator.Calculate(new EnergyTotals(0, 0, 0));
        zero.SelfSufficiencyPct.Should().BeNull();
        zero.PaybackProgressPct.Should().BeNull();
    }

    [Fact]
    public void Monthly_cumulative_payback_and_year_on_year()
    {
        var months = Enumerable.Range(0, 13).Select(i => new MonthlyEnergy(2025 + (i / 12), i % 12 + 1, i == 12 ? 1100 : 1000, 300, 200)).ToList();
        var list = EconomicsCalculator.Monthly(months, new EconomicsOptions { ImportRateThb = 4, FeedInRateThb = 0, SystemPriceThb = 100_000 });
        list.Should().HaveCount(13);
        list[0].PvYoYPct.Should().BeNull();
        list[11].CumulativeSavingsThb.Should().BeApproximately(12 * 3200, 1e-9);
        list[11].CumulativePaybackPct!.Value.Should().BeApproximately(38.4, 1e-9);
        list[12].PvYoYPct!.Value.Should().BeApproximately(10, 1e-9);
        list[12].SavingsYoYPct!.Value.Should().BeApproximately(12.5, 1e-9);   // 900 vs 800 self-consumed
        list[12].SelfSufficiencyYoYPts.Should().NotBeNull();
    }
}
