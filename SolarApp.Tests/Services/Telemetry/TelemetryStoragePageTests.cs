using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SolarApp.Models;
using SolarApp.Models.Telemetry;
using SolarApp.Services.Telemetry;
using SolarApp.Tests.TestSupport;
using Xunit;

namespace SolarApp.Tests.Services.Telemetry;

public class TelemetryCapacityEstimateTests
{
    [Fact]
    public void Projects_each_tier_from_bytes_per_row_daily_rate_and_retention()
    {
        // 10 sites × 150 channels × 1 sample/min; raw measured at 3 B, aggregates not measurable (reference 30 B).
        var e = TelemetryCapacityEstimate.Compute(3.0, null, 2.5, 30, rawPerDay: 2_160_000, activeChannels: 1500,
            activeSites: 10, rawDays: 90, fiveMinuteDays: 400);

        (e.BytesPerRaw, e.RawMeasured, e.BytesPerAggregate, e.AggregateMeasured).Should().Be((3.0, true, 30.0, false));
        e.FiveMinutePerDay.Should().Be(1500 * 288);
        e.HourAndDayPerDay.Should().Be(1500 * 24 + 1500);
        e.RawBytes.Should().Be(3.0 * 2_160_000 * 90);                 //   583 200 000
        e.FiveMinuteBytes.Should().Be(30.0 * 432_000 * 400);          // 5 184 000 000
        e.HourAndDayBytesPerYear.Should().Be(30.0 * 37_500 * 365);    //   410 625 000
        e.ProjectedBytes.Should().Be(6_177_825_000);
        e.ProjectedGb.Should().BeApproximately(5.7535, 0.0001);
        e.BytesPerSite.Should().Be(617_782_500);
        e.SitesPer100Gb.Should().Be(173);   // 100 × 2^30 ÷ 617 782 500 = 173.8
    }

    [Fact]
    public void A_sparse_channel_makes_no_more_buckets_than_samples()
    {
        var e = TelemetryCapacityEstimate.Compute(null, null, 2.5, 30, rawPerDay: 100, activeChannels: 10,
            activeSites: 1, rawDays: 90, fiveMinuteDays: 400);

        e.FiveMinutePerDay.Should().Be(100);
        e.HourAndDayPerDay.Should().Be(100 + 10);
        (e.RawMeasured, e.AggregateMeasured).Should().Be((false, false));
    }

    [Fact]
    public void No_reporting_site_means_no_per_site_figure()
    {
        var e = TelemetryCapacityEstimate.Compute(3, 30, 2.5, 30, 0, 0, 0, 90, 400);
        e.ProjectedBytes.Should().Be(0);
        e.BytesPerSite.Should().BeNull();
        e.SitesPer100Gb.Should().BeNull();
    }

    [Fact]
    public void Bytes_per_row_needs_enough_rows_to_mean_anything()
    {
        TelemetryCapacityEstimate.Measure(1_000_000, 10_000).Should().Be(100);
        TelemetryCapacityEstimate.Measure(1_000_000, 9_999).Should().BeNull();
        TelemetryCapacityEstimate.Measure(null, 50_000).Should().BeNull();
        TelemetryCapacityEstimate.Measure(1_000, null).Should().BeNull();
    }

    [Fact]
    public void Compression_saving_and_ratio_come_from_before_and_after_sizes()
    {
        var t = new TelemetryTableSize("telemetry_raw", TelemetryTableKind.Raw, 1000, 500, BeforeCompressionBytes: 8000, AfterCompressionBytes: 400);
        t.CompressionSavedBytes.Should().Be(7600);
        t.CompressionRatio.Should().Be(20);
        new TelemetryTableSize("x", TelemetryTableKind.Raw, 1, 1).CompressionRatio.Should().BeNull();
    }
}

public class TelemetryStoragePageTests
{
    [Fact]
    public async Task Renders_for_an_admin_with_counts_and_an_estimate()
    {
        var db = "telemetry-storage-" + Guid.NewGuid().ToString("N");
        var store = new InMemoryTelemetryStore();
        await using var host = await StaffPageHost.StartAsync(db, s => s.AddSingleton<ITelemetryStore>(store));
        int channelId;
        using (var ctx = host.NewDb())
        {
            var (_, site) = TestDb.SeedSite(ctx);
            var ch = new TelemetryChannel { InstallationId = site.Id, Key = "pv.power_w", FirstTs = DateTime.UtcNow.AddDays(-2), LastTs = DateTime.UtcNow.AddMinutes(-1) };
            ctx.TelemetryChannels.Add(ch);
            ctx.SaveChanges();
            channelId = ch.Id;
        }
        await store.AppendRawAsync(Enumerable.Range(1, 60).Select(m => new RawSample(channelId, DateTime.UtcNow.AddMinutes(-m), m)).ToList());

        var res = await host.Client.GetAsync("/Admin/TelemetryStorage");
        var html = WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());

        res.StatusCode.Should().Be(HttpStatusCode.OK, html.Length > 2000 ? html[..2000] : html);
        html.Should().Contain("ที่เก็บข้อมูล Telemetry");
        html.Should().Contain("หน่วยความจำ (ทดสอบ)");
        html.Should().Contain("ค่าดิบ 24 ชม. ล่าสุด");
        html.Should().Contain(">60<", "60 raw samples arrived in the last 24 hours");
        html.Should().Contain("ประมาณการพื้นที่").And.Contain("ค่าประมาณ");
        html.Should().NotContain("คัดลอกข้อมูล telemetry เดิมจาก SQL Server", "the copy tool is for PostgreSQL only");
    }

    [Fact]
    public async Task Renders_with_a_message_when_postgres_is_unreachable()
    {
        // Nothing listens on port 1: the store fails on first use.
        var store = new PostgresTelemetryStore("Host=127.0.0.1;Port=1;Timeout=2;Pooling=false", "public");
        await using var host = await StaffPageHost.StartAsync("telemetry-storage-down-" + Guid.NewGuid().ToString("N"), s =>
        {
            s.AddSingleton<ITelemetryStore>(store);
            s.AddKeyedSingleton<ITelemetryStoreStats>(SolarApp.Pages.Admin.TelemetryStorageModel.LegacySourceKey, new InMemoryTelemetryStore());
        });

        var res = await host.Client.GetAsync("/Admin/TelemetryStorage");
        var html = WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());

        res.StatusCode.Should().Be(HttpStatusCode.OK, html.Length > 2000 ? html[..2000] : html);
        html.Should().Contain("อ่านข้อมูลจากที่เก็บไม่ได้");
        html.Should().NotContain("แบบนี้ไม่บีบอัดข้อมูล", "with the database down it is unknown whether TimescaleDB is on");
        html.Should().Contain("ไม่มีข้อมูล telemetry ค้างอยู่ใน SQL Server");
    }

    [PostgresFact]
    public async Task Renders_in_postgres_mode_and_degrades_when_sql_server_is_unreachable()
    {
        var schema = "t_" + Guid.NewGuid().ToString("N")[..12];
        var store = new PostgresTelemetryStore(PostgresTelemetryStoreContractTests.ConnectionString!, schema);
        try
        {
            await using var host = await StaffPageHost.StartAsync("telemetry-storage-pg-" + schema,
                s => s.AddSingleton<ITelemetryStore>(store));
            using (var ctx = host.NewDb())
            {
                // A copy that stopped part-way: the page must show it without throwing, even with SQL Server gone.
                await new TelemetryCopyProgress { Status = TelemetryCopyStatus.Failed, RawCopied = 1234, LastError = "boom" }.SaveAsync(ctx);
            }

            var res = await host.Client.GetAsync("/Admin/TelemetryStorage");
            var html = WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
            res.StatusCode.Should().Be(HttpStatusCode.OK, html.Length > 2000 ? html[..2000] : html);
            html.Should().Contain("PostgreSQL");
            html.Should().Contain(schema);
            html.Should().Contain("telemetry_agg_5m");
            html.Should().Contain("ข้อมูล telemetry เดิมใน SQL Server");
        }
        finally { await DropSchemaAsync(schema); }
    }

    /// <summary>PostgreSQL active, and a stand-in for the SQL Server tables that still holds rows.</summary>
    private static async Task<(StaffPageHost Host, string Schema)> StartWithLegacyRowsAsync()
    {
        var schema = "t_" + Guid.NewGuid().ToString("N")[..12];
        var store = new PostgresTelemetryStore(PostgresTelemetryStoreContractTests.ConnectionString!, schema);
        var legacy = new InMemoryTelemetryStore();
        await legacy.AppendRawAsync(new[] { new RawSample(1, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), 5) });
        var host = await StaffPageHost.StartAsync("telemetry-storage-copy-" + schema, s =>
        {
            s.AddSingleton<ITelemetryStore>(store);
            s.AddKeyedSingleton<ITelemetryStoreStats>(SolarApp.Pages.Admin.TelemetryStorageModel.LegacySourceKey, legacy);
        });
        return (host, schema);
    }

    [PostgresFact]
    public async Task Shows_the_copy_tool_and_its_progress_and_a_second_click_while_running_does_nothing()
    {
        var (host, schema) = await StartWithLegacyRowsAsync();
        try
        {
            await using var _ = host;
            using (var ctx = host.NewDb())
                await new TelemetryCopyProgress
                {
                    Status = TelemetryCopyStatus.Running, RunId = "run-1", StartedUtc = DateTime.UtcNow.AddMinutes(-3),
                    HeartbeatUtc = DateTime.UtcNow, ChannelsTotal = 10, ChannelsDone = 4, RawCopied = 98765,
                    CurrentChannelId = 17, CurrentChannelKey = "battery.soc_pct", CurrentDayUtc = new DateTime(2026, 10, 3),
                }.SaveAsync(ctx);

            var form = await host.GetFormAsync("/Admin/TelemetryStorage");
            var html = WebUtility.HtmlDecode(form.Html);
            html.Should().Contain("คัดลอกข้อมูล telemetry เดิมจาก SQL Server");
            html.Should().Contain("กำลังคัดลอก").And.Contain("98,765").And.Contain("battery.soc_pct").And.Contain("2026-10-03");
            html.Should().MatchRegex("<button[^>]*disabled[^>]*>\\s*<i class=\"bi bi-files\"></i> คัดลอกข้อมูล");
            html.Should().Contain("location.reload()", "the page refreshes itself while the copy runs");

            var res = await host.PostAsync(form, "Copy");

            res.StatusCode.Should().Be(HttpStatusCode.Redirect);
            using var check = host.NewDb();
            var p = await TelemetryCopyProgress.LoadAsync(check);
            (p.Status, p.RunId, p.RawCopied).Should().Be((TelemetryCopyStatus.Running, "run-1", 98765L), "a click while running changes nothing");
        }
        finally { await DropSchemaAsync(schema); }
    }

    [PostgresFact]
    public async Task A_click_queues_the_copy_and_reports_when_the_job_cannot_be_enqueued()
    {
        var (host, schema) = await StartWithLegacyRowsAsync();
        try
        {
            await using var _ = host;
            var form = await host.GetFormAsync("/Admin/TelemetryStorage");
            WebUtility.HtmlDecode(form.Html).Should().Contain("ยังไม่เคยคัดลอก");

            var res = await host.PostAsync(form, "Copy");

            res.StatusCode.Should().Be(HttpStatusCode.Redirect);
            using var check = host.NewDb();
            var p = await TelemetryCopyProgress.LoadAsync(check);
            p.RunId.Should().NotBeNullOrEmpty("the click claimed a run");
            p.RequestedBy.Should().Be("admin@test");
            // No Hangfire storage in the test host: the failure is recorded, and the next click may try again.
            p.Status.Should().Be(TelemetryCopyStatus.Failed);
            p.LastError.Should().Contain("เข้าคิวงานเบื้องหลังไม่ได้");
            p.IsActive(DateTime.UtcNow).Should().BeFalse();
        }
        finally { await DropSchemaAsync(schema); }
    }

    private static async Task DropSchemaAsync(string schema)
    {
        await using var conn = new NpgsqlConnection(PostgresTelemetryStoreContractTests.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
