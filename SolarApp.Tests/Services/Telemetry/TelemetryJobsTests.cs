using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SolarApp.BackgroundJobs;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Models.Telemetry;
using SolarApp.Services.Telemetry;
using SolarApp.Tests.TestSupport;
using Xunit;

namespace SolarApp.Tests.Services.Telemetry;

public class TelemetryRollupJobTests
{
    // 12:00 Thai time; the site-local day 2026-11-13 started at 2026-11-12 17:00 UTC.
    private static readonly DateTime Now = new(2026, 11, 13, 5, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime From = new(2026, 11, 13, 3, 0, 0, DateTimeKind.Utc);

    private sealed record Fixture(ApplicationDbContext Db, InMemoryTelemetryStore Store, TelemetryRollupJob Job,
        Installation Site, TelemetryChannel Power, TelemetryChannel Counter);

    private static Fixture Setup(DateTime? firstTs = null, int minutes = 120)
    {
        var db = TestDb.Create();
        var (_, site) = TestDb.SeedSite(db);
        var start = firstTs ?? From;
        TelemetryChannel Ch(string key)
        {
            var p = TelemetryKey.Parse(key);
            return new TelemetryChannel
            {
                InstallationId = site.Id, Key = key, Kind = p.Kind, Quantity = p.Quantity, Unit = p.Unit,
                IsCumulative = p.IsCumulative, FirstTs = start, LastTs = start.AddMinutes(minutes - 1),
            };
        }
        var power = Ch("pv.power_w");
        var counter = Ch("pv.energy_total_kwh");
        db.TelemetryChannels.AddRange(power, counter);
        db.SaveChanges();

        var store = new InMemoryTelemetryStore();
        var raw = new List<RawSample>();
        for (var i = 0; i < minutes; i++)
        {
            raw.Add(new RawSample(power.Id, start.AddMinutes(i), 1200));
            raw.Add(new RawSample(counter.Id, start.AddMinutes(i), 1000 + 0.02 * i));   // 1.2 kWh/h
        }
        store.AppendRawAsync(raw).GetAwaiter().GetResult();

        var job = new TelemetryRollupJob(db, store, NullLogger<TelemetryRollupJob>.Instance);
        return new Fixture(db, store, job, site, power, counter);
    }

    [Fact]
    public async Task Rolls_closed_buckets_up_to_the_grace_and_writes_daily_energy()
    {
        var f = Setup();
        await f.Job.RunAsync(Now);

        // Grace: at 05:00 everything before 04:50 is final, the last 10 minutes are not.
        var state = await f.Db.TelemetryRollupStates.SingleAsync();
        state.WatermarkUtc.Should().Be(new DateTime(2026, 11, 13, 4, 50, 0, DateTimeKind.Utc));

        var fives = await f.Store.ReadAggregatesAsync(new[] { f.Power.Id }, AggregateResolution.FiveMinutes, From, Now);
        fives.Should().HaveCount(22);                         // 03:00 … 04:45
        fives[0].Count.Should().Be(5);
        fives[0].Avg.Should().Be(1200);

        var hours = await f.Store.ReadAggregatesAsync(new[] { f.Power.Id }, AggregateResolution.Hour, From, Now);
        hours.Select(h => h.Count).Should().Equal(60, 50);   // 04:xx is partial until 05:00 closes

        var day = await f.Store.ReadAggregatesAsync(new[] { f.Counter.Id }, AggregateResolution.Day,
            Now.AddDays(-2), Now);
        day.Single().BucketStartUtc.Should().Be(new DateTime(2026, 11, 12, 17, 0, 0, DateTimeKind.Utc));
        TelemetryRollup.Increase(day.Single()).Should().BeApproximately(109 * 0.02, 1e-3);   // 03:00 → 04:49 readings

        // Bridge: the site is LocalGateway, so today's Daily row comes from the counter.
        var daily = await f.Db.EnergyData.SingleAsync(e => e.Granularity == DataGranularity.Daily);
        daily.Timestamp.Should().Be(new DateTime(2026, 11, 13));
        ((double)daily.EnergyGenerationKwh).Should().BeApproximately(2.18, 0.002);
        daily.SelfConsumptionKwh.Should().Be(daily.EnergyGenerationKwh);
    }

    [Fact]
    public async Task Late_data_marks_a_dirty_range_that_the_next_run_recomputes()
    {
        var f = Setup();
        await f.Job.RunAsync(Now);

        // An HA buffer replay: a sample inside the already-final 03:00 bucket, through the real ingest path.
        var gw = new TelemetryGateway { InstallationId = f.Site.Id, Name = "HA", TokenHash = "h", TokenPrefix = "sag_x" };
        f.Db.TelemetryGateways.Add(gw);
        await f.Db.SaveChangesAsync();
        gw = await f.Db.TelemetryGateways.Include(g => g.Installation).FirstAsync();
        var ingest = new TelemetryIngestService(f.Db, f.Store, NullLogger<TelemetryIngestService>.Instance);
        await ingest.IngestTelemetryAsync(gw, JsonDocument.Parse("""
            { "samples": [ { "key": "pv.power_w", "ts": "2026-11-13T03:02:30Z", "value": 9999 } ] }
            """).RootElement, Now, null);
        (await f.Db.TelemetryDirtyRanges.CountAsync()).Should().Be(1);

        await f.Job.RunAsync(Now.AddMinutes(1));

        var bucket = (await f.Store.ReadAggregatesAsync(new[] { f.Power.Id }, AggregateResolution.FiveMinutes,
            From, From.AddMinutes(5))).Single();
        bucket.Count.Should().Be(6);
        bucket.Max.Should().Be(9999);
        var hour = (await f.Store.ReadAggregatesAsync(new[] { f.Power.Id }, AggregateResolution.Hour,
            From, From.AddHours(1))).Single();
        hour.Count.Should().Be(61);
        hour.Max.Should().Be(9999);
        (await f.Db.TelemetryDirtyRanges.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Late_counter_sample_does_not_double_count_the_next_bucket()
    {
        // Counter has a hole at 03:04 that is filled later: the 03:05 bucket's
        // opening reading changes too, and the day's total must stay right.
        var f = Setup();
        var keep = f.Store.AllRaw.Where(s => !(s.ChannelId == f.Counter.Id && s.TsUtc == From.AddMinutes(4))).ToList();
        var store = new InMemoryTelemetryStore();
        await store.AppendRawAsync(keep);
        var job = new TelemetryRollupJob(f.Db, store, NullLogger<TelemetryRollupJob>.Instance);
        await job.RunAsync(Now);

        await store.AppendRawAsync(new[] { new RawSample(f.Counter.Id, From.AddMinutes(4), 1000.08) });
        f.Db.TelemetryDirtyRanges.Add(new TelemetryDirtyRange
            { InstallationId = f.Site.Id, FromUtc = From.AddMinutes(4), ToUtc = From.AddMinutes(4) });
        await f.Db.SaveChangesAsync();
        await job.RunAsync(Now.AddMinutes(1));

        var day = (await store.ReadAggregatesAsync(new[] { f.Counter.Id }, AggregateResolution.Day, Now.AddDays(-2), Now)).Single();
        TelemetryRollup.Increase(day).Should().BeApproximately(109 * 0.02, 1e-3);
    }

    [Fact]
    public async Task Backlog_is_capped_per_run_and_starts_at_the_earliest_channel()
    {
        var start = Now.AddDays(-3).AddMinutes(-2);   // 2026-11-10 04:58
        var f = Setup(start, minutes: 10);
        await f.Job.RunAsync(Now);

        var state = await f.Db.TelemetryRollupStates.SingleAsync();
        state.WatermarkUtc.Should().Be(TelemetryRollup.FiveMinuteStart(start) + TelemetryRollupJob.MaxSpanPerRun);

        await f.Job.RunAsync(Now);
        await f.Job.RunAsync(Now);
        await f.Job.RunAsync(Now);
        (await f.Db.TelemetryRollupStates.SingleAsync()).WatermarkUtc.Should().Be(TelemetryRollup.FiveMinuteStart(Now - TelemetryRollupJob.Grace));
    }

    [Fact]
    public async Task Cloud_sites_get_aggregates_but_no_bridged_daily_row()
    {
        var f = Setup();
        f.Site.TelemetrySource = TelemetrySource.Cloud;
        await f.Db.SaveChangesAsync();
        await f.Job.RunAsync(Now);
        (await f.Db.EnergyData.CountAsync(e => e.Granularity == DataGranularity.Daily)).Should().Be(0);
        f.Store.AllRaw.Should().NotBeEmpty();
    }

    [Fact]
    public void Apply_daily_never_lowers_a_figure()
    {
        var row = new EnergyData { EnergyGenerationKwh = 30, GridExportKwh = 5, GridImportKwh = 2 };
        TelemetryRollupJob.ApplyDaily(row, new DailyEnergy(28, 3, 6, null, 1.5, null));
        row.EnergyGenerationKwh.Should().Be(30);     // ingest's fresher inverter counter stays
        row.GridImportKwh.Should().Be(3);
        row.GridExportKwh.Should().Be(6);
        row.BatteryChargeKwh.Should().Be(1.5m);
        row.BatteryDischargeKwh.Should().BeNull();
        row.SelfConsumptionKwh.Should().Be(24);
    }
}

public class TelemetryRetentionJobTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 20, 30, 0, DateTimeKind.Utc);   // Saturday 03:30 in Thailand

    [Fact]
    public async Task Deletes_only_rows_older_than_each_tier_retention()
    {
        var db = TestDb.Create();
        var (_, site) = TestDb.SeedSite(db);
        db.SystemSettings.Add(new SystemSetting { Key = "Telemetry.RawRetentionDays", Value = "30" });
        db.DeviceEvents.AddRange(
            new DeviceEvent { InstallationId = site.Id, TsUtc = Now.AddDays(-800), Source = "inverter1" },
            new DeviceEvent { InstallationId = site.Id, TsUtc = Now.AddDays(-10), Source = "inverter1" });
        db.SaveChanges();

        var store = new InMemoryTelemetryStore();
        await store.AppendRawAsync(new[]
        {
            new RawSample(1, Now.AddDays(-31), 1), new RawSample(1, Now.AddDays(-29), 2), new RawSample(1, Now.AddHours(-1), 3),
        });
        AggregatePoint A(AggregateResolution r, int daysAgo) => new(1, r, Now.AddDays(-daysAgo), 0, 0, 0, 0, 0, 1);
        await store.UpsertAggregatesAsync(new[]
        {
            A(AggregateResolution.FiveMinutes, 401), A(AggregateResolution.FiveMinutes, 399),
            A(AggregateResolution.Hour, 900), A(AggregateResolution.Day, 900),
        });

        var job = new TelemetryRetentionJob(db, store, new ConfigurationBuilder().Build(),
            NullLogger<TelemetryRetentionJob>.Instance);
        var r = await job.RunAsync(Now);

        r.Should().Be(new TelemetryRetentionJob.Result(1, 1, 1));
        store.AllRaw.Select(s => s.Value).Should().Equal(2, 3);                  // the 30-day setting applies
        (await store.ReadAggregatesAsync(new[] { 1 }, AggregateResolution.FiveMinutes, DateTime.MinValue, Now))
            .Should().ContainSingle();
        (await store.ReadAggregatesAsync(new[] { 1 }, AggregateResolution.Hour, DateTime.MinValue, Now))
            .Should().ContainSingle("hourly aggregates are kept forever");
        (await store.ReadAggregatesAsync(new[] { 1 }, AggregateResolution.Day, DateTime.MinValue, Now))
            .Should().ContainSingle();
        db.DeviceEvents.Should().ContainSingle(e => e.TsUtc == Now.AddDays(-10));
    }
}
