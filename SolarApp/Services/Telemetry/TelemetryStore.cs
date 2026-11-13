using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.SqlClient;
using SolarApp.Models.Enums;

namespace SolarApp.Services.Telemetry;

public readonly record struct RawSample(int ChannelId, DateTime TsUtc, double Value);

public readonly record struct AggregatePoint(
    int ChannelId, AggregateResolution Resolution, DateTime BucketStartUtc,
    double Min, double Max, double Avg, double First, double Last, int Count);

/// <summary>
/// Where telemetry lives (DESIGN.md §5.4). Everything above this interface
/// — ingestion, rollups, analytics — is independent of the engine, so raw
/// and 5-minute data can move to TimescaleDB/InfluxDB without touching it.
/// </summary>
public interface ITelemetryStore
{
    Task AppendRawAsync(IReadOnlyList<RawSample> samples, CancellationToken ct = default);

    /// <summary>Which of these (channel, ts) pairs are already stored. Used only for replays behind LastTs.</summary>
    Task<HashSet<(int ChannelId, DateTime Ts)>> ExistingAsync(
        IReadOnlyCollection<(int ChannelId, DateTime Ts)> keys, CancellationToken ct = default);

    Task<List<RawSample>> ReadRawAsync(IReadOnlyCollection<int> channelIds, DateTime fromUtc, DateTime toUtc,
        CancellationToken ct = default);

    Task<List<AggregatePoint>> ReadAggregatesAsync(IReadOnlyCollection<int> channelIds, AggregateResolution res,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>Replace aggregates for exactly these (channel, resolution, bucket) keys.</summary>
    Task UpsertAggregatesAsync(IReadOnlyList<AggregatePoint> points, CancellationToken ct = default);

    /// <summary>Delete one batch of raw rows older than the cutoff; returns rows deleted (0 = done).</summary>
    Task<int> DeleteRawBeforeAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct = default);

    Task<int> DeleteAggregatesBeforeAsync(AggregateResolution res, DateTime cutoffUtc, int batchSize,
        CancellationToken ct = default);
}

/// <summary>
/// SQL Server store. Raw rows go to a clustered-columnstore table through
/// SqlBulkCopy — a few thousand rows per request, appended in time order, is
/// the pattern columnstore compresses best. Aggregates are a page-compressed
/// rowstore keyed (ChannelId, Resolution, BucketStart) because rollups
/// rewrite buckets when late data arrives.
/// </summary>
public sealed class SqlServerTelemetryStore : ITelemetryStore
{
    private readonly string _connectionString;

    public SqlServerTelemetryStore(IConfiguration configuration)
    {
        // A separate database can hold telemetry so SQL Express's 10 GB cap
        // applies to it alone (DESIGN.md §5.2); default is the main one.
        _connectionString = configuration.GetConnectionString("TelemetryConnection")
                            ?? configuration.GetConnectionString("DefaultConnection")
                            ?? throw new InvalidOperationException("No connection string for telemetry");
    }

    public async Task AppendRawAsync(IReadOnlyList<RawSample> samples, CancellationToken ct = default)
    {
        if (samples.Count == 0) return;
        var table = new DataTable();
        table.Columns.Add("ChannelId", typeof(int));
        table.Columns.Add("Ts", typeof(DateTime));
        table.Columns.Add("Value", typeof(float));
        foreach (var s in samples.OrderBy(s => s.TsUtc))
            table.Rows.Add(s.ChannelId, s.TsUtc, (float)s.Value);

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, null)
        {
            DestinationTableName = "[TelemetryRaw]",
            BatchSize = 5000,
            BulkCopyTimeout = 60,
        };
        bulk.ColumnMappings.Add("ChannelId", "ChannelId");
        bulk.ColumnMappings.Add("Ts", "Ts");
        bulk.ColumnMappings.Add("Value", "Value");
        await bulk.WriteToServerAsync(table, ct);
    }

    public async Task<HashSet<(int ChannelId, DateTime Ts)>> ExistingAsync(
        IReadOnlyCollection<(int ChannelId, DateTime Ts)> keys, CancellationToken ct = default)
    {
        var found = new HashSet<(int, DateTime)>();
        if (keys.Count == 0) return found;

        // One range query per channel instead of one lookup per sample.
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        foreach (var group in keys.GroupBy(k => k.ChannelId))
        {
            var min = group.Min(k => k.Ts);
            var max = group.Max(k => k.Ts);
            await using var cmd = new SqlCommand(
                "SELECT [Ts] FROM [TelemetryRaw] WHERE [ChannelId] = @c AND [Ts] BETWEEN @a AND @b", conn);
            cmd.Parameters.Add("@c", SqlDbType.Int).Value = group.Key;
            cmd.Parameters.Add("@a", SqlDbType.DateTime2).Value = min;
            cmd.Parameters.Add("@b", SqlDbType.DateTime2).Value = max;
            var wanted = group.Select(k => k.Ts).ToHashSet();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var ts = r.GetDateTime(0);
                if (wanted.Contains(ts)) found.Add((group.Key, ts));
            }
        }
        return found;
    }

    public async Task<List<RawSample>> ReadRawAsync(IReadOnlyCollection<int> channelIds, DateTime fromUtc, DateTime toUtc,
        CancellationToken ct = default)
    {
        var list = new List<RawSample>();
        if (channelIds.Count == 0) return list;
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            $"SELECT [ChannelId],[Ts],[Value] FROM [TelemetryRaw] WHERE [ChannelId] IN ({IdList(channelIds)}) " +
            "AND [Ts] >= @a AND [Ts] < @b ORDER BY [Ts]", conn) { CommandTimeout = 120 };
        cmd.Parameters.Add("@a", SqlDbType.DateTime2).Value = fromUtc;
        cmd.Parameters.Add("@b", SqlDbType.DateTime2).Value = toUtc;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new RawSample(r.GetInt32(0), DateTime.SpecifyKind(r.GetDateTime(1), DateTimeKind.Utc), r.GetFloat(2)));
        return list;
    }

    public async Task<List<AggregatePoint>> ReadAggregatesAsync(IReadOnlyCollection<int> channelIds, AggregateResolution res,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var list = new List<AggregatePoint>();
        if (channelIds.Count == 0) return list;
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT [ChannelId],[BucketStart],[Min],[Max],[Avg],[First],[Last],[Count] FROM [TelemetryAggregates] " +
            $"WHERE [Resolution] = @res AND [ChannelId] IN ({IdList(channelIds)}) " +
            "AND [BucketStart] >= @a AND [BucketStart] < @b ORDER BY [BucketStart]", conn) { CommandTimeout = 120 };
        cmd.Parameters.Add("@res", SqlDbType.TinyInt).Value = (byte)res;
        cmd.Parameters.Add("@a", SqlDbType.DateTime2).Value = fromUtc;
        cmd.Parameters.Add("@b", SqlDbType.DateTime2).Value = toUtc;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new AggregatePoint(r.GetInt32(0), res, DateTime.SpecifyKind(r.GetDateTime(1), DateTimeKind.Utc),
                r.GetFloat(2), r.GetFloat(3), r.GetFloat(4), r.GetFloat(5), r.GetFloat(6), r.GetInt32(7)));
        return list;
    }

    public async Task UpsertAggregatesAsync(IReadOnlyList<AggregatePoint> points, CancellationToken ct = default)
    {
        if (points.Count == 0) return;
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        // Stage, then delete-and-insert the exact keys: simpler and faster
        // than MERGE for a few thousand rows, and correct for late data.
        await using (var create = new SqlCommand(
            "CREATE TABLE #agg ([ChannelId] int, [Resolution] tinyint, [BucketStart] datetime2(0), [Min] real, [Max] real, " +
            "[Avg] real, [First] real, [Last] real, [Count] int)", conn, tx))
            await create.ExecuteNonQueryAsync(ct);

        var table = new DataTable();
        foreach (var (name, type) in new[] { ("ChannelId", typeof(int)), ("Resolution", typeof(byte)), ("BucketStart", typeof(DateTime)),
                     ("Min", typeof(float)), ("Max", typeof(float)), ("Avg", typeof(float)), ("First", typeof(float)),
                     ("Last", typeof(float)), ("Count", typeof(int)) })
            table.Columns.Add(name, type);
        foreach (var p in points)
            table.Rows.Add(p.ChannelId, (byte)p.Resolution, p.BucketStartUtc, (float)p.Min, (float)p.Max, (float)p.Avg,
                (float)p.First, (float)p.Last, p.Count);

        using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx) { DestinationTableName = "#agg", BulkCopyTimeout = 120 })
            await bulk.WriteToServerAsync(table, ct);

        await using (var apply = new SqlCommand(
            "DELETE a FROM [TelemetryAggregates] a JOIN #agg s ON a.[ChannelId] = s.[ChannelId] " +
            "AND a.[Resolution] = s.[Resolution] AND a.[BucketStart] = s.[BucketStart]; " +
            "INSERT INTO [TelemetryAggregates] ([ChannelId],[Resolution],[BucketStart],[Min],[Max],[Avg],[First],[Last],[Count]) " +
            "SELECT [ChannelId],[Resolution],[BucketStart],[Min],[Max],[Avg],[First],[Last],[Count] FROM #agg; DROP TABLE #agg;",
            conn, tx) { CommandTimeout = 120 })
            await apply.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
    }

    public async Task<int> DeleteRawBeforeAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand("DELETE TOP (@n) FROM [TelemetryRaw] WHERE [Ts] < @c", conn) { CommandTimeout = 300 };
        cmd.Parameters.Add("@n", SqlDbType.Int).Value = batchSize;
        cmd.Parameters.Add("@c", SqlDbType.DateTime2).Value = cutoffUtc;
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> DeleteAggregatesBeforeAsync(AggregateResolution res, DateTime cutoffUtc, int batchSize,
        CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "DELETE TOP (@n) FROM [TelemetryAggregates] WHERE [Resolution] = @r AND [BucketStart] < @c", conn) { CommandTimeout = 300 };
        cmd.Parameters.Add("@n", SqlDbType.Int).Value = batchSize;
        cmd.Parameters.Add("@r", SqlDbType.TinyInt).Value = (byte)res;
        cmd.Parameters.Add("@c", SqlDbType.DateTime2).Value = cutoffUtc;
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Integer ids inlined — they are ints from our own catalog, never user text.</summary>
    private static string IdList(IEnumerable<int> ids) => string.Join(",", ids.Distinct());
}

/// <summary>In-memory store for tests and for running without SQL Server.</summary>
public sealed class InMemoryTelemetryStore : ITelemetryStore
{
    private readonly ConcurrentDictionary<(int, DateTime), double> _raw = new();
    private readonly ConcurrentDictionary<(int, AggregateResolution, DateTime), AggregatePoint> _agg = new();

    public IReadOnlyCollection<RawSample> AllRaw =>
        _raw.Select(kv => new RawSample(kv.Key.Item1, kv.Key.Item2, kv.Value)).OrderBy(s => s.TsUtc).ToList();

    public Task AppendRawAsync(IReadOnlyList<RawSample> samples, CancellationToken ct = default)
    {
        foreach (var s in samples) _raw[(s.ChannelId, s.TsUtc)] = s.Value;
        return Task.CompletedTask;
    }

    public Task<HashSet<(int ChannelId, DateTime Ts)>> ExistingAsync(
        IReadOnlyCollection<(int ChannelId, DateTime Ts)> keys, CancellationToken ct = default) =>
        Task.FromResult(keys.Where(k => _raw.ContainsKey((k.ChannelId, k.Ts))).ToHashSet());

    public Task<List<RawSample>> ReadRawAsync(IReadOnlyCollection<int> channelIds, DateTime fromUtc, DateTime toUtc,
        CancellationToken ct = default) =>
        Task.FromResult(_raw
            .Where(kv => channelIds.Contains(kv.Key.Item1) && kv.Key.Item2 >= fromUtc && kv.Key.Item2 < toUtc)
            .Select(kv => new RawSample(kv.Key.Item1, kv.Key.Item2, kv.Value))
            .OrderBy(s => s.TsUtc).ToList());

    public Task<List<AggregatePoint>> ReadAggregatesAsync(IReadOnlyCollection<int> channelIds, AggregateResolution res,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        Task.FromResult(_agg.Values
            .Where(p => p.Resolution == res && channelIds.Contains(p.ChannelId)
                        && p.BucketStartUtc >= fromUtc && p.BucketStartUtc < toUtc)
            .OrderBy(p => p.BucketStartUtc).ToList());

    public Task UpsertAggregatesAsync(IReadOnlyList<AggregatePoint> points, CancellationToken ct = default)
    {
        foreach (var p in points) _agg[(p.ChannelId, p.Resolution, p.BucketStartUtc)] = p;
        return Task.CompletedTask;
    }

    public Task<int> DeleteRawBeforeAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct = default)
    {
        var keys = _raw.Keys.Where(k => k.Item2 < cutoffUtc).Take(batchSize).ToList();
        foreach (var k in keys) _raw.TryRemove(k, out _);
        return Task.FromResult(keys.Count);
    }

    public Task<int> DeleteAggregatesBeforeAsync(AggregateResolution res, DateTime cutoffUtc, int batchSize,
        CancellationToken ct = default)
    {
        var keys = _agg.Keys.Where(k => k.Item2 == res && k.Item3 < cutoffUtc).Take(batchSize).ToList();
        foreach (var k in keys) _agg.TryRemove(k, out _);
        return Task.FromResult(keys.Count);
    }
}
