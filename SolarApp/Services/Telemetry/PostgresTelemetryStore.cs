using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using SolarApp.Models.Enums;

namespace SolarApp.Services.Telemetry;

/// <summary>
/// PostgreSQL store, with TimescaleDB when the extension is installed
/// (DESIGN.md §5.5). Business data stays in SQL Server; only telemetry
/// lives here.
///
/// <list type="bullet">
/// <item><c>telemetry_raw</c> (channel_id, ts, value): with TimescaleDB a
/// hypertable in 1-day chunks, compressed after 7 days segmented by
/// channel — reads of one channel's window touch only that channel's
/// compressed segments.</item>
/// <item><c>telemetry_agg_5m</c>, <c>_1h</c>, <c>_1d</c>: one table per
/// resolution, primary key (channel_id, bucket_start), so retention of the
/// 5-minute tier is a whole-chunk drop instead of a row delete.</item>
/// </list>
/// Without TimescaleDB the same tables are plain PostgreSQL with B-tree and
/// BRIN indexes: correct, just larger on disk.
///
/// The schema is created on first use and is idempotent; nothing has to be
/// run by hand besides creating the database.
/// </summary>
public sealed class PostgresTelemetryStore : ITelemetryStore, ITelemetryStoreStats
{
    /// <summary>Late data may arrive this far back (ingest window 35 days); aggregate chunks stay uncompressed until then.</summary>
    public static readonly TimeSpan RawCompressAfter = TimeSpan.FromDays(7);
    public static readonly TimeSpan AggregateCompressAfter = TimeSpan.FromDays(45);

    private readonly string _connectionString;
    private readonly string _schema;
    private readonly ILogger<PostgresTelemetryStore>? _logger;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private volatile bool _schemaReady;
    private readonly bool _requireTimescale;

    /// <summary>True when the TimescaleDB extension is active (known after the first call).</summary>
    public bool TimescaleEnabled { get; private set; }

    /// <summary>The PostgreSQL schema holding the telemetry tables.</summary>
    public string Schema => _schema;

    public PostgresTelemetryStore(IConfiguration configuration, ILogger<PostgresTelemetryStore> logger)
        : this(configuration.GetConnectionString("TelemetryPostgres")
               ?? throw new InvalidOperationException(
                   "Telemetry:Store is Postgres but ConnectionStrings:TelemetryPostgres is not set"),
            configuration["Telemetry:PostgresSchema"] ?? "public", logger,
            TelemetryStoreSelector.RequiresTimescale(configuration))
    {
    }

    /// <param name="requireTimescale">
    /// Refuse to run without the extension. Set when the operator chose
    /// "TimescaleDB": plain PostgreSQL measured ~80 bytes per raw value
    /// against ~2–3 compressed, so a silent fallback would fill the disk.
    /// </param>
    public PostgresTelemetryStore(string connectionString, string schema = "public",
        ILogger<PostgresTelemetryStore>? logger = null, bool requireTimescale = false)
    {
        _requireTimescale = requireTimescale;
        if (!Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
            throw new ArgumentException("Schema must be a lowercase identifier", nameof(schema));
        _connectionString = connectionString;
        _schema = schema;
        _logger = logger;
    }

    private string Raw => $"\"{_schema}\".telemetry_raw";

    private string Agg(AggregateResolution res) => res switch
    {
        AggregateResolution.FiveMinutes => $"\"{_schema}\".telemetry_agg_5m",
        AggregateResolution.Hour => $"\"{_schema}\".telemetry_agg_1h",
        AggregateResolution.Day => $"\"{_schema}\".telemetry_agg_1d",
        _ => throw new ArgumentOutOfRangeException(nameof(res)),
    };

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    // ── writes ───────────────────────────────────────────────────────

    public async Task AppendRawAsync(IReadOnlyList<RawSample> samples, CancellationToken ct = default)
    {
        if (samples.Count == 0) return;
        await using var conn = await OpenAsync(ct);
        await using var writer = await conn.BeginBinaryImportAsync(
            $"COPY {Raw} (channel_id, ts, value) FROM STDIN (FORMAT BINARY)", ct);
        foreach (var s in samples.OrderBy(s => s.TsUtc))
        {
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(s.ChannelId, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(Utc(s.TsUtc), NpgsqlDbType.TimestampTz, ct);
            await writer.WriteAsync((float)s.Value, NpgsqlDbType.Real, ct);
        }
        await writer.CompleteAsync(ct);
    }

    public async Task UpsertAggregatesAsync(IReadOnlyList<AggregatePoint> points, CancellationToken ct = default)
    {
        if (points.Count == 0) return;
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        foreach (var group in points.GroupBy(p => p.Resolution))
        {
            var table = Agg(group.Key);
            // ON CONFLICT may not touch a row twice in one statement: last point per key wins.
            var rows = group.GroupBy(p => (p.ChannelId, Utc(p.BucketStartUtc))).Select(g => g.Last()).ToList();

            await using (var create = new NpgsqlCommand(
                $"CREATE TEMP TABLE IF NOT EXISTS agg_stage (LIKE {table} INCLUDING DEFAULTS) ON COMMIT DROP; TRUNCATE agg_stage;", conn, tx))
                await create.ExecuteNonQueryAsync(ct);

            await using (var writer = await conn.BeginBinaryImportAsync(
                "COPY agg_stage (channel_id, bucket_start, min, max, avg, first, last, count) FROM STDIN (FORMAT BINARY)", ct))
            {
                foreach (var p in rows)
                {
                    await writer.StartRowAsync(ct);
                    await writer.WriteAsync(p.ChannelId, NpgsqlDbType.Integer, ct);
                    await writer.WriteAsync(Utc(p.BucketStartUtc), NpgsqlDbType.TimestampTz, ct);
                    await writer.WriteAsync((float)p.Min, NpgsqlDbType.Real, ct);
                    await writer.WriteAsync((float)p.Max, NpgsqlDbType.Real, ct);
                    await writer.WriteAsync((float)p.Avg, NpgsqlDbType.Real, ct);
                    await writer.WriteAsync((float)p.First, NpgsqlDbType.Real, ct);
                    await writer.WriteAsync((float)p.Last, NpgsqlDbType.Real, ct);
                    await writer.WriteAsync(p.Count, NpgsqlDbType.Integer, ct);
                }
                await writer.CompleteAsync(ct);
            }

            await using var apply = new NpgsqlCommand(
                $"INSERT INTO {table} (channel_id, bucket_start, min, max, avg, first, last, count) " +
                "SELECT channel_id, bucket_start, min, max, avg, first, last, count FROM agg_stage " +
                "ON CONFLICT (channel_id, bucket_start) DO UPDATE SET min = EXCLUDED.min, max = EXCLUDED.max, " +
                "avg = EXCLUDED.avg, first = EXCLUDED.first, last = EXCLUDED.last, count = EXCLUDED.count; " +
                "DROP TABLE agg_stage;", conn, tx) { CommandTimeout = 120 };
            await apply.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    // ── reads ────────────────────────────────────────────────────────

    public async Task<HashSet<(int ChannelId, DateTime Ts)>> ExistingAsync(
        IReadOnlyCollection<(int ChannelId, DateTime Ts)> keys, CancellationToken ct = default)
    {
        var found = new HashSet<(int, DateTime)>();
        if (keys.Count == 0) return found;
        await using var conn = await OpenAsync(ct);
        foreach (var group in keys.GroupBy(k => k.ChannelId))
        {
            var wanted = group.Select(k => Utc(k.Ts)).ToHashSet();
            await using var cmd = new NpgsqlCommand(
                $"SELECT ts FROM {Raw} WHERE channel_id = @c AND ts BETWEEN @a AND @b", conn);
            cmd.Parameters.AddWithValue("c", group.Key);
            cmd.Parameters.AddWithValue("a", NpgsqlDbType.TimestampTz, wanted.Min());
            cmd.Parameters.AddWithValue("b", NpgsqlDbType.TimestampTz, wanted.Max());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var ts = Utc(r.GetDateTime(0));
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
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT channel_id, ts, value FROM {Raw} WHERE channel_id = ANY(@ids) AND ts >= @a AND ts < @b ORDER BY ts", conn)
        { CommandTimeout = 120 };
        cmd.Parameters.AddWithValue("ids", channelIds.Distinct().ToArray());
        cmd.Parameters.AddWithValue("a", NpgsqlDbType.TimestampTz, Utc(fromUtc));
        cmd.Parameters.AddWithValue("b", NpgsqlDbType.TimestampTz, Utc(toUtc));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new RawSample(r.GetInt32(0), Utc(r.GetDateTime(1)), r.GetFloat(2)));
        return list;
    }

    public async Task<List<AggregatePoint>> ReadAggregatesAsync(IReadOnlyCollection<int> channelIds, AggregateResolution res,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var list = new List<AggregatePoint>();
        if (channelIds.Count == 0) return list;
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT channel_id, bucket_start, min, max, avg, first, last, count FROM {Agg(res)} " +
            "WHERE channel_id = ANY(@ids) AND bucket_start >= @a AND bucket_start < @b ORDER BY bucket_start", conn)
        { CommandTimeout = 120 };
        cmd.Parameters.AddWithValue("ids", channelIds.Distinct().ToArray());
        cmd.Parameters.AddWithValue("a", NpgsqlDbType.TimestampTz, Utc(fromUtc));
        cmd.Parameters.AddWithValue("b", NpgsqlDbType.TimestampTz, Utc(toUtc));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new AggregatePoint(r.GetInt32(0), res, Utc(r.GetDateTime(1)),
                r.GetFloat(2), r.GetFloat(3), r.GetFloat(4), r.GetFloat(5), r.GetFloat(6), r.GetInt32(7)));
        return list;
    }

    // ── retention ────────────────────────────────────────────────────

    public Task<int> DeleteRawBeforeAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct = default) =>
        DeleteBeforeAsync(Raw, "ts", cutoffUtc, batchSize, ct);

    public Task<int> DeleteAggregatesBeforeAsync(AggregateResolution res, DateTime cutoffUtc, int batchSize,
        CancellationToken ct = default) =>
        DeleteBeforeAsync(Agg(res), "bucket_start", cutoffUtc, batchSize, ct);

    /// <summary>
    /// TimescaleDB: drop whole chunks older than the cutoff — instant, no
    /// row-by-row delete, no bloat — and return 0 so the retention job's
    /// batch loop ends (rows in the chunk that straddles the cutoff go with
    /// it on a later day). Plain PostgreSQL: delete one batch by ctid.
    /// </summary>
    private async Task<int> DeleteBeforeAsync(string table, string tsColumn, DateTime cutoffUtc, int batchSize, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        if (TimescaleEnabled)
        {
            await using var drop = new NpgsqlCommand("SELECT count(*) FROM drop_chunks(@t::regclass, older_than => @c)", conn)
            { CommandTimeout = 600 };
            drop.Parameters.AddWithValue("t", table);
            drop.Parameters.AddWithValue("c", NpgsqlDbType.TimestampTz, Utc(cutoffUtc));
            var chunks = Convert.ToInt32(await drop.ExecuteScalarAsync(ct));
            if (chunks > 0) _logger?.LogInformation("Telemetry retention: dropped {Chunks} chunk(s) of {Table}", chunks, table);
            return 0;
        }
        await using var cmd = new NpgsqlCommand(
            $"DELETE FROM {table} WHERE ctid = ANY(ARRAY(SELECT ctid FROM {table} WHERE {tsColumn} < @c LIMIT @n))", conn)
        { CommandTimeout = 300 };
        cmd.Parameters.AddWithValue("c", NpgsqlDbType.TimestampTz, Utc(cutoffUtc));
        cmd.Parameters.AddWithValue("n", batchSize);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── schema ───────────────────────────────────────────────────────

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        if (_schemaReady) return;
        await _schemaLock.WaitAsync(ct);
        try
        {
            if (_schemaReady) return;
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            TimescaleEnabled = await TryEnableTimescaleAsync(conn, ct);
            if (!TimescaleEnabled && _requireTimescale)
                throw new InvalidOperationException(
                    "Telemetry:Store is TimescaleDB but the timescaledb extension is not available in this database. " +
                    "Install TimescaleDB and run CREATE EXTENSION timescaledb as a superuser, or set Telemetry:Store to Postgres.");

            var s = $"\"{_schema}\"";
            await ExecAsync(conn, $@"
CREATE SCHEMA IF NOT EXISTS {s};
CREATE TABLE IF NOT EXISTS {Raw} (
    channel_id integer     NOT NULL,
    ts         timestamptz NOT NULL,
    value      real        NOT NULL);
CREATE INDEX IF NOT EXISTS ix_telemetry_raw_channel_ts ON {Raw} (channel_id, ts);", ct);
            foreach (var res in new[] { AggregateResolution.FiveMinutes, AggregateResolution.Hour, AggregateResolution.Day })
                await ExecAsync(conn, $@"
CREATE TABLE IF NOT EXISTS {Agg(res)} (
    channel_id   integer     NOT NULL,
    bucket_start timestamptz NOT NULL,
    min real NOT NULL, max real NOT NULL, avg real NOT NULL, first real NOT NULL, last real NOT NULL,
    count integer NOT NULL,
    PRIMARY KEY (channel_id, bucket_start));", ct);

            if (TimescaleEnabled)
            {
                await HypertableAsync(conn, Raw, "ts", "1 day", "channel_id", "ts", RawCompressAfter, ct);
                await HypertableAsync(conn, Agg(AggregateResolution.FiveMinutes), "bucket_start", "7 days", "channel_id", "bucket_start", AggregateCompressAfter, ct);
                await HypertableAsync(conn, Agg(AggregateResolution.Hour), "bucket_start", "90 days", "channel_id", "bucket_start", TimeSpan.FromDays(120), ct);
                await HypertableAsync(conn, Agg(AggregateResolution.Day), "bucket_start", "365 days", null, null, null, ct);
            }
            else
            {
                // Retention deletes scan by time; BRIN is tiny on append-ordered data.
                await ExecAsync(conn, $@"
CREATE INDEX IF NOT EXISTS ix_telemetry_raw_ts_brin ON {Raw} USING brin (ts);
CREATE INDEX IF NOT EXISTS ix_telemetry_agg_5m_ts_brin ON {Agg(AggregateResolution.FiveMinutes)} USING brin (bucket_start);", ct);
                _logger?.LogWarning("Telemetry store: PostgreSQL without TimescaleDB — data is not compressed. " +
                                    "Install the timescaledb extension for compression and chunk retention.");
            }
            _schemaReady = true;
            _logger?.LogInformation("Telemetry store: PostgreSQL schema {Schema} ready (TimescaleDB {State})",
                _schema, TimescaleEnabled ? "on" : "off");
        }
        finally { _schemaLock.Release(); }
    }

    private async Task<bool> TryEnableTimescaleAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        try
        {
            await ExecAsync(conn, "CREATE EXTENSION IF NOT EXISTS timescaledb;", ct);
            return true;
        }
        catch (PostgresException ex)
        {
            _logger?.LogInformation("TimescaleDB not available ({Message}); using plain PostgreSQL tables", ex.MessageText);
            return false;
        }
    }

    /// <summary>
    /// Convert to a hypertable (once; existing rows migrate) and set
    /// compression. Each step tolerates "already done" so restarts are safe.
    /// </summary>
    private async Task HypertableAsync(NpgsqlConnection conn, string table, string timeColumn, string chunk,
        string? segmentBy, string? orderBy, TimeSpan? compressAfter, CancellationToken ct)
    {
        // TimescaleDB renamed parts of this API over 2.x (by_range in 2.13,
        // "columnstore" names in 2.18): try the current form, then the older one.
        await FirstThatWorksAsync(conn, ct, $"hypertable {table}",
            $"SELECT create_hypertable('{table}', by_range('{timeColumn}', INTERVAL '{chunk}'), " +
            "if_not_exists => TRUE, migrate_data => TRUE);",
            $"SELECT create_hypertable('{table}', '{timeColumn}', chunk_time_interval => INTERVAL '{chunk}', " +
            "if_not_exists => TRUE, migrate_data => TRUE);");
        if (segmentBy == null || compressAfter == null) return;

        var configured = await TryScalarAsync<bool>(conn, ct,
                             "SELECT EXISTS (SELECT 1 FROM timescaledb_information.hypertable_compression_settings " +
                             $"WHERE hypertable = '{table}'::regclass)")
                         ?? await TryScalarAsync<bool>(conn, ct,
                             "SELECT EXISTS (SELECT 1 FROM timescaledb_information.compression_settings " +
                             $"WHERE format('%I.%I', hypertable_schema, hypertable_name)::regclass = '{table}'::regclass)")
                         ?? false;
        if (!configured)
            await FirstThatWorksAsync(conn, ct, $"compression settings {table}",
                $"ALTER TABLE {table} SET (timescaledb.enable_columnstore = true, timescaledb.segmentby = '{segmentBy}', " +
                $"timescaledb.orderby = '{orderBy} DESC');",
                $"ALTER TABLE {table} SET (timescaledb.compress, timescaledb.compress_segmentby = '{segmentBy}', " +
                $"timescaledb.compress_orderby = '{orderBy} DESC');");

        var days = (int)compressAfter.Value.TotalDays;
        await FirstThatWorksAsync(conn, ct, $"compression policy {table}",
            $"CALL add_columnstore_policy('{table}', after => INTERVAL '{days} days', if_not_exists => TRUE);",
            $"SELECT add_compression_policy('{table}', INTERVAL '{days} days', if_not_exists => TRUE);");
    }

    /// <summary>Run the first statement that succeeds; throw the last error when none does.</summary>
    private async Task FirstThatWorksAsync(NpgsqlConnection conn, CancellationToken ct, string what, params string[] statements)
    {
        PostgresException? last = null;
        foreach (var sql in statements)
        {
            try
            {
                await ExecAsync(conn, sql, ct);
                return;
            }
            catch (PostgresException ex)
            {
                last = ex;
                _logger?.LogDebug("Telemetry store: {What} form failed ({Message}); trying the next form", what, ex.MessageText);
            }
        }
        throw new InvalidOperationException($"TimescaleDB setup failed for {what}: {last?.MessageText}", last);
    }

    private static async Task<T?> TryScalarAsync<T>(NpgsqlConnection conn, CancellationToken ct, string sql) where T : struct
    {
        try { return await ScalarAsync<T>(conn, sql, ct); }
        catch (PostgresException) { return null; }
    }

    /// <summary>On-disk size per table and how much TimescaleDB compression saved — for the admin status line and sizing.</summary>
    public async Task<List<(string Table, long Bytes, long? UncompressedBytes)>> SizesAsync(CancellationToken ct = default)
    {
        var list = new List<(string, long, long?)>();
        await using var conn = await OpenAsync(ct);
        foreach (var table in new[] { Raw, Agg(AggregateResolution.FiveMinutes), Agg(AggregateResolution.Hour), Agg(AggregateResolution.Day) })
        {
            long bytes;
            long? before = null;
            if (TimescaleEnabled)
            {
                bytes = await ScalarAsync<long>(conn, $"SELECT COALESCE(hypertable_size('{table}'), 0)", ct);
                try
                {
                    before = await ScalarAsync<long?>(conn,
                        $"SELECT sum(before_compression_total_bytes)::bigint FROM chunk_compression_stats('{table}')", ct);
                }
                catch (PostgresException) { /* no compression on this table */ }
            }
            else bytes = await ScalarAsync<long>(conn, $"SELECT pg_total_relation_size('{table}')", ct);
            list.Add((table, bytes, before));
        }
        return list;
    }

    // ── stats (read-only, ITelemetryStoreStats) ──────────────────────

    public async Task<long> CountRawAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {Raw} WHERE ts >= @a AND ts < @b", conn) { CommandTimeout = 120 };
        cmd.Parameters.AddWithValue("a", NpgsqlDbType.TimestampTz, Utc(fromUtc));
        cmd.Parameters.AddWithValue("b", NpgsqlDbType.TimestampTz, Utc(toUtc));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<Dictionary<int, (DateTime First, DateTime Last)>> ChannelExtentsAsync(CancellationToken ct = default)
    {
        var map = new Dictionary<int, (DateTime, DateTime)>();
        await using var conn = await OpenAsync(ct);
        var parts = new[] { $"SELECT channel_id, min(ts) AS f, max(ts) AS l FROM {Raw} GROUP BY channel_id" }
            .Concat(new[] { AggregateResolution.FiveMinutes, AggregateResolution.Hour, AggregateResolution.Day }
                .Select(r => $"SELECT channel_id, min(bucket_start), max(bucket_start) FROM {Agg(r)} GROUP BY channel_id"));
        await using var cmd = new NpgsqlCommand(
            $"SELECT channel_id, min(f), max(l) FROM ({string.Join(" UNION ALL ", parts)}) x GROUP BY channel_id", conn)
        { CommandTimeout = 600 };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            map[r.GetInt32(0)] = (Utc(r.GetDateTime(1)), Utc(r.GetDateTime(2)));
        return map;
    }

    /// <summary>
    /// <see cref="SizesAsync"/> plus row counts (planner estimates, exact
    /// only for small never-analysed tables) and, with TimescaleDB, the
    /// compressed chunks' size after compression.
    /// </summary>
    public async Task<TelemetryStorageReport> StorageAsync(CancellationToken ct = default)
    {
        var sizes = await SizesAsync(ct);
        var kinds = new Dictionary<string, TelemetryTableKind>
        {
            [Raw] = TelemetryTableKind.Raw,
            [Agg(AggregateResolution.FiveMinutes)] = TelemetryTableKind.FiveMinutes,
            [Agg(AggregateResolution.Hour)] = TelemetryTableKind.Hour,
            [Agg(AggregateResolution.Day)] = TelemetryTableKind.Day,
        };
        var list = new List<TelemetryTableSize>();
        await using var conn = await OpenAsync(ct);
        foreach (var (table, bytes, before) in sizes)
        {
            long? after = null;
            if (TimescaleEnabled && before is > 0)
                after = await TryScalarAsync<long>(conn, ct,
                            $"SELECT sum(after_compression_total_bytes)::bigint FROM chunk_compression_stats('{table}')")
                        ?? await TryScalarAsync<long>(conn, ct,
                            $"SELECT sum(after_compression_total_bytes)::bigint FROM chunk_columnstore_stats('{table}')");
            list.Add(new TelemetryTableSize(table[(table.LastIndexOf('.') + 1)..], kinds[table],
                await RowEstimateAsync(conn, table, ct), bytes, before is > 0 ? before : null, after is > 0 ? after : null));
        }
        return new TelemetryStorageReport(list);
    }

    /// <summary>
    /// Counting a hypertable of billions of rows takes minutes, so use the
    /// statistics: TimescaleDB's approximate_row_count, else pg_class.reltuples.
    /// Only a table never analysed (fresh, so small) is counted exactly.
    /// </summary>
    private async Task<long?> RowEstimateAsync(NpgsqlConnection conn, string table, CancellationToken ct)
    {
        long? n = null;
        if (TimescaleEnabled) n = await TryScalarAsync<long>(conn, ct, $"SELECT approximate_row_count('{table}')");
        if (n is null or <= 0)
            n = await TryScalarAsync<long>(conn, ct, $"SELECT reltuples::bigint FROM pg_class WHERE oid = '{table}'::regclass");
        if (n is > 0) return n;
        try
        {
            await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {table}", conn) { CommandTimeout = 30 };
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }
        catch (NpgsqlException) { return null; }
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 600 };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? default! : (T)Convert.ChangeType(v, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    private static DateTime Utc(DateTime t) => t.Kind switch
    {
        DateTimeKind.Utc => t,
        DateTimeKind.Local => t.ToUniversalTime(),
        _ => DateTime.SpecifyKind(t, DateTimeKind.Utc),
    };
}

public static class TelemetryStoreSelector
{
    public static bool RequiresTimescale(IConfiguration configuration) =>
        string.Equals(configuration["Telemetry:Store"], "TimescaleDB", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Telemetry:Store" = "Postgres" or "TimescaleDB" (any case) selects <see cref="PostgresTelemetryStore"/>.</summary>
    public static bool UsePostgres(IConfiguration configuration) =>
        configuration["Telemetry:Store"] is { } v &&
        (v.Equals("Postgres", StringComparison.OrdinalIgnoreCase) ||
         v.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase) ||
         v.Equals("TimescaleDB", StringComparison.OrdinalIgnoreCase));
}
