using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SolarApp.BackgroundJobs;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services;
using SolarApp.Services.Analytics;
using SolarApp.Services.LineApi;
using SolarApp.Tests.TestSupport;
using Xunit;

namespace SolarApp.Tests.BackgroundJobs;

/// <summary>The monthly LINE report must say what the portal and the printed report say.</summary>
public class MonthlyReportJobTests
{
    [Fact]
    public async Task Savings_co2_and_trees_use_the_same_formula_and_settings_as_the_pages()
    {
        await using var db = TestDb.Create();
        var (customer, site) = TestDb.SeedSite(db, hasBattery: false);
        customer.LineUserId = "U123";
        site.ElectricityRatePerKwh = 5m;
        site.FeedInTariffRate = 2m;
        var today = SiteTime.Today;
        var monthStart = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
        var days = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
        for (var d = 0; d < days; d++)
            db.EnergyData.Add(new EnergyData
            {
                InstallationId = site.Id, Granularity = DataGranularity.Daily, Timestamp = monthStart.AddDays(d),
                EnergyGenerationKwh = 40, GridImportKwh = 6, GridExportKwh = 10,
            });
        db.SaveChanges();
        await EnvironmentalFactors.SaveAsync(db, 0.4, 20, null, null);

        MonthlyReportData? sent = null;
        var line = new Mock<ILineMessagingService>();
        line.Setup(l => l.SendMonthlyReportAsync(It.IsAny<string>(), It.IsAny<MonthlyReportData>()))
            .Callback<string, MonthlyReportData>((_, r) => sent = r)
            .Returns(Task.CompletedTask);

        await new MonthlyReportJob(db, line.Object, NullLogger<MonthlyReportJob>.Instance).ExecuteAsync();

        sent.Should().NotBeNull();
        var pv = 40.0 * days;
        // Self-consumed (pv − export) at ฿5 + export at ฿2 — the analytics formula, not "all kWh × rate".
        var expected = EconomicsCalculator.Calculate(new EnergyTotals(pv, 6.0 * days, 10.0 * days),
            new EconomicsOptions { ImportRateThb = 5, FeedInRateThb = 2 }).SavingsThb;
        ((double)sent!.SavingsThb).Should().BeApproximately(Math.Round(expected), 1);
        ((double)sent.Co2SavedKg).Should().BeApproximately(Math.Round(pv * 0.4), 1);
        sent.TreesEquivalent.Should().Be((int)Math.Round(pv * 0.4 / 20));
    }
}
