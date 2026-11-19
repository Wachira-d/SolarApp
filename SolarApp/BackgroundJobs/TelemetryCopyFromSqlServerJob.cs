using Hangfire;
using SolarApp.Data;
using SolarApp.Services.Telemetry;

namespace SolarApp.BackgroundJobs;

/// <summary>
/// One-off, started from /Admin/TelemetryStorage: copies the telemetry that
/// arrived while Telemetry:Store was SQL Server into the active PostgreSQL /
/// TimescaleDB store. The work is <see cref="TelemetryStoreCopier"/>; this
/// only wires the two stores. Nothing is deleted from SQL Server.
/// </summary>
public class TelemetryCopyFromSqlServerJob
{
    private readonly ApplicationDbContext _context;
    private readonly ITelemetryStore _store;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TelemetryCopyFromSqlServerJob> _logger;

    public TelemetryCopyFromSqlServerJob(ApplicationDbContext context, ITelemetryStore store, IConfiguration configuration,
        ILogger<TelemetryCopyFromSqlServerJob> logger)
    {
        _context = context;
        _store = store;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// A second job waits a minute for the first one's lock, then gives up
    /// (and the copier also ignores a run id that is not the current request).
    /// No automatic retry: a failure shows on the page, and the next click
    /// carries on from the saved position. Hangfire passes its shutdown token
    /// as <paramref name="ct"/>; an app recycle saves the position and the job
    /// resumes after restart.
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(string runId, CancellationToken ct)
    {
        if (_store is not PostgresTelemetryStore)
        {
            await TelemetryStoreCopier.RecordFailureAsync(_context,
                "ที่เก็บ telemetry ที่ใช้อยู่ไม่ใช่ PostgreSQL จึงไม่มีที่ให้คัดลอกไป", DateTime.UtcNow, ct);
            return;
        }
        var source = SqlServerTelemetryStore.FromConfiguration(_configuration);
        if (source == null)
        {
            await TelemetryStoreCopier.RecordFailureAsync(_context,
                "ไม่พบ connection string ของ SQL Server (TelemetryConnection / DefaultConnection)", DateTime.UtcNow, ct);
            return;
        }

        _logger.LogInformation("Telemetry copy from SQL Server to PostgreSQL: run {RunId} starting", runId);
        await new TelemetryStoreCopier(_context, source, _store, _logger).RunAsync(runId, ct);
    }
}
