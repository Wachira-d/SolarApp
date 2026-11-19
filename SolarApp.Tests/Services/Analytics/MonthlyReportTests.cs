using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
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
using SolarApp.Services.Authorization;
using SolarApp.Services.Telemetry;
using SolarApp.Tests.TestSupport;
using Xunit;
using OwnerAnalytics = SolarApp.Pages.My.AnalyticsModel;
using OwnerReport = SolarApp.Pages.My.ReportModel;
using StaffReport = SolarApp.Pages.Installations.ReportModel;

namespace SolarApp.Tests.Services.Analytics;

/// <summary>
/// The printable monthly report (/Installations/Report/{id}, /My/Report/{id}):
/// authorisation on both routes, month parsing, figures that agree with the
/// analytics pages, and the server-side daily chart.
/// </summary>
public class MonthlyReportTests
{
    private const string OwnerId = "owner-user";
    private static readonly DateTime Today = new(2026, 11, 19);

    private static SiteAnalyticsService Service(ApplicationDbContext db) =>
        new(db, new SiteAnalyticsLoader(db, new InMemoryTelemetryStore()), NullLogger<SiteAnalyticsService>.Instance);

    private static UserManager<ApplicationUser> Users(ApplicationUser? user)
    {
        var users = new Mock<UserManager<ApplicationUser>>(Mock.Of<IUserStore<ApplicationUser>>(),
            null!, null!, null!, null!, null!, null!, null!, null!);
        users.Setup(u => u.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(user);
        return users.Object;
    }

    private static OwnerReport OwnerPage(ApplicationDbContext db, ApplicationUser? user, SiteAnalyticsService? svc = null)
    {
        return new OwnerReport(db, Users(user), svc ?? Service(db))
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() },
            Today = Today,
        };
    }

    private static StaffReport StaffPage(ApplicationDbContext db, bool allowed, SiteAnalyticsService? svc = null)
    {
        var access = new Mock<IInstallationAccessHelper>();
        access.Setup(a => a.CanAccessAsync(It.IsAny<int>(), It.IsAny<ClaimsPrincipal>())).ReturnsAsync(allowed);
        return new StaffReport(db, access.Object, svc ?? Service(db))
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() },
            Today = Today,
        };
    }

    /// <summary>Daily rows for every day from <paramref name="from"/> up to (not including) <paramref name="to"/>.</summary>
    internal static void AddDays(ApplicationDbContext db, int siteId, DateTime from, DateTime to)
    {
        for (var d = from; d < to; d = d.AddDays(1))
            db.EnergyData.Add(new EnergyData
            {
                InstallationId = siteId, Granularity = DataGranularity.Daily, Timestamp = d,
                EnergyGenerationKwh = 40 + d.Day % 7, GridImportKwh = 10, GridExportKwh = 5,
            });
        db.SaveChanges();
    }

    private static (ApplicationDbContext Db, Installation Site) CloudOnlySite()
    {
        var db = TestDb.Create();
        var (customer, site) = TestDb.SeedSite(db, hasBattery: false);
        site.TelemetrySource = TelemetrySource.Cloud;
        site.ContractValue = 200_000;
        site.ElectricityRatePerKwh = 4.5m;
        customer.UserId = OwnerId;
        db.SaveChanges();
        AddDays(db, site.Id, new DateTime(2025, 9, 1), Today);
        return (db, site);
    }

    // ── authorisation ─────────────────────────────────────────────────

    [Fact]
    public async Task Owner_sees_the_report_of_their_own_site()
    {
        var (db, site) = CloudOnlySite();
        var page = OwnerPage(db, new ApplicationUser { Id = OwnerId });

        (await page.OnGetAsync(site.Id)).Should().BeOfType<PageResult>();

        page.Report.Site.Id.Should().Be(site.Id);
        page.Report.CustomerName.Should().Be("Test Owner");
    }

    [Fact]
    public async Task Another_customer_or_an_unlinked_user_gets_not_found_and_no_user_is_forbidden()
    {
        var (db, site) = CloudOnlySite();
        db.Customers.Add(new Customer { FirstName = "Other", LastName = "Owner", Phone = "0811111111", Address = "y", UserId = "other-user" });
        db.SaveChanges();

        (await OwnerPage(db, new ApplicationUser { Id = "other-user" }).OnGetAsync(site.Id)).Should().BeOfType<NotFoundResult>();
        (await OwnerPage(db, new ApplicationUser { Id = "staff-user" }).OnGetAsync(site.Id)).Should().BeOfType<NotFoundResult>();
        (await OwnerPage(db, null).OnGetAsync(site.Id)).Should().BeOfType<ForbidResult>();
        (await OwnerPage(db, new ApplicationUser { Id = OwnerId }).OnGetAsync(site.Id + 999)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Staff_route_follows_the_access_helper()
    {
        var (db, site) = CloudOnlySite();

        (await StaffPage(db, allowed: false).OnGetAsync(site.Id)).Should().BeOfType<ForbidResult>();
        (await StaffPage(db, allowed: true).OnGetAsync(site.Id + 999)).Should().BeOfType<NotFoundResult>();
        var page = StaffPage(db, allowed: true);
        (await page.OnGetAsync(site.Id, "2026-03")).Should().BeOfType<PageResult>();
        (page.Report.Year, page.Report.Month).Should().Be((2026, 3));
    }

    // ── month ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2026-09", 2026, 9)]
    [InlineData("2026-11", 2026, 11)]   // the running month is allowed
    [InlineData(" 2024-02 ", 2024, 2)]
    [InlineData(null, 2026, 10)]
    [InlineData("", 2026, 10)]
    [InlineData("2026-12", 2026, 10)]   // future
    [InlineData("2026-13", 2026, 10)]
    [InlineData("10/2026", 2026, 10)]
    [InlineData("1999-12", 2026, 10)]
    public void Month_is_parsed_as_yyyy_mm_and_falls_back_to_the_last_complete_month(string? input, int year, int month)
    {
        MonthlyReportBuilder.ParseMonth(input, Today).Should().Be((year, month));
    }

    [Fact]
    public void Default_month_crosses_the_year_boundary()
    {
        MonthlyReportBuilder.DefaultMonth(new DateTime(2027, 1, 1)).Should().Be((2026, 12));
    }

    [Fact]
    public async Task Default_is_the_last_complete_month_and_the_picker_lists_at_most_24_months_with_data()
    {
        var (db, site) = CloudOnlySite();
        var page = OwnerPage(db, new ApplicationUser { Id = OwnerId });
        await page.OnGetAsync(site.Id);

        var r = page.Report;
        (r.Year, r.Month).Should().Be((2026, 10));
        r.Partial.Should().BeFalse();
        r.MonthLabel.Should().Contain("ตุลาคม");
        r.Months.Should().HaveCount(15);   // Sep 2025 .. Nov 2026
        r.Months[0].Should().Be(new ReportMonth(2026, 11, true));
        r.Months[^1].Key.Should().Be("2025-09");

        db.EnergyData.RemoveRange(db.EnergyData);
        db.SaveChanges();
        AddDays(db, site.Id, new DateTime(2023, 1, 1), new DateTime(2026, 11, 1));
        var page2 = OwnerPage(db, new ApplicationUser { Id = OwnerId });
        await page2.OnGetAsync(site.Id);
        page2.Report.Months.Should().HaveCount(23);   // Dec 2024 .. Oct 2026 (the running month has no data)
        page2.Report.Months.Min(m => m.Year * 100 + m.Month).Should().Be(202412);
    }

    [Fact]
    public async Task Running_month_is_marked_partial_and_counts_days_so_far()
    {
        var (db, site) = CloudOnlySite();
        var page = OwnerPage(db, new ApplicationUser { Id = OwnerId });
        await page.OnGetAsync(site.Id, "2026-11");

        page.Report.Partial.Should().BeTrue();
        page.Report.DaysCounted.Should().Be(19);
        page.Report.DaysWithData.Should().Be(18);   // up to yesterday
        page.Report.Chart.Bars.Should().HaveCount(30);
        page.Report.Chart.Bars.Where(b => b.Future).Select(b => b.Day).Should().Equal(Enumerable.Range(20, 11));
    }

    // ── figures ───────────────────────────────────────────────────────

    [Fact]
    public async Task Cloud_only_site_gets_production_savings_and_comparisons_that_match_the_analytics_page()
    {
        var (db, site) = CloudOnlySite();
        var page = OwnerPage(db, new ApplicationUser { Id = OwnerId });
        await page.OnGetAsync(site.Id, "2026-09");
        var r = page.Report;

        r.Caps.HasTelemetry.Should().BeFalse();
        r.HasMonthData.Should().BeTrue();
        r.PvKwh.Should().Be(Enumerable.Range(1, 30).Sum(d => 40 + d % 7));
        r.Result!.SavingsThb.Should().BeGreaterThan(0);
        r.Result.SelfSufficiencyPct.Should().NotBeNull();
        r.Result.Co2Kg.Should().BeApproximately(r.PvKwh * EnvironmentalFactors.DefaultCo2KgPerKwh, 1e-9);
        r.PaybackPct.Should().BeApproximately(r.CumulativeSavingsThb!.Value / 200_000 * 100, 1e-9);
        r.PrevEnergy.Should().NotBeNull();
        r.LastYearEnergy.Should().NotBeNull();
        r.PvVsPrevPct.Should().NotBeNull();
        r.PvVsLastYearPct.Should().NotBeNull();
        r.BestDay!.Value.Kwh.Should().Be(46);
        r.ShowOutages.Should().BeFalse();
        r.ShowBattery.Should().BeFalse();

        // Same numbers as the owner's analytics page for that month.
        var analytics = new OwnerAnalytics(db, Users(new ApplicationUser { Id = OwnerId }), Service(db))
        { PageContext = new PageContext { HttpContext = new DefaultHttpContext() } };
        await analytics.OnGetAsync(site.Id);
        var row = analytics.Months.Single(m => m.Year == 2026 && m.Month == 9);
        r.Result.SavingsThb.Should().Be(row.Result.SavingsThb);
        r.CumulativeSavingsThb.Should().Be(row.CumulativeSavingsThb);
        r.Result.Co2Kg.Should().Be(row.Result.Co2Kg);
        r.Result.SelfSufficiencyPct.Should().Be(row.Result.SelfSufficiencyPct);
    }

    [Fact]
    public async Task CO2_uses_the_system_wide_factors()
    {
        var (db, site) = CloudOnlySite();
        await EnvironmentalFactors.SaveAsync(db, 0.4, 20, "TGO 2025", null);
        var page = OwnerPage(db, new ApplicationUser { Id = OwnerId });
        await page.OnGetAsync(site.Id, "2026-09");

        page.Report.Result!.Co2Kg.Should().BeApproximately(page.Report.PvKwh * 0.4, 1e-9);
        page.Report.Result.Trees.Should().BeApproximately(page.Report.PvKwh * 0.4 / 20, 1e-9);
        page.Report.Factors.BasisText.Should().Contain("0.4").And.Contain("TGO 2025");
    }

    [Fact]
    public async Task Month_without_data_still_loads()
    {
        var (db, site) = CloudOnlySite();
        var page = OwnerPage(db, new ApplicationUser { Id = OwnerId });
        (await page.OnGetAsync(site.Id, "2024-05")).Should().BeOfType<PageResult>();

        page.Report.HasEnergyData.Should().BeTrue();
        page.Report.HasMonthData.Should().BeFalse();
        page.Report.Months.Should().Contain(m => m.Key == "2024-05");
    }

    [Fact]
    public async Task Service_counts_match_the_line_report_rules()
    {
        var (db, site) = CloudOnlySite();
        var sep = new DateTime(2026, 9, 1);
        db.SystemAlerts.AddRange(
            new SystemAlert { InstallationId = site.Id, AlertType = AlertType.Offline, Title = "offline", Message = "m", OccurredAt = SolarApp.Services.SiteTime.ToUtc(sep.AddDays(2).AddHours(1)) },
            new SystemAlert { InstallationId = site.Id, AlertType = AlertType.Offline, Title = "offline", Message = "m", OccurredAt = SolarApp.Services.SiteTime.ToUtc(sep.AddDays(2).AddHours(9)) },
            // 03:00 Thai time on 1 Oct is still 30 Sep in UTC: belongs to October.
            new SystemAlert { InstallationId = site.Id, AlertType = AlertType.Offline, Title = "offline", Message = "m", OccurredAt = SolarApp.Services.SiteTime.ToUtc(new DateTime(2026, 10, 1, 3, 0, 0)) });
        db.CleaningRecords.Add(new CleaningRecord { InstallationId = site.Id, Status = CleaningStatus.Completed, ScheduledDate = sep.AddDays(9), ActualDate = sep.AddDays(10) });
        db.SaveChanges();

        var page = OwnerPage(db, new ApplicationUser { Id = OwnerId });
        await page.OnGetAsync(site.Id, "2026-09");

        page.Report.OfflineDays.Should().Be(1);
        page.Report.HealthyDays.Should().Be(29);
        page.Report.AlertCount.Should().Be(2);
        page.Report.CleaningsDone.Should().Be(1);
    }

    [Fact]
    public async Task Battery_site_gets_the_months_outages_and_battery_summary()
    {
        var f = SiteAnalyticsServiceTests.Build();
        await SiteAnalyticsServiceTests.RollupAsync(f);
        (await f.Service.ComputeSiteAsync(f.Site.Id, SiteAnalyticsServiceTests.Now)).Errors.Should().BeEmpty();

        var page = StaffPage(f.Db, allowed: true, f.Service);
        await page.OnGetAsync(f.Site.Id, "2026-11");
        var r = page.Report;

        r.ShowBattery.Should().BeTrue();
        r.Battery.Should().NotBeNull();
        r.BatteryDays.Should().NotBeEmpty().And.OnlyContain(d => d.Date.Month == 11);
        r.ShowOutages.Should().BeTrue();
        r.OutagesCovered.Should().BeTrue();
        r.Outages.Should().ContainSingle();

        // October: the stored snapshot covers it, nothing happened, battery days none.
        var oct = StaffPage(f.Db, allowed: true, f.Service);
        await oct.OnGetAsync(f.Site.Id, "2026-10");
        oct.Report.OutagesCovered.Should().BeTrue();
        oct.Report.Outages.Should().BeEmpty();
        oct.Report.BatteryDays.Should().BeEmpty();
    }

    // ── chart ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(2026, 2, 28)]
    [InlineData(2024, 2, 29)]
    [InlineData(2026, 4, 30)]
    [InlineData(2026, 10, 31)]
    public void Daily_chart_has_one_bar_per_day_of_the_month(int year, int month, int days)
    {
        var data = new Dictionary<int, double> { [1] = 20, [3] = 55.5, [days] = 10 };
        var chart = DailyChart.Build(year, month, data);

        chart.Bars.Should().HaveCount(days);
        chart.Bars.Select(b => b.Day).Should().Equal(Enumerable.Range(1, days));
        chart.Bars.Single(b => b.Best).Day.Should().Be(3);
        chart.Bars.Single(b => b.Day == 2).Height.Should().Be(0);
        chart.Bars.Single(b => b.Day == 2).Kwh.Should().BeNull();
        chart.MaxKwh.Should().Be(100);
        chart.Bars.Single(b => b.Day == 3).Height.Should().BeApproximately(55.5 / 100 * chart.PlotHeight, 0.01);
        chart.Bars.Should().OnlyContain(b => b.X >= DailyChart.Left && b.X + b.Width <= DailyChart.Width - DailyChart.Right + 0.01);
        chart.Ticks.Should().HaveCount(5);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(7.2, 10)]
    [InlineData(43, 50)]
    [InlineData(120, 200)]
    [InlineData(230, 250)]
    [InlineData(1000, 1000)]
    public void Axis_maximum_is_a_round_number(double max, double expected) =>
        DailyChart.NiceCeiling(max).Should().Be(expected);
}

/// <summary>
/// Renders both report routes through a test server: the real pages and the
/// shared sheet, no staff or portal layout.
/// </summary>
public class MonthlyReportRenderTests
{
    private const string UserHeader = "X-Test-User";

    private sealed class HeaderAuth : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public HeaderAuth(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e) : base(o, l, e) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var userId = Request.Headers[UserHeader].ToString();
            if (string.IsNullOrEmpty(userId)) return Task.FromResult(AuthenticateResult.NoResult());
            var role = userId == "test-admin" ? "Admin" : UserRoles.Customer;
            var id = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Name, userId + "@test"), new Claim(ClaimTypes.Role, role),
            }, "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), "Test")));
        }
    }

    private static async Task<(HttpClient Client, IHost Host)> StartAsync(string dbName, ITelemetryStore store, bool staffAllowed = true)
    {
        var contentRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "SolarApp"));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(OwnerReport).Assembly.GetName().Name,
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
            o.DefaultForbidScheme = "Test";
        }).AddScheme<AuthenticationSchemeOptions, HeaderAuth>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddRazorPages().AddApplicationPart(typeof(OwnerReport).Assembly);
        var access = new Mock<IInstallationAccessHelper>();
        access.Setup(a => a.CanAccessAsync(It.IsAny<int>(), It.IsAny<ClaimsPrincipal>())).ReturnsAsync(staffAllowed);
        builder.Services.AddSingleton(access.Object);
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

    private static async Task<(HttpStatusCode Status, string Html)> GetAs(HttpClient client, string url, string userId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add(UserHeader, userId);
        var res = await client.SendAsync(req);
        // Thai text from @-expressions arrives as numeric entities (no WebEncoderOptions in the app); a browser shows the same.
        return (res.StatusCode, WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync()));
    }

    private static void Dump(string name, string html)
    {
        if (Environment.GetEnvironmentVariable("ANALYTICS_RENDER_DIR") is { Length: > 0 } dir)
            File.WriteAllText(Path.Combine(dir, name), html);
    }

    private static int Bars(string html) => Regex.Matches(html, "<rect class=\"rp-bar[^\"]*\" data-day=\"\\d+\"").Count;

    [Fact]
    public async Task Cloud_only_site_renders_on_both_routes_with_one_bar_per_day()
    {
        var dbName = Guid.NewGuid().ToString();
        var db = TestDb.Create(dbName);
        var (customer, site) = TestDb.SeedSite(db, hasBattery: false);
        site.TelemetrySource = TelemetrySource.Cloud;
        site.ContractValue = 250_000;
        site.City = "เมือง";
        site.Province = "ชลบุรี";
        customer.UserId = "owner";
        db.SaveChanges();
        AddUser(db, "owner");
        AddUser(db, "stranger");
        db.Customers.Add(new Customer { FirstName = "S", LastName = "T", Phone = "0", Address = "z", UserId = "stranger" });
        db.SaveChanges();
        MyAnalyticsPageTests.AddDailyEnergy(db, site.Id, 400);

        var (year, month) = MonthlyReportBuilder.DefaultMonth(SolarApp.Services.SiteTime.Today);
        var days = DateTime.DaysInMonth(year, month);

        var (client, host) = await StartAsync(dbName, new InMemoryTelemetryStore());
        using (host)
        {
            foreach (var (url, user, back) in new[]
            {
                ($"/Installations/Report/{site.Id}", "test-admin", $"/Installations/Analytics/{site.Id}"),
                ($"/My/Report/{site.Id}", "owner", $"/My/Analytics/{site.Id}"),
            })
            {
                var (status, html) = await GetAs(client, url, user);
                status.Should().Be(HttpStatusCode.OK, Snippet(html));
                foreach (var id in new[] { "report-sheet", "rp-header", "rp-production", "rp-savings", "rp-self", "rp-co2", "rp-co2-basis",
                             "rp-cumulative", "rp-payback", "rp-compare", "rp-daily-chart", "rp-service", "rp-print", "rp-month" })
                    html.Should().Contain($"id=\"{id}\"", $"{url} should render #{id}");
                html.Should().NotContain("id=\"rp-battery\"").And.NotContain("id=\"rp-outages\"").And.NotContain("id=\"rp-nodata\"");
                Bars(html).Should().Be(days);
                html.Should().Contain("window.print()").And.Contain("พิมพ์ / บันทึกเป็น PDF").And.Contain("@media print");
                html.Should().Contain("ชลบุรี").And.Contain("Test Site").And.Contain("10 kWp");
                html.Should().Contain($"value=\"{year:0000}-{month:00}\" selected=\"selected\"");
                html.Should().Contain($"href=\"{back}\"");
                html.Should().Contain("Sarabun").And.Contain("Tahoma");
                html.Should().NotContain("NaN");
                // No staff or portal chrome on the sheet.
                html.Should().NotContain("my-bottom-nav").And.NotContain("navbar").And.NotContain("bootstrap");
                Dump(url.StartsWith("/My") ? "report-my-cloud.html" : "report-staff-cloud.html", html);
            }

            (await GetAs(client, $"/My/Report/{site.Id}", "stranger")).Status.Should().Be(HttpStatusCode.NotFound);

            // Each analytics page links to its report.
            var (s1, staffAnalytics) = await GetAs(client, $"/Installations/Analytics/{site.Id}", "test-admin");
            s1.Should().Be(HttpStatusCode.OK, Snippet(staffAnalytics));
            staffAnalytics.Should().Contain($"href=\"/Installations/Report/{site.Id}\"").And.Contain("รายงานรายเดือน (PDF)");
            var (s2, myAnalytics) = await GetAs(client, $"/My/Analytics/{site.Id}", "owner");
            s2.Should().Be(HttpStatusCode.OK, Snippet(myAnalytics));
            myAnalytics.Should().Contain($"href=\"/My/Report/{site.Id}\"").And.Contain("รายงานรายเดือน (PDF)");

            var (febStatus, feb) = await GetAs(client, $"/My/Report/{site.Id}?month={year:0000}-{month:00}", "owner");
            febStatus.Should().Be(HttpStatusCode.OK);
            Bars(feb).Should().Be(days);
        }
    }

    [Fact]
    public async Task Staff_route_is_forbidden_when_the_access_helper_denies()
    {
        var dbName = Guid.NewGuid().ToString();
        var db = TestDb.Create(dbName);
        var (_, site) = TestDb.SeedSite(db, hasBattery: false);
        var (client, host) = await StartAsync(dbName, new InMemoryTelemetryStore(), staffAllowed: false);
        using (host)
            (await GetAs(client, $"/Installations/Report/{site.Id}", "test-admin")).Status.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Battery_site_renders_outages_and_battery_sections()
    {
        var dbName = Guid.NewGuid().ToString();
        var f = SiteAnalyticsServiceTests.Build(dbName: dbName);
        await SiteAnalyticsServiceTests.RollupAsync(f);
        f.Site.BatteryWarrantyYears = 10;
        f.Site.BatteryWarrantyThroughputMwh = 33.4m;
        f.Site.BatteryWarrantyMinCapacityPct = 70;
        f.Db.SaveChanges();
        (await f.Service.ComputeSiteAsync(f.Site.Id, SiteAnalyticsServiceTests.Now)).Errors.Should().BeEmpty();
        for (var d = new DateTime(2025, 10, 1); d < new DateTime(2026, 11, 13); d = d.AddDays(1))
            f.Db.EnergyData.Add(new EnergyData
            {
                InstallationId = f.Site.Id, Granularity = DataGranularity.Daily, Timestamp = d, EnergyGenerationKwh = 40 + d.Day % 7,
                GridImportKwh = 8, GridExportKwh = 1, BatteryChargeKwh = 20, BatteryDischargeKwh = 18,
            });
        f.Db.SaveChanges();
        AddUser(f.Db, "owner");
        f.Db.Customers.Single(c => c.Id == f.Site.CustomerId).UserId = "owner";
        f.Db.SaveChanges();

        var (client, host) = await StartAsync(dbName, f.Store);
        using (host)
        {
            foreach (var (url, user) in new[] { ($"/Installations/Report/{f.Site.Id}?month=2026-11", "test-admin"), ($"/My/Report/{f.Site.Id}?month=2026-11", "owner") })
            {
                var (status, html) = await GetAs(client, url, user);
                status.Should().Be(HttpStatusCode.OK, Snippet(html));
                html.Should().Contain("id=\"rp-battery\"").And.Contain("id=\"rp-outages\"").And.Contain("id=\"rp-daily-chart\"");
                html.Should().Contain("สุขภาพแบตเตอรี่").And.Contain("ใช้ประกันไปแล้ว").And.Contain("จำนวน <b>1</b> ครั้ง");
                html.Should().NotContain("NaN");
                Bars(html).Should().Be(30);
                Dump(url.StartsWith("/My") ? "report-my-battery.html" : "report-staff-battery.html", html);
            }
        }
    }

    private static string Snippet(string html)
    {
        var i = html.IndexOf("Exception", StringComparison.Ordinal);
        return i < 0 ? html[..Math.Min(400, html.Length)] : html.Substring(Math.Max(0, i - 200), Math.Min(1500, html.Length - Math.Max(0, i - 200)));
    }
}
