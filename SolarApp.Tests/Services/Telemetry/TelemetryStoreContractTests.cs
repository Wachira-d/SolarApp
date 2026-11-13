using FluentAssertions;
using Npgsql;
using SolarApp.Models.Enums;
using SolarApp.Services.Analytics;
using SolarApp.Services.Telemetry;
using SolarApp.Tests.Services.Analytics;
using Xunit;

namespace SolarApp.Tests.Services.Telemetry;

/// <summary>
/// One behaviour contract for every ITelemetryStore. The PostgreSQL run needs
/// a database: set SOLARAPP_TEST_POSTGRES to a connection string (each run
/// uses a throw-away schema); without it those tests are reported as skipped.
/// </summary>
public abstract class TelemetryStoreContractTests
{
    protected abstract ITelemetryStore Store { get; }

    private static readonly DateTime T0 = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    protected async Task Check_Appends_and_reads_raw_in_time_order_with_half_open_window()
    {
        await Store.AppendRawAsync(new[]
        {
            new RawSample(1, T0.AddMinutes(2), 30), new RawSample(1, T0, 10), new RawSample(1, T0.AddMinutes(1), 20.5),
            new RawSample(2, T0, 99),
        });

        var rows = await Store.ReadRawAsync(new[] { 1 }, T0, T0.AddMinutes(2));

        rows.Select(r => r.Value).Should().Equal(10, 20.5);
        rows.Should().OnlyContain(r => r.TsUtc.Kind == DateTimeKind.Utc);
        (await Store.ReadRawAsync(new[] { 1, 2 }, T0, T0.AddHours(1))).Should().HaveCount(4);
        (await Store.ReadRawAsync(Array.Empty<int>(), T0, T0.AddHours(1))).Should().BeEmpty();
    }

    protected async Task Check_Existing_reports_only_stored_pairs()
    {
        await Store.AppendRawAsync(new[] { new RawSample(5, T0, 1), new RawSample(5, T0.AddMinutes(3), 1) });
        var found = await Store.ExistingAsync(new[] { (5, T0), (5, T0.AddMinutes(1)), (5, T0.AddMinutes(3)), (6, T0) });
        found.Should().BeEquivalentTo(new[] { (5, T0), (5, T0.AddMinutes(3)) });
    }

    protected async Task Check_Upsert_replaces_buckets_and_keeps_resolutions_apart()
    {
        AggregatePoint P(AggregateResolution r, int minute, double avg) =>
            new(7, r, T0.AddMinutes(minute), avg - 1, avg + 1, avg, avg, avg, 5);
        await Store.UpsertAggregatesAsync(new[]
        {
            P(AggregateResolution.FiveMinutes, 0, 100), P(AggregateResolution.FiveMinutes, 5, 200),
            P(AggregateResolution.Hour, 0, 150),
        });
        // Late data: bucket 0 recomputed, a duplicate key in one batch keeps the last.
        await Store.UpsertAggregatesAsync(new[]
        {
            P(AggregateResolution.FiveMinutes, 0, 110), P(AggregateResolution.FiveMinutes, 10, 1), P(AggregateResolution.FiveMinutes, 10, 300),
        });

        var five = await Store.ReadAggregatesAsync(new[] { 7 }, AggregateResolution.FiveMinutes, T0, T0.AddHours(1));
        five.Select(a => a.Avg).Should().Equal(110, 200, 300);
        five[0].Min.Should().Be(109);
        five[0].Count.Should().Be(5);
        five.Should().OnlyContain(a => a.Resolution == AggregateResolution.FiveMinutes && a.BucketStartUtc.Kind == DateTimeKind.Utc);
        (await Store.ReadAggregatesAsync(new[] { 7 }, AggregateResolution.Hour, T0, T0.AddHours(1)))
            .Should().ContainSingle().Which.Avg.Should().Be(150);
        (await Store.ReadAggregatesAsync(new[] { 7 }, AggregateResolution.Day, T0, T0.AddHours(1))).Should().BeEmpty();
    }

    protected async Task Check_Retention_removes_only_rows_before_the_cutoff()
    {
        await Store.AppendRawAsync(Enumerable.Range(0, 10).Select(d => new RawSample(9, T0.AddDays(d), d)).ToList());
        await Store.UpsertAggregatesAsync(Enumerable.Range(0, 10)
            .Select(d => new AggregatePoint(9, AggregateResolution.FiveMinutes, T0.AddDays(d), 0, 0, d, 0, 0, 1)).ToList());

        var cutoff = T0.AddDays(5);
        var total = 0;
        for (int n, i = 0; i < 20 && (n = await Store.DeleteRawBeforeAsync(cutoff, 3)) > 0; i++) total += n;
        for (var i = 0; i < 20 && await Store.DeleteAggregatesBeforeAsync(AggregateResolution.FiveMinutes, cutoff, 3) > 0; i++) { }

        // TimescaleDB drops whole chunks, so a straddling chunk may survive a day; nothing at or after the cutoff may go.
        var raw = await Store.ReadRawAsync(new[] { 9 }, T0, T0.AddDays(11));
        raw.Select(r => r.Value).Should().Contain(new double[] { 5, 6, 7, 8, 9 });
        raw.Should().OnlyContain(r => r.TsUtc >= cutoff || AllowsChunkSlack);
        var agg = await Store.ReadAggregatesAsync(new[] { 9 }, AggregateResolution.FiveMinutes, T0, T0.AddDays(11));
        agg.Select(a => a.Avg).Should().Contain(new double[] { 5, 6, 7, 8, 9 });
        agg.Should().OnlyContain(a => a.BucketStartUtc >= cutoff || AllowsChunkSlack);
    }

    protected virtual bool AllowsChunkSlack => false;

    protected async Task Check_Full_pipeline_rollup_and_analytics_run_on_this_store()
    {
        var f = SiteAnalyticsServiceTests.Build(telemetryStore: Store);
        await SiteAnalyticsServiceTests.RollupAsync(f);

        var r = await f.Service.ComputeSiteAsync(f.Site.Id, SiteAnalyticsServiceTests.Now);

        r.Errors.Should().BeEmpty();
        r.Computed.Should().Contain(new[] { SiteAnalyticsKinds.Battery, SiteAnalyticsKinds.Pv, SiteAnalyticsKinds.Tou, SiteAnalyticsKinds.Grid });
        var (battery, _) = await f.Service.LatestAsync<BatteryReport>(f.Site.Id, SiteAnalyticsKinds.Battery);
        battery!.Efc.Should().BeGreaterThan(1);
    }
}

public sealed class InMemoryTelemetryStoreContractTests : TelemetryStoreContractTests
{
    protected override ITelemetryStore Store { get; } = new InMemoryTelemetryStore();

    [Fact] public Task Appends_and_reads_raw_in_time_order_with_half_open_window() => Check_Appends_and_reads_raw_in_time_order_with_half_open_window();
    [Fact] public Task Existing_reports_only_stored_pairs() => Check_Existing_reports_only_stored_pairs();
    [Fact] public Task Upsert_replaces_buckets_and_keeps_resolutions_apart() => Check_Upsert_replaces_buckets_and_keeps_resolutions_apart();
    [Fact] public Task Retention_removes_only_rows_before_the_cutoff() => Check_Retention_removes_only_rows_before_the_cutoff();
    [Fact] public Task Full_pipeline_rollup_and_analytics_run_on_this_store() => Check_Full_pipeline_rollup_and_analytics_run_on_this_store();
}

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(PostgresTelemetryStoreContractTests.ConnectionString))
            Skip = "Set SOLARAPP_TEST_POSTGRES to run against PostgreSQL";
    }
}

public sealed class PostgresTelemetryStoreContractTests : TelemetryStoreContractTests, IDisposable
{
    public static string? ConnectionString => Environment.GetEnvironmentVariable("SOLARAPP_TEST_POSTGRES");

    private readonly string _schema = "t_" + Guid.NewGuid().ToString("N")[..12];
    private readonly PostgresTelemetryStore? _store;

    public PostgresTelemetryStoreContractTests()
    {
        if (!string.IsNullOrEmpty(ConnectionString)) _store = new PostgresTelemetryStore(ConnectionString, _schema);
    }

    protected override ITelemetryStore Store => _store!;
    protected override bool AllowsChunkSlack => _store?.TimescaleEnabled == true;

    // The contract, with the skip-aware attribute.
    [PostgresFact] public Task Appends_and_reads_raw_in_time_order_with_half_open_window() => Check_Appends_and_reads_raw_in_time_order_with_half_open_window();
    [PostgresFact] public Task Existing_reports_only_stored_pairs() => Check_Existing_reports_only_stored_pairs();
    [PostgresFact] public Task Upsert_replaces_buckets_and_keeps_resolutions_apart() => Check_Upsert_replaces_buckets_and_keeps_resolutions_apart();
    [PostgresFact] public Task Retention_removes_only_rows_before_the_cutoff() => Check_Retention_removes_only_rows_before_the_cutoff();
    [PostgresFact] public Task Full_pipeline_rollup_and_analytics_run_on_this_store() => Check_Full_pipeline_rollup_and_analytics_run_on_this_store();

    [PostgresFact]
    public async Task Schema_creation_is_idempotent_and_reports_sizes()
    {
        await _store!.EnsureSchemaAsync();
        await new PostgresTelemetryStore(ConnectionString!, _schema).EnsureSchemaAsync();   // second process start
        var sizes = await _store.SizesAsync();
        sizes.Should().HaveCount(4).And.OnlyContain(s => s.Bytes > 0);
    }

    [PostgresFact]
    public async Task Requiring_timescale_fails_loudly_when_the_extension_is_missing()
    {
        var probe = new PostgresTelemetryStore(ConnectionString!, _schema);
        await probe.EnsureSchemaAsync();
        if (probe.TimescaleEnabled) return;   // extension installed here: the strict path has nothing to refuse

        var strict = new PostgresTelemetryStore(ConnectionString!, _schema, requireTimescale: true);
        var act = () => strict.EnsureSchemaAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*timescaledb*");
    }

    public void Dispose()
    {
        if (string.IsNullOrEmpty(ConnectionString)) return;
        using var conn = new NpgsqlConnection(ConnectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", conn);
        cmd.ExecuteNonQuery();
    }
}
