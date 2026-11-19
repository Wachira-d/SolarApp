using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SolarApp.Data;
using SolarApp.Models.Enums;
using SolarApp.Models.Telemetry;
using SolarApp.Services.Telemetry;
using SolarApp.Tests.TestSupport;
using Xunit;

namespace SolarApp.Tests.Services.Telemetry;

/// <summary>
/// The one-off copy from the SQL Server tables into the active store
/// (/Admin/TelemetryStorage). Run between two in-memory stores, and into
/// PostgreSQL when SOLARAPP_TEST_POSTGRES is set.
/// </summary>
public abstract class TelemetryStoreCopierContract
{
    protected static readonly DateTime Day0 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    protected static readonly DateTime Now = new(2026, 11, 19, 6, 0, 0, DateTimeKind.Utc);

    protected abstract ITelemetryStore NewDestination();

    protected sealed record Fixture(ApplicationDbContext Db, InMemoryTelemetryStore Source, ITelemetryStore Destination,
        int SiteId, int[] Channels, int RawTotal, int AggregateTotal);

    /// <summary>
    /// Two channels: A with 3 days of raw (every 10 min) and 5-min/hour/day
    /// aggregates, plus an hour bucket 20 days earlier whose raw has aged out;
    /// B with 2 days. Day buckets start at Thai midnight (17:00 UTC).
    /// </summary>
    protected Fixture Build()
    {
        var db = TestDb.Create();
        var (_, site) = TestDb.SeedSite(db);
        var a = new TelemetryChannel { InstallationId = site.Id, Key = "pv.power_w", FirstTs = Day0.AddDays(-20), LastTs = Now };
        var b = new TelemetryChannel { InstallationId = site.Id, Key = "grid.power_w", FirstTs = Day0, LastTs = Now };
        db.TelemetryChannels.AddRange(a, b);
        db.SaveChanges();

        var source = new InMemoryTelemetryStore();
        var raw = new List<RawSample>();
        var agg = new List<AggregatePoint>();
        void Fill(int channel, int days)
        {
            for (var d = 0; d < days; d++)
            {
                var day = Day0.AddDays(d);
                for (var m = 0; m < 24 * 60; m += 10) raw.Add(new RawSample(channel, day.AddMinutes(m), channel * 1000 + d * 100 + m / 10.0));
                for (var m = 0; m < 24 * 60; m += 5)
                    agg.Add(new AggregatePoint(channel, AggregateResolution.FiveMinutes, day.AddMinutes(m), 1, 3, 2, 1, 3, 1));
                for (var h = 0; h < 24; h++)
                    agg.Add(new AggregatePoint(channel, AggregateResolution.Hour, day.AddHours(h), 0, 5, 2.5, 0, 5, 12));
                agg.Add(new AggregatePoint(channel, AggregateResolution.Day, day.AddHours(17), 0, 9, 4.5, 0, 9, 24));
            }
        }
        Fill(a.Id, 3);
        Fill(b.Id, 2);
        agg.Add(new AggregatePoint(a.Id, AggregateResolution.Hour, Day0.AddDays(-20).AddHours(3), 1, 2, 1.5, 1, 2, 12));
        source.AppendRawAsync(raw).GetAwaiter().GetResult();
        source.UpsertAggregatesAsync(agg).GetAwaiter().GetResult();

        return new Fixture(db, source, NewDestination(), site.Id, new[] { a.Id, b.Id }, raw.Count, agg.Count);
    }

    protected static TelemetryStoreCopier Copier(Fixture f, ITelemetryStore? source = null, int? maxDays = null) =>
        new(f.Db, source ?? f.Source, f.Destination)
        {
            SaveEvery = TimeSpan.Zero, MaxDaysThisRun = maxDays, Clock = () => Now,
        };

    private static readonly DateTime WindowFrom = Day0.AddDays(-30), WindowTo = Day0.AddDays(30);

    protected static async Task<List<RawSample>> RawOf(ITelemetryStore store, Fixture f) =>
        await store.ReadRawAsync(f.Channels, WindowFrom, WindowTo);

    protected static async Task<List<AggregatePoint>> AggregatesOf(ITelemetryStore store, Fixture f)
    {
        var all = new List<AggregatePoint>();
        foreach (var res in new[] { AggregateResolution.FiveMinutes, AggregateResolution.Hour, AggregateResolution.Day })
            all.AddRange(await store.ReadAggregatesAsync(f.Channels, res, WindowFrom, WindowTo));
        return all;
    }

    protected static IEnumerable<object> Keys(IEnumerable<RawSample> raw) =>
        raw.Select(r => (object)(r.ChannelId, r.TsUtc.Ticks, (float)r.Value));

    protected static IEnumerable<object> Keys(IEnumerable<AggregatePoint> agg) =>
        agg.Select(a => (object)(a.ChannelId, a.Resolution, a.BucketStartUtc.Ticks, (float)a.Avg, a.Count));

    protected async Task Check_Copies_raw_and_every_resolution_and_leaves_the_source_alone()
    {
        var f = Build();

        var p = await Copier(f).RunAsync();

        p.Status.Should().Be(TelemetryCopyStatus.Done);
        p.ChannelsTotal.Should().Be(2);
        p.ChannelsDone.Should().Be(2);
        p.RawCopied.Should().Be(f.RawTotal);
        p.AggregatesCopied.Should().Be(f.AggregateTotal);
        p.FinishedUtc.Should().Be(Now);
        (await RawOf(f.Destination, f)).Select(r => (object)(r.ChannelId, r.TsUtc.Ticks, (float)r.Value))
            .Should().BeEquivalentTo(Keys(f.Source.AllRaw));
        Keys(await AggregatesOf(f.Destination, f)).Should().BeEquivalentTo(Keys(await AggregatesOf(f.Source, f)));
        (await RawOf(f.Source, f)).Should().HaveCount(f.RawTotal, "nothing is ever deleted from the source");
        f.Db.TelemetryDirtyRanges.Should().BeEmpty("the destination had nothing of its own on these days");
        (await TelemetryCopyProgress.LoadAsync(f.Db)).Status.Should().Be(TelemetryCopyStatus.Done, "progress is persisted");
    }

    protected async Task Check_A_second_run_copies_nothing_and_creates_no_duplicates()
    {
        var f = Build();
        await Copier(f).RunAsync();

        var again = await Copier(f).RunAsync();

        again.Status.Should().Be(TelemetryCopyStatus.Done);
        again.RawCopied.Should().Be(0);
        again.AggregatesCopied.Should().Be(0);
        again.RawAlreadyThere.Should().Be(f.RawTotal);
        again.AggregatesAlreadyThere.Should().Be(f.AggregateTotal);
        again.AggregatesKept.Should().Be(0);
        (await RawOf(f.Destination, f)).Should().HaveCount(f.RawTotal);
        (await AggregatesOf(f.Destination, f)).Should().HaveCount(f.AggregateTotal);
        f.Db.TelemetryDirtyRanges.Should().BeEmpty();
    }

    protected async Task Check_An_interrupted_run_resumes_where_it_stopped()
    {
        var f = Build();

        // Channel A's days run from Day0 − 20 (an hour bucket only) to Day0 + 2: stop after Day0 + 1.
        var first = await Copier(f, maxDays: 22).RunAsync();
        first.Status.Should().Be(TelemetryCopyStatus.Interrupted);
        first.ResumeChannelId.Should().Be(f.Channels[0]);
        first.ResumeDayUtc.Should().Be(Day0.AddDays(2));
        first.RawCopied.Should().Be(2 * 144);

        var counting = new CountingStore(f.Source);
        var second = await Copier(f, counting).RunAsync();

        second.Status.Should().Be(TelemetryCopyStatus.Done);
        second.RawCopied.Should().Be(f.RawTotal, "counters carry over: the second run copied exactly the rest");
        second.RawAlreadyThere.Should().Be(0, "the days already done were not read again");
        counting.RawDaysRead.Should().NotContain(d => d.Channel == f.Channels[0] && d.From < first.ResumeDayUtc);
        (await RawOf(f.Destination, f)).Should().HaveCount(f.RawTotal);
        Keys(await AggregatesOf(f.Destination, f)).Should().BeEquivalentTo(Keys(await AggregatesOf(f.Source, f)));
    }

    protected async Task Check_Resumes_from_a_saved_position()
    {
        var f = Build();
        await new TelemetryCopyProgress
        {
            Status = TelemetryCopyStatus.Failed, RunId = "r1", ResumeChannelId = f.Channels[1], ResumeDayUtc = Day0.AddDays(1),
            RawCopied = 7, LastError = "boom",
        }.SaveAsync(f.Db);

        var p = await Copier(f).RunAsync();

        p.Status.Should().Be(TelemetryCopyStatus.Done);
        p.LastError.Should().BeNull();
        p.ChannelsDone.Should().Be(2);
        p.RawCopied.Should().Be(7 + 144, "only channel B's second day was left");
        (await f.Destination.ReadRawAsync(new[] { f.Channels[0] }, WindowFrom, WindowTo)).Should().BeEmpty();
        (await f.Destination.ReadRawAsync(new[] { f.Channels[1] }, WindowFrom, WindowTo))
            .Should().HaveCount(144).And.OnlyContain(r => r.TsUtc >= Day0.AddDays(1));
    }

    protected async Task Check_Raw_already_in_the_destination_is_not_appended_again()
    {
        var f = Build();
        // An earlier run died half-way through a day; ingest also wrote one sample there directly.
        var part = f.Source.AllRaw.Where(r => r.ChannelId == f.Channels[0] && r.TsUtc < Day0.AddHours(12)).ToList();
        await f.Destination.AppendRawAsync(part);

        var p = await Copier(f).RunAsync();

        p.RawAlreadyThere.Should().Be(part.Count);
        p.RawCopied.Should().Be(f.RawTotal - part.Count);
        var dest = await RawOf(f.Destination, f);
        dest.Should().HaveCount(f.RawTotal);
        dest.GroupBy(r => (r.ChannelId, r.TsUtc)).Should().OnlyContain(g => g.Count() == 1);
    }

    protected async Task Check_Buckets_rolled_up_in_the_destination_are_kept_and_the_day_is_rolled_up_again()
    {
        var f = Build();
        // After the switch the destination rolled up hour 23:00 of day 2 from its own partial raw.
        var hour = Day0.AddDays(2).AddHours(23);
        var own = new AggregatePoint(f.Channels[0], AggregateResolution.Hour, hour, 7, 7, 7, 7, 7, 1);
        await f.Destination.UpsertAggregatesAsync(new[] { own });

        var p = await Copier(f).RunAsync();

        p.AggregatesKept.Should().Be(1);
        p.AggregatesCopied.Should().Be(f.AggregateTotal - 1);
        (await f.Destination.ReadAggregatesAsync(new[] { f.Channels[0] }, AggregateResolution.Hour, hour, hour.AddHours(1)))
            .Should().ContainSingle().Which.Avg.Should().Be(7);
        p.DaysQueuedForRollup.Should().Be(1);
        var dirty = await f.Db.TelemetryDirtyRanges.SingleAsync();
        (dirty.InstallationId, dirty.FromUtc, dirty.ToUtc).Should().Be((f.SiteId, Day0.AddDays(2), Day0.AddDays(3)));
    }

    /// <summary>Wraps a source and records which (channel, day) raw windows were read.</summary>
    protected sealed class CountingStore : ITelemetryStore, ITelemetryStoreStats
    {
        private readonly InMemoryTelemetryStore _inner;
        public CountingStore(InMemoryTelemetryStore inner) => _inner = inner;
        public List<(int Channel, DateTime From)> RawDaysRead { get; } = new();

        public Task<List<RawSample>> ReadRawAsync(IReadOnlyCollection<int> channelIds, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
        {
            foreach (var c in channelIds) RawDaysRead.Add((c, fromUtc));
            return _inner.ReadRawAsync(channelIds, fromUtc, toUtc, ct);
        }
        public Task AppendRawAsync(IReadOnlyList<RawSample> samples, CancellationToken ct = default) => throw new InvalidOperationException("source is read-only");
        public Task<HashSet<(int ChannelId, DateTime Ts)>> ExistingAsync(IReadOnlyCollection<(int ChannelId, DateTime Ts)> keys, CancellationToken ct = default) => _inner.ExistingAsync(keys, ct);
        public Task<List<AggregatePoint>> ReadAggregatesAsync(IReadOnlyCollection<int> channelIds, AggregateResolution res, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) => _inner.ReadAggregatesAsync(channelIds, res, fromUtc, toUtc, ct);
        public Task UpsertAggregatesAsync(IReadOnlyList<AggregatePoint> points, CancellationToken ct = default) => throw new InvalidOperationException("source is read-only");
        public Task<int> DeleteRawBeforeAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct = default) => throw new InvalidOperationException("source is read-only");
        public Task<int> DeleteAggregatesBeforeAsync(AggregateResolution res, DateTime cutoffUtc, int batchSize, CancellationToken ct = default) => throw new InvalidOperationException("source is read-only");
        public Task<long> CountRawAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) => _inner.CountRawAsync(fromUtc, toUtc, ct);
        public Task<Dictionary<int, (DateTime First, DateTime Last)>> ChannelExtentsAsync(CancellationToken ct = default) => _inner.ChannelExtentsAsync(ct);
        public Task<TelemetryStorageReport> StorageAsync(CancellationToken ct = default) => _inner.StorageAsync(ct);
    }
}

public sealed class InMemoryTelemetryStoreCopierTests : TelemetryStoreCopierContract
{
    protected override ITelemetryStore NewDestination() => new InMemoryTelemetryStore();

    [Fact] public Task Copies_raw_and_every_resolution_and_leaves_the_source_alone() => Check_Copies_raw_and_every_resolution_and_leaves_the_source_alone();
    [Fact] public Task A_second_run_copies_nothing_and_creates_no_duplicates() => Check_A_second_run_copies_nothing_and_creates_no_duplicates();
    [Fact] public Task An_interrupted_run_resumes_where_it_stopped() => Check_An_interrupted_run_resumes_where_it_stopped();
    [Fact] public Task Resumes_from_a_saved_position() => Check_Resumes_from_a_saved_position();
    [Fact] public Task Raw_already_in_the_destination_is_not_appended_again() => Check_Raw_already_in_the_destination_is_not_appended_again();
    [Fact] public Task Buckets_rolled_up_in_the_destination_are_kept_and_the_day_is_rolled_up_again() => Check_Buckets_rolled_up_in_the_destination_are_kept_and_the_day_is_rolled_up_again();

    [Fact]
    public async Task Without_source_extents_the_catalog_dates_bound_the_days()
    {
        var f = Build();
        // A source that cannot report extents: the copier walks FirstTs..LastTs from the channel catalog.
        var p = await new TelemetryStoreCopier(f.Db, new NoStatsStore(f.Source), f.Destination) { Clock = () => Now }.RunAsync();

        p.Status.Should().Be(TelemetryCopyStatus.Done);
        p.RawCopied.Should().Be(f.RawTotal);
        p.AggregatesCopied.Should().Be(f.AggregateTotal);
    }

    [Fact]
    public async Task A_click_while_a_copy_is_queued_or_running_does_nothing()
    {
        var db = TestDb.Create();

        var first = await TelemetryStoreCopier.RequestAsync(db, "admin", null, Now);
        var second = await TelemetryStoreCopier.RequestAsync(db, "admin", null, Now.AddMinutes(1));

        first.Started.Should().BeTrue();
        second.Started.Should().BeFalse();
        second.RunId.Should().Be(first.RunId);
        (await TelemetryCopyProgress.LoadAsync(db)).Status.Should().Be(TelemetryCopyStatus.Queued);

        // A queued run that never started (worker died) is stale after 15 minutes: a click may start it again.
        var later = await TelemetryStoreCopier.RequestAsync(db, "admin", null, Now.Add(TelemetryCopyProgress.StaleAfter).AddMinutes(1));
        later.Started.Should().BeTrue();
        later.RunId.Should().NotBe(first.RunId);
    }

    [Fact]
    public async Task A_job_for_a_superseded_request_does_nothing()
    {
        var f = Build();
        var request = await TelemetryStoreCopier.RequestAsync(f.Db, "admin", null, Now);

        var stale = await Copier(f).RunAsync("some-older-run");

        stale.Status.Should().Be(TelemetryCopyStatus.Queued);
        (await RawOf(f.Destination, f)).Should().BeEmpty();

        var real = await Copier(f).RunAsync(request.RunId);
        real.Status.Should().Be(TelemetryCopyStatus.Done);
        real.RawCopied.Should().Be(f.RawTotal);
    }

    [Fact]
    public async Task A_new_request_after_a_finished_copy_starts_over_and_one_after_a_failure_resumes()
    {
        var db = TestDb.Create();
        await new TelemetryCopyProgress { Status = TelemetryCopyStatus.Done, RawCopied = 50, ResumeChannelId = null }.SaveAsync(db);
        (await TelemetryStoreCopier.RequestAsync(db, "admin", null, Now)).Progress.RawCopied.Should().Be(0);

        await new TelemetryCopyProgress { Status = TelemetryCopyStatus.Failed, RawCopied = 50, ResumeChannelId = 9 }.SaveAsync(db);
        var resumed = (await TelemetryStoreCopier.RequestAsync(db, "admin", null, Now)).Progress;
        (resumed.RawCopied, resumed.ResumeChannelId, resumed.Status).Should().Be((50L, 9, TelemetryCopyStatus.Queued));
    }

    [Fact]
    public async Task A_failure_is_recorded_with_the_position_kept()
    {
        var f = Build();
        var broken = new FailingDestination(failOnAppend: 2);

        var act = () => new TelemetryStoreCopier(f.Db, f.Source, broken) { SaveEvery = TimeSpan.Zero, Clock = () => Now }.RunAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
        var p = await TelemetryCopyProgress.LoadAsync(f.Db);
        p.Status.Should().Be(TelemetryCopyStatus.Failed);
        p.LastError.Should().Contain("disk full");
        p.ResumeChannelId.Should().Be(f.Channels[0]);
        p.ResumeDayUtc.Should().Be(Day0.AddDays(1), "day 0 was done, day 1 failed");
    }

    private sealed class NoStatsStore : ITelemetryStore
    {
        private readonly ITelemetryStore _inner;
        public NoStatsStore(ITelemetryStore inner) => _inner = inner;
        public Task AppendRawAsync(IReadOnlyList<RawSample> samples, CancellationToken ct = default) => _inner.AppendRawAsync(samples, ct);
        public Task<HashSet<(int ChannelId, DateTime Ts)>> ExistingAsync(IReadOnlyCollection<(int ChannelId, DateTime Ts)> keys, CancellationToken ct = default) => _inner.ExistingAsync(keys, ct);
        public Task<List<RawSample>> ReadRawAsync(IReadOnlyCollection<int> channelIds, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) => _inner.ReadRawAsync(channelIds, fromUtc, toUtc, ct);
        public Task<List<AggregatePoint>> ReadAggregatesAsync(IReadOnlyCollection<int> channelIds, AggregateResolution res, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) => _inner.ReadAggregatesAsync(channelIds, res, fromUtc, toUtc, ct);
        public Task UpsertAggregatesAsync(IReadOnlyList<AggregatePoint> points, CancellationToken ct = default) => _inner.UpsertAggregatesAsync(points, ct);
        public Task<int> DeleteRawBeforeAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct = default) => _inner.DeleteRawBeforeAsync(cutoffUtc, batchSize, ct);
        public Task<int> DeleteAggregatesBeforeAsync(AggregateResolution res, DateTime cutoffUtc, int batchSize, CancellationToken ct = default) => _inner.DeleteAggregatesBeforeAsync(res, cutoffUtc, batchSize, ct);
    }

    private sealed class FailingDestination : ITelemetryStore
    {
        private readonly InMemoryTelemetryStore _inner = new();
        private readonly int _failOnAppend;
        private int _appends;
        public FailingDestination(int failOnAppend) => _failOnAppend = failOnAppend;
        public Task AppendRawAsync(IReadOnlyList<RawSample> samples, CancellationToken ct = default) =>
            ++_appends == _failOnAppend ? throw new InvalidOperationException("disk full") : _inner.AppendRawAsync(samples, ct);
        public Task<HashSet<(int ChannelId, DateTime Ts)>> ExistingAsync(IReadOnlyCollection<(int ChannelId, DateTime Ts)> keys, CancellationToken ct = default) => _inner.ExistingAsync(keys, ct);
        public Task<List<RawSample>> ReadRawAsync(IReadOnlyCollection<int> channelIds, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) => _inner.ReadRawAsync(channelIds, fromUtc, toUtc, ct);
        public Task<List<AggregatePoint>> ReadAggregatesAsync(IReadOnlyCollection<int> channelIds, AggregateResolution res, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) => _inner.ReadAggregatesAsync(channelIds, res, fromUtc, toUtc, ct);
        public Task UpsertAggregatesAsync(IReadOnlyList<AggregatePoint> points, CancellationToken ct = default) => _inner.UpsertAggregatesAsync(points, ct);
        public Task<int> DeleteRawBeforeAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct = default) => _inner.DeleteRawBeforeAsync(cutoffUtc, batchSize, ct);
        public Task<int> DeleteAggregatesBeforeAsync(AggregateResolution res, DateTime cutoffUtc, int batchSize, CancellationToken ct = default) => _inner.DeleteAggregatesBeforeAsync(res, cutoffUtc, batchSize, ct);
    }
}

/// <summary>The same copy into PostgreSQL (throw-away schema per test); skipped without SOLARAPP_TEST_POSTGRES.</summary>
public sealed class PostgresTelemetryStoreCopierTests : TelemetryStoreCopierContract, IDisposable
{
    private readonly string _schema = "t_" + Guid.NewGuid().ToString("N")[..12];

    protected override ITelemetryStore NewDestination() =>
        new PostgresTelemetryStore(PostgresTelemetryStoreContractTests.ConnectionString!, _schema);

    [PostgresFact] public Task Copies_raw_and_every_resolution_and_leaves_the_source_alone() => Check_Copies_raw_and_every_resolution_and_leaves_the_source_alone();
    [PostgresFact] public Task A_second_run_copies_nothing_and_creates_no_duplicates() => Check_A_second_run_copies_nothing_and_creates_no_duplicates();
    [PostgresFact] public Task An_interrupted_run_resumes_where_it_stopped() => Check_An_interrupted_run_resumes_where_it_stopped();
    [PostgresFact] public Task Resumes_from_a_saved_position() => Check_Resumes_from_a_saved_position();
    [PostgresFact] public Task Raw_already_in_the_destination_is_not_appended_again() => Check_Raw_already_in_the_destination_is_not_appended_again();
    [PostgresFact] public Task Buckets_rolled_up_in_the_destination_are_kept_and_the_day_is_rolled_up_again() => Check_Buckets_rolled_up_in_the_destination_are_kept_and_the_day_is_rolled_up_again();

    [PostgresFact]
    public async Task Postgres_reports_its_own_stats_after_a_copy()
    {
        var f = Build();
        await Copier(f).RunAsync();
        var stats = (ITelemetryStoreStats)f.Destination;

        (await stats.CountRawAsync(Day0, Day0.AddDays(1))).Should().Be(2 * 144);
        var extents = await stats.ChannelExtentsAsync();
        extents[f.Channels[0]].Should().Be((Day0.AddDays(-20).AddHours(3), Day0.AddDays(2).AddHours(23).AddMinutes(55)));
        var report = await stats.StorageAsync();
        report.Tables.Should().HaveCount(4);
        report.Tables.Single(t => t.Kind == TelemetryTableKind.Raw).Rows.Should().Be(f.RawTotal);
        report.Tables.Single(t => t.Kind == TelemetryTableKind.FiveMinutes).Rows.Should().Be((3 + 2) * 288);
        report.AnyRows.Should().BeTrue();
    }

    public void Dispose()
    {
        if (string.IsNullOrEmpty(PostgresTelemetryStoreContractTests.ConnectionString)) return;
        using var conn = new NpgsqlConnection(PostgresTelemetryStoreContractTests.ConnectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", conn);
        cmd.ExecuteNonQuery();
    }
}
