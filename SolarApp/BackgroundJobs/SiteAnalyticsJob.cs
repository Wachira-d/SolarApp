using Hangfire;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Services.Analytics;

namespace SolarApp.BackgroundJobs;

/// <summary>
/// Daily analytics for every site that sends telemetry (DESIGN.md §7–8):
/// battery report and per-day history rows, weekly expected-PV retrain,
/// curtailment, PV vs expected, strings, grid, temperatures, data quality and
/// the TOU what-if. Results land in SiteAnalyticsDaily; the Analytics page
/// only reads them. One site failing never stops the others.
/// </summary>
public class SiteAnalyticsJob
{
    private readonly ApplicationDbContext _db;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SiteAnalyticsJob> _logger;

    public SiteAnalyticsJob(ApplicationDbContext db, IServiceScopeFactory scopes, ILogger<SiteAnalyticsJob> logger)
    {
        _db = db;
        _scopes = scopes;
        _logger = logger;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
    [AutomaticRetry(Attempts = 0)]   // tomorrow's run is the retry; a page can recompute one site on demand
    public Task ExecuteAsync() => RunAsync(DateTime.UtcNow);

    public async Task<List<SiteAnalyticsRunResult>> RunAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var siteIds = await _db.TelemetryChannels.AsNoTracking()
            .Select(c => c.InstallationId).Distinct().OrderBy(i => i).ToListAsync(ct);

        var results = new List<SiteAnalyticsRunResult>();
        foreach (var id in siteIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // A fresh scope per site: one site's tracked rows never pile up into the next.
                using var scope = _scopes.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<SiteAnalyticsService>();
                var r = await svc.ComputeSiteAsync(id, nowUtc, ct);
                results.Add(r);
                if (r.Errors.Count > 0) _logger.LogWarning("Site analytics: {Result}", r);
                else _logger.LogInformation("Site analytics: {Result}", r);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Site analytics failed for site {Id}", id);
            }
        }
        return results;
    }
}
