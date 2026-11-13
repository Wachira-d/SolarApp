using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SolarApp.BackgroundJobs;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Models.Telemetry;
using SolarApp.Pages.Installations;
using SolarApp.Services.Analytics;
using SolarApp.Services.Authorization;
using SolarApp.Services.Telemetry;
using SolarApp.Tests.TestSupport;
using Xunit;

namespace SolarApp.Tests.Services.Analytics;

/// <summary>
/// End to end over synthetic telemetry: raw samples → rollup → SiteAnalyticsService
/// → SiteAnalyticsDaily JSON → the Analytics page model reading it back.
/// </summary>
public class SiteAnalyticsServiceTests
{
    // 12:00 Thai time.
    internal static readonly DateTime Now = new(2026, 11, 13, 5, 0, 0, DateTimeKind.Utc);
    private const int Days = 5;
    private static readonly TimeSpan Step = TimeSpan.FromMinutes(5);

    internal sealed record Fixture(ApplicationDbContext Db, InMemoryTelemetryStore Store, Installation Site,
        SiteAnalyticsService Service, SiteAnalyticsLoader Loader);

    internal static Fixture Build(bool hasBattery = true, bool fullSet = true, string? dbName = null)
    {
        var db = TestDb.Create(dbName);
        var (_, site) = TestDb.SeedSite(db, hasBattery: hasBattery);
        site.Latitude = 13.36;
        site.Longitude = 100.98;
        site.ElectricityRatePerKwh = 4.5m;
        db.SaveChanges();

        var keys = new List<string> { "pv.power_w", "grid.power_w" };
        if (fullSet)
        {
            keys.AddRange(new[]
            {
                "grid.status", "grid.voltage_v", "grid.import_total_kwh", "inverter1.temp_c",
                "pv.string1.power_w", "pv.string2.power_w",
            });
            if (hasBattery)
            {
                keys.AddRange(new[] { "battery.soc_pct", "battery.power_w", "battery.soh_pct" });
                for (var p = 1; p <= 3; p++)
                    keys.AddRange(new[]
                    {
                        $"battery.pack{p}.soc_pct", $"battery.pack{p}.power_w", $"battery.pack{p}.cell_v_max",
                        $"battery.pack{p}.cell_v_min", $"battery.pack{p}.cell_temp_max_c", $"battery.pack{p}.cell_temp_min_c",
                    });
            }
        }

        var from = Now.AddDays(-Days);
        var channels = keys.ToDictionary(k => k, k =>
        {
            var p = TelemetryKey.Parse(k);
            return new TelemetryChannel
            {
                InstallationId = site.Id, Key = k, Kind = p.Kind, DeviceIndex = p.DeviceIndex, SubIndex = p.SubIndex,
                Quantity = p.Quantity, Unit = p.Unit, IsCumulative = p.IsCumulative, FirstTs = from, LastTs = Now,
            };
        });
        db.TelemetryChannels.AddRange(channels.Values);
        db.SaveChanges();

        var store = new InMemoryTelemetryStore();
        var raw = new List<RawSample>();
        double importKwh = 1000;
        for (var t = from; t < Now; t += Step)
        {
            var local = t.AddHours(7);
            var h = local.Hour + local.Minute / 60.0;
            var pv = h is > 6 and < 18 ? 8000 * Math.Sin(Math.PI * (h - 6) / 12) : 0;
            // Battery: full at 18:00, discharges overnight to 24 % at 07:00, recharges by 13:00, holds.
            double soc, batW;
            if (h >= 18) { soc = 100 - (h - 18) * 76 / 13; batW = -1796; }
            else if (h < 7) { soc = 100 - (h + 6) * 76 / 13; batW = -1796; }
            else if (h < 13) { soc = 24 + (h - 7) * 76 / 6; batW = 3891; }
            else { soc = 100; batW = 0; }
            var grid = pv > 0 ? 0 : 300.0;
            importKwh += grid * Step.TotalHours / 1000;
            var outage = local.Date == Now.AddHours(7).Date.AddDays(-2) && h is >= 19 and < 19.5;

            void Add(string key, double v)
            {
                if (channels.TryGetValue(key, out var c)) raw.Add(new RawSample(c.Id, t, v));
            }
            Add("pv.power_w", pv);
            Add("grid.power_w", grid);
            Add("grid.status", outage ? 0 : 1);
            Add("grid.voltage_v", outage ? 0 : 230 + 4 * Math.Sin(h));
            Add("grid.import_total_kwh", importKwh);
            Add("inverter1.temp_c", 30 + pv / 8000 * 15);
            Add("pv.string1.power_w", pv / 2);
            Add("pv.string2.power_w", pv / 2);
            Add("battery.soc_pct", Math.Round(soc));
            Add("battery.power_w", batW);
            Add("battery.soh_pct", 99);
            for (var p = 1; p <= 3; p++)
            {
                Add($"battery.pack{p}.soc_pct", Math.Round(soc));
                Add($"battery.pack{p}.power_w", batW / 3);
                Add($"battery.pack{p}.cell_v_max", 3.345);
                Add($"battery.pack{p}.cell_v_min", 3.330);
                Add($"battery.pack{p}.cell_temp_max_c", 31);
                Add($"battery.pack{p}.cell_temp_min_c", 29);
            }
        }
        store.AppendRawAsync(raw).GetAwaiter().GetResult();

        var loader = new SiteAnalyticsLoader(db, store);
        var svc = new SiteAnalyticsService(db, loader, NullLogger<SiteAnalyticsService>.Instance);
        return new Fixture(db, store, site, svc, loader);
    }

    internal static async Task RollupAsync(Fixture f)
    {
        var job = new TelemetryRollupJob(f.Db, f.Store, NullLogger<TelemetryRollupJob>.Instance);
        for (var i = 0; i < Days + 3; i++) await job.RunAsync(Now);
    }

    private static AnalyticsModel Page(Fixture f)
    {
        var access = new Mock<IInstallationAccessHelper>();
        access.Setup(a => a.CanAccessAsync(It.IsAny<int>(), It.IsAny<ClaimsPrincipal>())).ReturnsAsync(true);
        return new AnalyticsModel(f.Db, access.Object, f.Service, NullLogger<AnalyticsModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() },
        };
    }

    [Fact]
    public async Task Full_site_computes_every_section_and_the_page_reads_them_back()
    {
        var f = Build();
        await RollupAsync(f);

        var r = await f.Service.ComputeSiteAsync(f.Site.Id, Now);

        r.Errors.Should().BeEmpty();
        r.Computed.Should().Contain(new[]
        {
            SiteAnalyticsKinds.Battery, SiteAnalyticsKinds.PvModel, SiteAnalyticsKinds.Curtailment, SiteAnalyticsKinds.Pv,
            SiteAnalyticsKinds.Strings, SiteAnalyticsKinds.Grid, SiteAnalyticsKinds.Thermal, SiteAnalyticsKinds.Quality,
            SiteAnalyticsKinds.Tou,
        });

        var batteryDays = await f.Db.SiteAnalyticsDaily.CountAsync(a => a.Kind == SiteAnalyticsKinds.BatteryDay);
        batteryDays.Should().BeInRange(Days, Days + 1);

        // Every tab loads and finds its stored report.
        foreach (var tab in new[] { "overview", "battery", "pv", "tariff", "power", "quality", "events" })
        {
            var page = Page(f);
            (await page.OnGetAsync(f.Site.Id, tab)).Should().BeOfType<PageResult>();
            page.ActiveTab.Should().Be(tab == "events" ? "overview" : tab);   // no status codes → no events tab
        }

        var p = Page(f);
        await p.OnGetAsync(f.Site.Id, "battery", "30");
        p.Battery.Should().NotBeNull();
        p.Battery!.Packs.Should().HaveCount(3);
        p.Battery.Days.Should().NotBeEmpty();
        p.Battery.Efc.Should().BeGreaterThan(1);
        p.History.Resolution.Should().Be("day");
        p.History.Buckets.Should().HaveCountGreaterThanOrEqualTo(Days);
        p.History.Buckets.Should().Contain(b => b.HadOutage);
        p.Pv.Should().NotBeNull();
        p.Strings.Should().NotBeNull();
        p.Grid!.Outages.Should().ContainSingle();
        p.Grid.Quality.Should().NotBeNull();
        p.Thermal!.Inverters.Should().ContainSingle();
        p.Quality.Should().NotBeNull();
        p.Tou.Should().NotBeNull();
        p.Tou!.Comparison.CurrentName.Should().Contain("ก้าวหน้า");
        p.Tabs.Single(t => t.Key == "events").Available.Should().BeFalse();
    }

    [Fact]
    public void Daily_comparisons_start_at_the_first_full_day_of_data()
    {
        var tz = SolarApp.Services.SiteTime.TimeZone;
        var from = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        // Data starts 09:00 local on 10 Nov → comparison starts 11 Nov 00:00 local (10 Nov 17:00 UTC).
        var s = TimeSeries.FromValues(new DateTime(2026, 11, 10, 2, 0, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(5), new double[] { 1, 2 });
        SiteAnalyticsService.DataStart(s, from, tz).Should().Be(new DateTime(2026, 11, 10, 17, 0, 0, DateTimeKind.Utc));
        // Data that starts at local midnight keeps that day; data older than the window keeps the window.
        var midnight = TimeSeries.FromValues(new DateTime(2026, 11, 9, 17, 0, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(5), new double[] { 1 });
        SiteAnalyticsService.DataStart(midnight, from, tz).Should().Be(new DateTime(2026, 11, 9, 17, 0, 0, DateTimeKind.Utc));
        var old = TimeSeries.FromValues(from.AddDays(-3), TimeSpan.FromMinutes(5), new double[] { 1 });
        SiteAnalyticsService.DataStart(old, from, tz).Should().Be(from);
    }

    [Fact]
    public async Task Pv_summary_ignores_days_before_the_gateway_existed()
    {
        var f = Build(hasBattery: false, fullSet: false);
        await RollupAsync(f);
        await f.Service.ComputeSiteAsync(f.Site.Id, Now);
        var (pv, _) = await f.Service.LatestAsync<PvSnapshot>(f.Site.Id, SiteAnalyticsKinds.Pv);
        pv!.Days.Count.Should().BeLessThanOrEqualTo(Days);
        pv.Summary.PerformanceIndexPct.Should().BeGreaterThan(60);
    }

    [Fact]
    public async Task Recompute_is_idempotent_and_keeps_one_row_per_day_and_kind()
    {
        var f = Build();
        await RollupAsync(f);
        await f.Service.ComputeSiteAsync(f.Site.Id, Now);
        var count = await f.Db.SiteAnalyticsDaily.CountAsync();

        await f.Service.ComputeSiteAsync(f.Site.Id, Now.AddMinutes(30));

        (await f.Db.SiteAnalyticsDaily.CountAsync()).Should().Be(count);
        var dupes = await f.Db.SiteAnalyticsDaily.GroupBy(a => new { a.Date, a.Kind }).CountAsync(g => g.Count() > 1);
        dupes.Should().Be(0);
    }

    [Fact]
    public async Task Pv_model_is_reused_within_a_week_and_retrained_after()
    {
        var f = Build();
        await RollupAsync(f);
        await f.Service.ComputeSiteAsync(f.Site.Id, Now);
        var first = await f.Db.SiteAnalyticsDaily.SingleAsync(a => a.Kind == SiteAnalyticsKinds.PvModel);

        await f.Service.ComputeSiteAsync(f.Site.Id, Now.AddDays(3));
        (await f.Db.SiteAnalyticsDaily.Where(a => a.Kind == SiteAnalyticsKinds.PvModel).Select(a => a.ComputedAt).ToListAsync())
            .Should().Equal(first.ComputedAt);

        await f.Service.ComputeSiteAsync(f.Site.Id, Now.AddDays(8));
        (await f.Db.SiteAnalyticsDaily.CountAsync(a => a.Kind == SiteAnalyticsKinds.PvModel)).Should().Be(2);
    }

    [Fact]
    public async Task Site_without_battery_skips_battery_and_hides_the_tab_with_a_reason()
    {
        var f = Build(hasBattery: false);
        await RollupAsync(f);

        var r = await f.Service.ComputeSiteAsync(f.Site.Id, Now);

        r.Computed.Should().NotContain(SiteAnalyticsKinds.Battery);
        r.Skipped.Should().Contain(s => s.Contains("ไม่มีแบตเตอรี่"));
        (await f.Db.SiteAnalyticsDaily.AnyAsync(a => a.Kind.StartsWith("battery"))).Should().BeFalse();

        var page = Page(f);
        await page.OnGetAsync(f.Site.Id, "battery");
        page.ActiveTab.Should().Be("overview");
        var tab = page.Tabs.Single(t => t.Key == "battery");
        tab.Available.Should().BeFalse();
        tab.Reason.Should().Be("ไม่มีแบตเตอรี่ในระบบนี้");
    }

    [Fact]
    public async Task Minimal_site_gets_only_what_its_channels_support()
    {
        var f = Build(hasBattery: false, fullSet: false);   // pv.power_w + grid.power_w only
        await RollupAsync(f);

        var r = await f.Service.ComputeSiteAsync(f.Site.Id, Now);

        r.Errors.Should().BeEmpty();
        r.Computed.Should().Contain(new[] { SiteAnalyticsKinds.PvModel, SiteAnalyticsKinds.Pv, SiteAnalyticsKinds.Curtailment, SiteAnalyticsKinds.Tou });
        r.Computed.Should().NotContain(new[] { SiteAnalyticsKinds.Strings, SiteAnalyticsKinds.Grid, SiteAnalyticsKinds.Thermal });

        var page = Page(f);
        await page.OnGetAsync(f.Site.Id);
        page.Tabs.Where(t => t.Available).Select(t => t.Key).Should().Equal("overview", "pv", "tariff", "quality");
    }

    [Fact]
    public async Task Cloud_only_site_shows_overview_from_daily_energy_and_nothing_else()
    {
        var db = TestDb.Create();
        var (_, site) = TestDb.SeedSite(db, hasBattery: false);
        site.TelemetrySource = TelemetrySource.Cloud;
        site.ContractValue = 200_000;
        site.ElectricityRatePerKwh = 4.5m;
        for (var d = 0; d < 60; d++)
            db.EnergyData.Add(new EnergyData
            {
                InstallationId = site.Id, Granularity = DataGranularity.Daily, Timestamp = new DateTime(2026, 9, 1).AddDays(d),
                EnergyGenerationKwh = 40, GridImportKwh = 10, GridExportKwh = 5,
            });
        db.SaveChanges();
        var svc = new SiteAnalyticsService(db, new SiteAnalyticsLoader(db, new InMemoryTelemetryStore()), NullLogger<SiteAnalyticsService>.Instance);

        var r = await svc.ComputeSiteAsync(site.Id, Now);
        r.Computed.Should().BeEmpty();
        r.Skipped.Should().ContainSingle().Which.Should().Contain("Local API");

        var access = new Mock<IInstallationAccessHelper>();
        access.Setup(a => a.CanAccessAsync(It.IsAny<int>(), It.IsAny<ClaimsPrincipal>())).ReturnsAsync(true);
        var page = new AnalyticsModel(db, access.Object, svc, NullLogger<AnalyticsModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() },
        };
        await page.OnGetAsync(site.Id);

        page.Tabs.Where(t => t.Available).Select(t => t.Key).Should().Equal("overview");
        page.HasEnergyData.Should().BeTrue();
        page.Months.Should().HaveCount(2);
        // 35 kWh self-consumed a day at 4.5 THB = 157.5 THB a day over 60 days.
        page.Months[^1].CumulativeSavingsThb.Should().BeApproximately(60 * 35 * 4.5, 1);
        page.Months[^1].CumulativePaybackPct.Should().BeApproximately(60 * 35 * 4.5 / 200_000 * 100, 0.01);
    }

    [Fact]
    public async Task Denied_user_gets_forbid()
    {
        var f = Build(fullSet: false);
        var access = new Mock<IInstallationAccessHelper>();
        access.Setup(a => a.CanAccessAsync(It.IsAny<int>(), It.IsAny<ClaimsPrincipal>())).ReturnsAsync(false);
        var page = new AnalyticsModel(f.Db, access.Object, f.Service, NullLogger<AnalyticsModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() },
        };
        (await page.OnGetAsync(f.Site.Id)).Should().BeOfType<Microsoft.AspNetCore.Mvc.ForbidResult>();
        (await page.OnPostRecomputeAsync(f.Site.Id, null)).Should().BeOfType<Microsoft.AspNetCore.Mvc.ForbidResult>();
    }

    [Fact]
    public async Task Job_runs_every_site_with_telemetry()
    {
        var f = Build(fullSet: false);
        await RollupAsync(f);
        var services = new ServiceCollection();
        services.AddSingleton(f.Service);
        using var provider = services.BuildServiceProvider();
        var job = new SiteAnalyticsJob(f.Db, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<SiteAnalyticsJob>.Instance);

        var results = await job.RunAsync(Now);

        results.Should().ContainSingle().Which.InstallationId.Should().Be(f.Site.Id);
    }
}

public class SiteAnalyticsLoaderTests
{
    private static TelemetryChannel Ch(string key, int id)
    {
        var p = TelemetryKey.Parse(key);
        return new TelemetryChannel { Id = id, Key = key, Kind = p.Kind, DeviceIndex = p.DeviceIndex, Quantity = p.Quantity, IsCumulative = p.IsCumulative };
    }

    private static AggregatePoint Agg(DateTime t, double min, double max, double avg, double first, double last) =>
        new(1, AggregateResolution.FiveMinutes, t, min, max, avg, first, last, 5);

    [Theory]
    [InlineData("pv.power_w", 50)]
    [InlineData("grid.import_total_kwh", 90)]
    [InlineData("inverter1.status_code", 90)]
    [InlineData("battery.pack1.cell_v_max", 100)]
    [InlineData("battery.pack1.cell_temp_min_c", 0)]
    [InlineData("grid.status", 0)]
    public void Picks_the_bucket_value_by_quantity(string key, double expected) =>
        SiteAnalyticsLoader.Pick(Ch(key, 1), Agg(DateTime.UtcNow, 0, 100, 50, 10, 90)).Should().Be(expected);

    [Fact]
    public void Counter_hourly_steps_between_hours_and_bridges_resets()
    {
        var t0 = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        AggregatePoint H(int h, double first, double last) => new(1, AggregateResolution.Hour, t0.AddHours(h), first, last, 0, first, last, 12);
        var s = SiteAnalyticsLoader.CounterHourly(new[] { H(0, 100, 101), H(1, 101.2, 103), H(2, 0.1, 0.5), H(4, 1, 2) });
        s.Points.Select(p => Math.Round(p.Value, 3)).Should().Equal(1, 2, 0.5, 1);
    }

    [Fact]
    public async Task Long_windows_read_aggregates_and_append_the_raw_tail()
    {
        var db = TestDb.Create();
        var (_, site) = TestDb.SeedSite(db);
        var ch = new TelemetryChannel { InstallationId = site.Id, Key = "pv.power_w", Kind = TelemetryDeviceKind.Pv, Quantity = TelemetryQuantity.Power };
        db.TelemetryChannels.Add(ch);
        db.SaveChanges();
        var store = new InMemoryTelemetryStore();
        var t0 = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        await store.UpsertAggregatesAsync(Enumerable.Range(0, 12).Select(i =>
            new AggregatePoint(ch.Id, AggregateResolution.FiveMinutes, t0.AddMinutes(5 * i), 0, 0, 1000, 0, 0, 5)).ToList());
        // Raw rows inside the aggregated hour are ignored; rows after it are appended.
        await store.AppendRawAsync(new[]
        {
            new RawSample(ch.Id, t0.AddMinutes(10), 9999),
            new RawSample(ch.Id, t0.AddMinutes(61), 2000),
            new RawSample(ch.Id, t0.AddMinutes(62), 2100),
        });

        var set = await new SiteAnalyticsLoader(db, store).LoadAsync(site.Id, t0, t0.AddDays(3));

        var s = set!.Get("pv.power_w")!;
        s.Points.Should().HaveCount(14);
        s.Points.Should().NotContain(p => p.Value == 9999);
        s.Points[^1].Value.Should().Be(2100);
    }

    [Fact]
    public void Battery_input_uses_packs_and_falls_back_to_one_system_pack()
    {
        var site = new Installation { HasBattery = true, BatteryCapacityKwh = 10.24m, BatteryCount = 3,
            BatteryWarrantyYears = 10, BatteryWarrantyThroughputMwh = 33.4m, BatteryWarrantyMinCapacityPct = 70 };
        var soc = TimeSeries.FromValues(DateTime.UtcNow.Date, TimeSpan.FromMinutes(5), new double[] { 50, 51, 52 });
        var noPacks = new SiteTelemetrySet
        {
            Site = site, Capabilities = SiteCapabilities.From(site, Array.Empty<TelemetryChannel>()),
            Series = new Dictionary<string, TimeSeries> { ["battery.soc_pct"] = soc },
        };

        var input = SiteAnalyticsInputs.Battery(noPacks);

        input.NominalCapacityKwh.Should().BeApproximately(30.72, 1e-9);
        input.Packs.Should().ContainSingle().Which.NominalCapacityKwh.Should().BeApproximately(30.72, 1e-9);
        input.Warranty.Should().Be(new BatteryWarranty(10, 33.4, 70));

        var withPacks = new SiteTelemetrySet
        {
            Site = site, Capabilities = noPacks.Capabilities,
            Channels = new[] { Ch("battery.pack1.soc_pct", 1), Ch("battery.pack2.soc_pct", 2) },
            Series = new Dictionary<string, TimeSeries> { ["battery.pack1.soc_pct"] = soc, ["battery.pack2.soc_pct"] = soc },
        };
        var packed = SiteAnalyticsInputs.Battery(withPacks);
        packed.Packs.Select(p => p.Index).Should().Equal(1, 2);
        packed.Packs.Should().OnlyContain(p => Math.Abs(p.NominalCapacityKwh - 10.24) < 1e-9);
        packed.Soc.Points.Should().HaveCount(3);   // averaged from the packs when there is no system SOC
    }

    [Fact]
    public void String_power_is_derived_from_voltage_and_current()
    {
        var t = DateTime.UtcNow.Date;
        var set = new SiteTelemetrySet
        {
            Site = new Installation(),
            Capabilities = SiteCapabilities.From(new Installation(), Array.Empty<TelemetryChannel>()),
            Channels = new[] { Ch("pv.string1.voltage_v", 1), Ch("pv.string1.current_a", 2) },
            Series = new Dictionary<string, TimeSeries>
            {
                ["pv.string1.voltage_v"] = TimeSeries.FromValues(t, TimeSpan.FromMinutes(1), new double[] { 400, 410 }),
                ["pv.string1.current_a"] = TimeSeries.FromValues(t, TimeSpan.FromMinutes(1), new double[] { 5, 6 }),
            },
        };
        SiteAnalyticsInputs.Strings(set).Single().PowerW.Points.Select(p => p.Value).Should().Equal(2000, 2460);
    }
}

public class AnalyticsTariffFormTests
{
    [Fact]
    public void Parses_progressive_tiers_and_appends_an_open_last_tier()
    {
        AnalyticsModel.ParseTiers("150:3.2484, 400:4.2218, :4.4217").Should().Equal(
            new TariffTier(150, 3.2484), new TariffTier(400, 4.2218), new TariffTier(null, 4.4217));
        AnalyticsModel.ParseTiers("150:3.2, 400:4.2").Should().HaveCount(3).And.EndWith(new TariffTier(null, 4.2));
    }

    [Theory]
    [InlineData("")]
    [InlineData("150-3.2")]
    [InlineData("400:4, 150:3")]          // limits must rise
    [InlineData(":3, 150:4")]             // only the last tier may be open
    [InlineData("150:-1")]
    public void Rejects_bad_tiers(string text) => AnalyticsModel.ParseTiers(text).Should().BeNull();

    [Fact]
    public void Validates_tou_window_and_round_trips_to_json()
    {
        var bad = new AnalyticsModel.TariffInput { Name = "x", Kind = TariffKind.TimeOfUse, PeakRate = 5, OffPeakRate = 2.6, PeakStartHour = 22, PeakEndHour = 9 };
        AnalyticsModel.BuildConfig(bad).Config.Should().BeNull();

        var ok = new AnalyticsModel.TariffInput { Name = "TOU", Kind = TariffKind.TimeOfUse, PeakRate = 5.7982, OffPeakRate = 2.6369, PeakStartHour = 9, PeakEndHour = 22, FtPerKwh = 0.3672, VatPct = 7 };
        var (cfg, err) = AnalyticsModel.BuildConfig(ok);
        err.Should().BeNull();
        var back = TariffConfig.FromJson(cfg!.ToJson());
        back.Kind.Should().Be(TariffKind.TimeOfUse);
        back.PeakRate.Should().Be(5.7982);
        back.IsUnconfirmedDefault.Should().BeFalse();
    }

    [Fact]
    public void Picks_current_and_an_alternative_of_another_kind()
    {
        SiteAnalyticsService.PickTariffs(Array.Empty<TariffPlan>()).Current.Kind.Should().Be(TariffKind.Progressive);
        SiteAnalyticsService.PickTariffs(Array.Empty<TariffPlan>()).Alternative.Kind.Should().Be(TariffKind.TimeOfUse);

        var plans = new[]
        {
            new TariffPlan { Id = 1, IsCurrent = false, ConfigJson = TariffPresets.Flat(4).ToJson() },
            new TariffPlan { Id = 2, IsCurrent = true, ConfigJson = TariffPresets.PeaResidentialTou().ToJson() },
            new TariffPlan { Id = 3, IsCurrent = false, ConfigJson = "not json" },
        };
        var (cur, alt) = SiteAnalyticsService.PickTariffs(plans);
        cur.Kind.Should().Be(TariffKind.TimeOfUse);
        alt.Kind.Should().Be(TariffKind.Flat);
    }
}
