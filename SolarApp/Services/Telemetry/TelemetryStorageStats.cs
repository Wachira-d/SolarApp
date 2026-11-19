namespace SolarApp.Services.Telemetry;

/// <summary>
/// Read-only storage figures a telemetry store can report about itself, for
/// /Admin/TelemetryStorage and the one-off copy from SQL Server. Kept apart
/// from <see cref="ITelemetryStore"/> so ingestion, rollups and analytics
/// never depend on it.
/// </summary>
public interface ITelemetryStoreStats
{
    /// <summary>Raw samples with a timestamp in [from, to).</summary>
    Task<long> CountRawAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>Earliest and latest timestamp per channel, over raw samples and every aggregate resolution.</summary>
    Task<Dictionary<int, (DateTime First, DateTime Last)>> ChannelExtentsAsync(CancellationToken ct = default);

    /// <summary>Rows and on-disk bytes per table, as far as this store and its permissions can tell.</summary>
    Task<TelemetryStorageReport> StorageAsync(CancellationToken ct = default);
}

public enum TelemetryTableKind
{
    Raw,
    /// <summary>SQL Server keeps every resolution in one TelemetryAggregates table.</summary>
    AllAggregates,
    FiveMinutes,
    Hour,
    Day,
}

/// <param name="BeforeCompressionBytes">TimescaleDB: size of the compressed chunks before compression.</param>
/// <param name="AfterCompressionBytes">TimescaleDB: size of the same chunks after compression.</param>
public sealed record TelemetryTableSize(
    string Table, TelemetryTableKind Kind, long? Rows, long? Bytes,
    long? BeforeCompressionBytes = null, long? AfterCompressionBytes = null)
{
    /// <summary>Bytes the compressed chunks no longer take.</summary>
    public long? CompressionSavedBytes =>
        BeforeCompressionBytes is > 0 && AfterCompressionBytes is { } after ? BeforeCompressionBytes - after : null;

    /// <summary>before ÷ after for the compressed chunks, e.g. 20 means 20× smaller.</summary>
    public double? CompressionRatio =>
        BeforeCompressionBytes is > 0 && AfterCompressionBytes is > 0 ? (double)BeforeCompressionBytes / AfterCompressionBytes.Value : null;
}

/// <param name="HasRows">Set when the row counts are unknown (no permission) but a cheap existence check worked.</param>
/// <param name="Note">Plain-language Thai note when some figures could not be read.</param>
public sealed record TelemetryStorageReport(IReadOnlyList<TelemetryTableSize> Tables, bool? HasRows = null, string? Note = null)
{
    /// <summary>True/false when known; null when neither counts nor the existence check worked.</summary>
    public bool? AnyRows =>
        HasRows ?? (Tables.Any(t => t.Rows > 0) ? true : Tables.All(t => t.Rows != null) ? false : null);

    public long? TotalBytes => Tables.Count > 0 && Tables.All(t => t.Bytes != null) ? Tables.Sum(t => t.Bytes!.Value) : null;
}

/// <summary>
/// Rough steady-state disk use: today's ingest rate kept for the configured
/// retention. Bytes per row come from the active store when it holds enough
/// rows to measure, else from the reference figures in DESIGN.md §5.2/§5.5.
/// An estimate — hourly/daily tiers grow forever, so one year of them is included.
/// </summary>
public sealed record TelemetryCapacityEstimate(
    double BytesPerRaw, bool RawMeasured,
    double BytesPerAggregate, bool AggregateMeasured,
    long RawPerDay, long FiveMinutePerDay, long HourAndDayPerDay,
    int RawDays, int FiveMinuteDays,
    double RawBytes, double FiveMinuteBytes, double HourAndDayBytesPerYear,
    int ActiveSites, double? BytesPerSite, long? SitesPer100Gb)
{
    public const double Gb = 1024d * 1024 * 1024;
    /// <summary>Fewer rows than this make bytes/row meaningless: an empty table is still a few index pages.</summary>
    public const long MinRowsToMeasure = 10_000;

    public double ProjectedBytes => RawBytes + FiveMinuteBytes + HourAndDayBytesPerYear;
    public double ProjectedGb => ProjectedBytes / Gb;

    /// <summary>Bytes per row from a table, or null when it holds too few rows to say.</summary>
    public static double? Measure(long? bytes, long? rows) =>
        bytes is > 0 && rows is { } r && r >= MinRowsToMeasure ? (double)bytes.Value / r : null;

    /// <param name="rawPerDay">Raw samples stored in the last 24 hours.</param>
    /// <param name="activeChannels">Channels that reported in the last 24 hours.</param>
    /// <param name="activeSites">Sites that reported in the last 24 hours.</param>
    public static TelemetryCapacityEstimate Compute(
        double? measuredBytesPerRaw, double? measuredBytesPerAggregate,
        double referenceBytesPerRaw, double referenceBytesPerAggregate,
        long rawPerDay, int activeChannels, int activeSites, int rawDays, int fiveMinuteDays)
    {
        var perRaw = measuredBytesPerRaw ?? referenceBytesPerRaw;
        var perAgg = measuredBytesPerAggregate ?? referenceBytesPerAggregate;

        // A channel makes at most one bucket per period, and none without samples.
        var five = Math.Min(rawPerDay, (long)activeChannels * 288);
        var hourDay = Math.Min(rawPerDay, (long)activeChannels * 24) + Math.Min(rawPerDay, activeChannels);

        var rawBytes = perRaw * rawPerDay * rawDays;
        var fiveBytes = perAgg * five * fiveMinuteDays;
        var hourDayBytes = perAgg * hourDay * 365;
        var total = rawBytes + fiveBytes + hourDayBytes;

        double? perSite = activeSites > 0 && total > 0 ? total / activeSites : null;
        long? fit = perSite is > 0 ? (long)Math.Floor(100 * Gb / perSite.Value) : null;

        return new TelemetryCapacityEstimate(perRaw, measuredBytesPerRaw != null, perAgg, measuredBytesPerAggregate != null,
            rawPerDay, five, hourDay, rawDays, fiveMinuteDays, rawBytes, fiveBytes, hourDayBytes,
            activeSites, perSite, fit);
    }
}
