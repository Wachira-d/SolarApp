using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.Analytics;
using SolarApp.Services.Telemetry;
using SolarApp.Tests.TestSupport;
using Xunit;
using OwnerAnalytics = SolarApp.Pages.My.AnalyticsModel;

namespace SolarApp.Tests.Services.Analytics;

/// <summary>
/// The owner-facing /My/Analytics page: authorisation is the portal's
/// (linked Customer must own the site), sections follow SiteCapabilities.
/// </summary>
public class MyAnalyticsPageTests
{
    private const string OwnerId = "owner-user";

    private static void LinkOwner(ApplicationDbContext db, Installation site, string userId = OwnerId)
    {
        db.Customers.Single(c => c.Id == site.CustomerId).UserId = userId;
        db.SaveChanges();
    }

    /// <summary>Daily EnergyData ending yesterday (site time) so the last-30-day figures have data.</summary>
    internal static void AddDailyEnergy(ApplicationDbContext db, int siteId, int days, bool battery = false)
    {
        var start = SolarApp.Services.SiteTime.Today.AddDays(-days);
        for (var d = 0; d < days; d++)
            db.EnergyData.Add(new EnergyData
            {
                InstallationId = siteId, Granularity = DataGranularity.Daily, Timestamp = start.AddDays(d),
                EnergyGenerationKwh = 40 + d % 7, GridImportKwh = 10, GridExportKwh = 5,
                BatteryChargeKwh = battery ? 20 : null, BatteryDischargeKwh = battery ? 18 : null,
            });
        db.SaveChanges();
    }

    private static OwnerAnalytics Page(ApplicationDbContext db, ApplicationUser? user, SiteAnalyticsService? svc = null)
    {
        var users = new Mock<UserManager<ApplicationUser>>(Mock.Of<IUserStore<ApplicationUser>>(),
            null!, null!, null!, null!, null!, null!, null!, null!);
        users.Setup(u => u.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(user);
        svc ??= new SiteAnalyticsService(db, new SiteAnalyticsLoader(db, new InMemoryTelemetryStore()), NullLogger<SiteAnalyticsService>.Instance);
        return new OwnerAnalytics(db, users.Object, svc)
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private static (ApplicationDbContext Db, Installation Site) CloudOnlySite(int days = 400)
    {
        var db = TestDb.Create();
        var (_, site) = TestDb.SeedSite(db, hasBattery: false);
        site.TelemetrySource = TelemetrySource.Cloud;
        site.ContractValue = 200_000;
        site.ElectricityRatePerKwh = 4.5m;
        db.SaveChanges();
        LinkOwner(db, site);
        AddDailyEnergy(db, site.Id, days);
        return (db, site);
    }

    [Fact]
    public async Task Owner_of_a_cloud_only_site_sees_savings_co2_and_year_on_year_only()
    {
        var (db, site) = CloudOnlySite();
        var page = Page(db, new ApplicationUser { Id = OwnerId });

        (await page.OnGetAsync(site.Id)).Should().BeOfType<PageResult>();

        page.Site.Id.Should().Be(site.Id);
        page.HasEnergyData.Should().BeTrue();
        page.CumulativeSavingsThb.Should().BeGreaterThan(0);
        page.Last30!.SavingsThb.Should().BeGreaterThan(0);
        page.PaybackPct.Should().BeApproximately(page.CumulativeSavingsThb!.Value / 200_000 * 100, 1e-6);
        page.Last30.SelfSufficiencyPct.Should().NotBeNull();
        page.Lifetime!.Co2Kg.Should().BeGreaterThan(0);
        page.YearOnYear.Should().NotBeEmpty().And.HaveCountLessThanOrEqualTo(24);
        page.YearOnYear.Should().Contain(r => r.PvYoYPct.HasValue && r.PvLastYearKwh.HasValue);

        page.ShowOutages.Should().BeFalse();
        page.ShowBattery.Should().BeFalse();
        page.Battery.Should().BeNull();
        page.Grid.Should().BeNull();
        page.History.Buckets.Should().BeEmpty();
        page.BestHours.Should().BeEmpty();
    }

    [Fact]
    public async Task Another_customer_gets_not_found_like_the_other_portal_pages()
    {
        var (db, site) = CloudOnlySite(days: 10);
        var other = new Customer { FirstName = "Other", LastName = "Owner", Phone = "0811111111", Address = "y", UserId = "other-user" };
        db.Customers.Add(other);
        db.SaveChanges();

        (await Page(db, new ApplicationUser { Id = "other-user" }).OnGetAsync(site.Id)).Should().BeOfType<NotFoundResult>();
        // Signed in but not linked to any customer (e.g. a staff account).
        (await Page(db, new ApplicationUser { Id = "staff-user" }).OnGetAsync(site.Id)).Should().BeOfType<NotFoundResult>();
        // No user behind the cookie.
        (await Page(db, null).OnGetAsync(site.Id)).Should().BeOfType<ForbidResult>();
        // The owner, asking for a site id that does not exist.
        (await Page(db, new ApplicationUser { Id = OwnerId }).OnGetAsync(site.Id + 999)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Owner_of_a_battery_site_sees_battery_summary_history_and_outages()
    {
        var f = SiteAnalyticsServiceTests.Build();
        await SiteAnalyticsServiceTests.RollupAsync(f);
        (await f.Service.ComputeSiteAsync(f.Site.Id, SiteAnalyticsServiceTests.Now)).Errors.Should().BeEmpty();
        f.Site.BatteryWarrantyYears = 10;
        f.Site.BatteryWarrantyThroughputMwh = 33.4m;
        f.Site.BatteryWarrantyMinCapacityPct = 70;
        f.Db.SaveChanges();
        (await f.Service.ComputeSiteAsync(f.Site.Id, SiteAnalyticsServiceTests.Now)).Errors.Should().BeEmpty();
        LinkOwner(f.Db, f.Site);
        AddDailyEnergy(f.Db, f.Site.Id, 60, battery: true);

        var page = Page(f.Db, new ApplicationUser { Id = OwnerId }, f.Service);
        (await page.OnGetAsync(f.Site.Id)).Should().BeOfType<PageResult>();

        page.ShowBattery.Should().BeTrue();
        page.Battery.Should().NotBeNull();
        page.Battery!.Overall.Grade.Should().BeOneOf("A", "B", "C");
        page.Battery.Warranty.Should().NotBeNull();
        page.History.Resolution.Should().Be("day");
        page.History.Buckets.Should().NotBeEmpty().And.Contain(b => b.HadOutage);
        page.HistoryDays.Should().BeGreaterThan(0);

        page.ShowOutages.Should().BeTrue();
        page.Grid!.Outages.Should().ContainSingle().Which.HoursCovered.Should().BeGreaterThan(0);
        page.HasEnergyData.Should().BeTrue();
        page.UpdatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Telemetry_site_without_computed_reports_still_loads()
    {
        var f = SiteAnalyticsServiceTests.Build();
        LinkOwner(f.Db, f.Site);

        var page = Page(f.Db, new ApplicationUser { Id = OwnerId }, f.Service);
        (await page.OnGetAsync(f.Site.Id)).Should().BeOfType<PageResult>();

        page.ShowBattery.Should().BeTrue();
        page.ShowOutages.Should().BeTrue();
        page.Battery.Should().BeNull();
        page.Grid.Should().BeNull();
        page.HasEnergyData.Should().BeFalse();
        page.UpdatedAt.Should().BeNull();
    }

    [Fact]
    public void Year_on_year_pairs_each_month_with_the_same_month_a_year_earlier()
    {
        var energy = new List<MonthlyEnergy>
        {
            new(2025, 10, 1000, 300, 100), new(2025, 11, 900, 300, 100),
            new(2026, 10, 1100, 300, 100), new(2026, 11, 450, 150, 50),
        };
        var econ = EconomicsCalculator.Monthly(energy, new EconomicsOptions { ImportRateThb = 4 });

        var rows = OwnerAnalytics.BuildYearOnYear(econ, energy, new DateTime(2026, 11, 19));

        rows.Select(r => (r.Year, r.Month)).Should().Equal((2026, 11), (2026, 10), (2025, 11), (2025, 10));
        rows[0].Partial.Should().BeTrue();
        rows[0].PvLastYearKwh.Should().Be(900);
        rows[0].PvYoYPct.Should().BeApproximately(-50, 1e-9);
        rows[1].Partial.Should().BeFalse();
        rows[1].PvLastYearKwh.Should().Be(1000);
        rows[1].SavingsLastYearThb.Should().BeApproximately(econ[0].Result.SavingsThb, 1e-9);
        rows[3].PvLastYearKwh.Should().BeNull();
        rows[3].PvYoYPct.Should().BeNull();
    }
}

/// <summary>
/// Renders the real /My/Analytics Razor page (portal layout included) through
/// a test server signed in as a portal customer, with real Identity over the
/// in-memory database so the page's own UserManager lookup runs.
/// </summary>
public class MyAnalyticsRenderTests
{
    private const string UserHeader = "X-Test-User";

    private sealed class CustomerAuth : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public CustomerAuth(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e) : base(o, l, e) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var userId = Request.Headers[UserHeader].ToString();
            if (string.IsNullOrEmpty(userId)) return Task.FromResult(AuthenticateResult.NoResult());
            var id = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Name, userId + "@test"),
                new Claim(ClaimTypes.Role, UserRoles.Customer),
            }, "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), "Test")));
        }
    }

    private static async Task<(HttpClient Client, IHost Host)> StartAsync(string dbName, ITelemetryStore store)
    {
        var contentRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "SolarApp"));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(OwnerAnalytics).Assembly.GetName().Name,
            ContentRootPath = contentRoot,
            EnvironmentName = "Development",
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(dbName));
        builder.Services.AddIdentity<ApplicationUser, IdentityRole>().AddEntityFrameworkStores<ApplicationDbContext>();
        builder.Services.AddAuthentication(o =>
        {
            o.DefaultScheme = "Test";
            o.DefaultAuthenticateScheme = "Test";
            o.DefaultChallengeScheme = "Test";
        }).AddScheme<AuthenticationSchemeOptions, CustomerAuth>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddRazorPages().AddApplicationPart(typeof(OwnerAnalytics).Assembly);
        builder.Services.AddSingleton<ITelemetryStore>(store);
        builder.Services.AddScoped<SiteAnalyticsLoader>();
        builder.Services.AddScoped<SiteAnalyticsService>();

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapRazorPages();
        await app.StartAsync();
        return (app.GetTestClient(), app);
    }

    private static void AddUser(ApplicationDbContext db, string id)
    {
        db.Users.Add(new ApplicationUser { Id = id, UserName = id + "@test", NormalizedUserName = (id + "@test").ToUpperInvariant() });
        db.SaveChanges();
    }

    private static async Task<HttpResponseMessage> GetAs(HttpClient client, string url, string userId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add(UserHeader, userId);
        return await client.SendAsync(req);
    }

    [Fact]
    public async Task Battery_site_renders_every_owner_section_for_its_owner_only()
    {
        var dbName = Guid.NewGuid().ToString();
        var f = SiteAnalyticsServiceTests.Build(dbName: dbName);
        await SiteAnalyticsServiceTests.RollupAsync(f);
        f.Site.ContractValue = 350_000;
        f.Site.BatteryWarrantyYears = 10;
        f.Site.BatteryWarrantyThroughputMwh = 33.4m;
        f.Site.BatteryWarrantyMinCapacityPct = 70;
        f.Db.SaveChanges();
        (await f.Service.ComputeSiteAsync(f.Site.Id, SiteAnalyticsServiceTests.Now)).Errors.Should().BeEmpty();
        MyAnalyticsPageTests.AddDailyEnergy(f.Db, f.Site.Id, 400, battery: true);
        AddUser(f.Db, "owner");
        AddUser(f.Db, "stranger");
        f.Db.Customers.Single(c => c.Id == f.Site.CustomerId).UserId = "owner";
        f.Db.Customers.Add(new Customer { FirstName = "S", LastName = "T", Phone = "0", Address = "z", UserId = "stranger" });
        f.Db.SaveChanges();

        var (client, host) = await StartAsync(dbName, f.Store);
        using (host)
        {
            var res = await GetAs(client, $"/My/Analytics/{f.Site.Id}", "owner");
            var html = await res.Content.ReadAsStringAsync();
            res.StatusCode.Should().Be(HttpStatusCode.OK, Snippet(html));
            html.Should().Contain("id=\"chSavings\"").And.Contain("id=\"chSelfSufficiency\"").And.Contain("id=\"chHistory\"");
            html.Should().Contain("คืนทุนแล้ว").And.Contain("ตัน CO₂").And.Contain("เทียบกับปีก่อน")
                .And.Contain("ไฟดับ").And.Contain("สุขภาพแบตเตอรี่").And.Contain("ใช้ประกันไปแล้ว");
            html.Should().Contain("table-responsive");
            html.Should().NotContain("NaN");
            // Owner view only: none of the technician tools.
            html.Should().NotContain("handler=Recompute").And.NotContain("chHeat").And.NotContain("chDod").And.NotContain("Tariff.").And.NotContain("<form method=\"post\"");
            if (Environment.GetEnvironmentVariable("ANALYTICS_RENDER_DIR") is { Length: > 0 } dir)
                await File.WriteAllTextAsync(Path.Combine(dir, "my-battery.html"), html);

            (await GetAs(client, $"/My/Analytics/{f.Site.Id}", "stranger")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task Cloud_only_site_renders_savings_without_battery_or_outages()
    {
        var dbName = Guid.NewGuid().ToString();
        var db = TestDb.Create(dbName);
        var (customer, site) = TestDb.SeedSite(db, hasBattery: false);
        site.TelemetrySource = TelemetrySource.Cloud;
        customer.UserId = "owner";
        db.SaveChanges();
        AddUser(db, "owner");
        MyAnalyticsPageTests.AddDailyEnergy(db, site.Id, 45);

        var (client, host) = await StartAsync(dbName, new InMemoryTelemetryStore());
        using (host)
        {
            var res = await GetAs(client, $"/My/Analytics/{site.Id}", "owner");
            var html = await res.Content.ReadAsStringAsync();
            res.StatusCode.Should().Be(HttpStatusCode.OK, Snippet(html));
            html.Should().Contain("id=\"chSavings\"").And.Contain("ตัน CO₂");
            html.Should().NotContain("id=\"chHistory\"").And.NotContain("สุขภาพแบตเตอรี่").And.NotContain("คืนทุนแล้ว");
            if (Environment.GetEnvironmentVariable("ANALYTICS_RENDER_DIR") is { Length: > 0 } dir)
                await File.WriteAllTextAsync(Path.Combine(dir, "my-cloud.html"), html);
        }
    }

    [Fact]
    public async Task Site_with_no_data_yet_renders_friendly_lines()
    {
        var dbName = Guid.NewGuid().ToString();
        var db = TestDb.Create(dbName);
        var (customer, site) = TestDb.SeedSite(db, hasBattery: false);
        site.TelemetrySource = TelemetrySource.Cloud;
        customer.UserId = "owner";
        db.SaveChanges();
        AddUser(db, "owner");

        var (client, host) = await StartAsync(dbName, new InMemoryTelemetryStore());
        using (host)
        {
            var res = await GetAs(client, $"/My/Analytics/{site.Id}", "owner");
            var html = await res.Content.ReadAsStringAsync();
            res.StatusCode.Should().Be(HttpStatusCode.OK, Snippet(html));
            html.Should().Contain("ยังไม่มีข้อมูลการผลิตรายวัน").And.NotContain("<canvas");
        }
    }

    private static string Snippet(string html)
    {
        var i = html.IndexOf("Exception", StringComparison.Ordinal);
        return i < 0 ? html[..Math.Min(400, html.Length)] : html.Substring(Math.Max(0, i - 200), Math.Min(1500, html.Length - Math.Max(0, i - 200)));
    }
}
