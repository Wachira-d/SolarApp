using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SolarApp.Models;
using SolarApp.Services.Analytics;
using SolarApp.Tests.TestSupport;
using Moq;
using Xunit;

namespace SolarApp.Tests.Services.Analytics;

public class EnvironmentalFactorsTests
{
    [Fact]
    public async Task Defaults_until_set_then_saved_values_everywhere()
    {
        await using var db = TestDb.Create();
        (await EnvironmentalFactors.LoadAsync(db)).Should().Be(EnvironmentalFactors.Default);

        await EnvironmentalFactors.SaveAsync(db, 0.4999, 21, " อบก. 2568 ", "u1");
        var f = await EnvironmentalFactors.LoadAsync(db);

        f.Co2KgPerKwh.Should().Be(0.4999);
        f.Co2KgPerTreeYear.Should().Be(21);
        f.Source.Should().Be("อบก. 2568");
        f.Co2Kg(1000).Should().BeApproximately(499.9, 1e-9);
        f.Trees(210).Should().Be(10);
        f.Apply(new EconomicsOptions { ImportRateThb = 5 }).Should().Match<EconomicsOptions>(o =>
            o.ImportRateThb == 5 && o.Co2KgPerKwh == 0.4999 && o.Co2KgPerTree == 21);
        f.BasisText.Should().Contain("0.4999").And.Contain("21").And.Contain("อบก. 2568");

        // Saving again updates the same rows.
        await EnvironmentalFactors.SaveAsync(db, 0.5, 9, null, "u1");
        (await db.SystemSettings.CountAsync(s => s.Key.StartsWith("Report."))).Should().Be(3);
    }

    [Theory]
    [InlineData("0.45", 0.45)]
    [InlineData("abc", 0.5)]
    [InlineData("50", 0.5)]      // out of range — a typo must not make every report absurd
    [InlineData("0", 0.5)]
    [InlineData(null, 0.5)]
    public void Stored_values_outside_the_range_fall_back(string? raw, double expected) =>
        EnvironmentalFactors.Parse(raw, 0.5, EnvironmentalFactors.MinPerKwh, EnvironmentalFactors.MaxPerKwh).Should().Be(expected);

    [Fact]
    public async Task Admin_page_saves_and_rejects_bad_values()
    {
        await using var host = await StaffPageHost.StartAsync(Guid.NewGuid().ToString());

        var form = await host.GetFormAsync("/Admin/ReportSettings");
        form.Get("Co2KgPerKwh").Should().Be("0.5");
        var bad = await host.PostAsync(form.Set("Co2KgPerKwh", "5").Set("Co2KgPerTreeYear", "21"));
        bad.StatusCode.Should().Be(HttpStatusCode.OK);   // re-rendered with the error
        await using (var db = host.NewDb())
            (await EnvironmentalFactors.LoadAsync(db)).Should().Be(EnvironmentalFactors.Default);

        form = await host.GetFormAsync("/Admin/ReportSettings");
        var ok = await host.PostAsync(form.Set("Co2KgPerKwh", "0,4999").Set("Co2KgPerTreeYear", "21").Set("Source", "อบก."));
        ok.StatusCode.Should().Be(HttpStatusCode.Redirect);
        await using (var db = host.NewDb())
        {
            var f = await EnvironmentalFactors.LoadAsync(db);
            f.Co2KgPerKwh.Should().Be(0.4999);
            f.Co2KgPerTreeYear.Should().Be(21);
            f.Source.Should().Be("อบก.");
        }
    }

    [Fact]
    public async Task Staff_analytics_overview_uses_the_saved_factors()
    {
        await using var db = TestDb.Create();
        var (_, site) = TestDb.SeedSite(db, hasBattery: false);
        for (var d = 0; d < 10; d++)
            db.EnergyData.Add(new EnergyData
            {
                InstallationId = site.Id, Granularity = SolarApp.Models.Enums.DataGranularity.Daily,
                Timestamp = new DateTime(2026, 10, 1).AddDays(d), EnergyGenerationKwh = 100,
            });
        db.SaveChanges();
        await EnvironmentalFactors.SaveAsync(db, 0.4, 20, null, null);

        var access = new Moq.Mock<SolarApp.Services.Authorization.IInstallationAccessHelper>();
        access.Setup(a => a.CanAccessAsync(Moq.It.IsAny<int>(), Moq.It.IsAny<System.Security.Claims.ClaimsPrincipal>())).ReturnsAsync(true);
        var svc = new SiteAnalyticsService(db, new SiteAnalyticsLoader(db, new SolarApp.Services.Telemetry.InMemoryTelemetryStore()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SiteAnalyticsService>.Instance);
        var page = new SolarApp.Pages.Installations.AnalyticsModel(db, access.Object, svc,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SolarApp.Pages.Installations.AnalyticsModel>.Instance)
        {
            PageContext = new Microsoft.AspNetCore.Mvc.RazorPages.PageContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() },
        };

        await page.OnGetAsync(site.Id);

        page.Lifetime!.Co2Kg.Should().BeApproximately(1000 * 0.4, 1e-6);
        page.Lifetime.Trees.Should().BeApproximately(1000 * 0.4 / 20, 1e-6);
    }
}
