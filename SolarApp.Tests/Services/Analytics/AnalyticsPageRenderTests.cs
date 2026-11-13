using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Pages.Installations;
using SolarApp.Services.Analytics;
using SolarApp.Services.Authorization;
using SolarApp.Services.Telemetry;
using Xunit;

namespace SolarApp.Tests.Services.Analytics;

/// <summary>
/// Renders the real Analytics Razor page (layout included) through a test
/// server, so a view-time error — a null in a report, a bad format string —
/// fails here instead of in front of a customer.
/// </summary>
public class AnalyticsPageRenderTests
{
    private sealed class TestAuth : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e) : base(o, l, e) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var id = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "test-admin"), new Claim(ClaimTypes.Name, "admin@test"),
                new Claim(ClaimTypes.Role, "Admin"),
            }, "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), "Test")));
        }
    }

    private static async Task<(HttpClient Client, IHost Host)> StartAsync(string dbName, ITelemetryStore store)
    {
        var contentRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "SolarApp"));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(AnalyticsModel).Assembly.GetName().Name,
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
        }).AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddRazorPages().AddApplicationPart(typeof(AnalyticsModel).Assembly);
        var access = new Mock<IInstallationAccessHelper>();
        access.Setup(a => a.CanAccessAsync(It.IsAny<int>(), It.IsAny<ClaimsPrincipal>())).ReturnsAsync(true);
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

    [Fact]
    public async Task Every_tab_renders_for_a_full_battery_site()
    {
        var dbName = Guid.NewGuid().ToString();
        var f = SiteAnalyticsServiceTests.Build(dbName: dbName);
        await SiteAnalyticsServiceTests.RollupAsync(f);
        (await f.Service.ComputeSiteAsync(f.Site.Id, SiteAnalyticsServiceTests.Now)).Errors.Should().BeEmpty();
        for (var d = 0; d < 400; d++)
            f.Db.EnergyData.Add(new EnergyData
            {
                InstallationId = f.Site.Id, Granularity = SolarApp.Models.Enums.DataGranularity.Daily,
                Timestamp = new DateTime(2025, 10, 1).AddDays(d), EnergyGenerationKwh = 40 + d % 7, GridImportKwh = 8,
                GridExportKwh = 1, BatteryChargeKwh = 20, BatteryDischargeKwh = 18,
            });
        f.Site.ContractValue = 350_000;
        f.Db.SaveChanges();

        var (client, host) = await StartAsync(dbName, f.Store);
        using (host)
        {
            foreach (var (tab, marker) in new[]
            {
                ("overview", "chSankey"), ("battery", "chHistory"), ("pv", "chStrings"), ("tariff", "chTou"),
                ("power", "chVoltage"), ("quality", "an-table"),
            })
            {
                foreach (var range in new[] { "30", "all" })
                {
                    var res = await client.GetAsync($"/Installations/Analytics/{f.Site.Id}?tab={tab}&range={range}");
                    var html = await res.Content.ReadAsStringAsync();
                    res.StatusCode.Should().Be(HttpStatusCode.OK, $"tab {tab} failed: {Snippet(html)}");
                    html.Should().Contain(marker);
                    if (Environment.GetEnvironmentVariable("ANALYTICS_RENDER_DIR") is { Length: > 0 } dir && range == "30")
                        await File.WriteAllTextAsync(Path.Combine(dir, $"{tab}.html"), html);
                }
            }

            var battery = await client.GetStringAsync($"/Installations/Analytics/{f.Site.Id}?tab=battery");
            battery.Should().Contain("chHeat").And.Contain("chDod").And.Contain("Warranty_ThroughputMwh");
            battery.Should().NotContain("NaN");
        }
    }

    [Fact]
    public async Task Pages_render_for_a_site_with_telemetry_but_no_computed_reports_yet()
    {
        var dbName = Guid.NewGuid().ToString();
        var f = SiteAnalyticsServiceTests.Build(dbName: dbName);
        var (client, host) = await StartAsync(dbName, f.Store);
        using (host)
        {
            foreach (var tab in new[] { "overview", "battery", "pv", "tariff", "power", "quality" })
            {
                var res = await client.GetAsync($"/Installations/Analytics/{f.Site.Id}?tab={tab}");
                var html = await res.Content.ReadAsStringAsync();
                res.StatusCode.Should().Be(HttpStatusCode.OK, $"tab {tab} failed: {Snippet(html)}");
            }
        }
    }

    private static string Snippet(string html)
    {
        var i = html.IndexOf("Exception", StringComparison.Ordinal);
        return i < 0 ? html[..Math.Min(400, html.Length)] : html.Substring(Math.Max(0, i - 200), Math.Min(1500, html.Length - Math.Max(0, i - 200)));
    }
}
