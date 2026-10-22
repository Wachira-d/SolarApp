using Hangfire;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.PredictiveDiagnostics;

namespace SolarApp.BackgroundJobs;

/// <summary>
/// Runs predictive diagnostics daily (default: 08:00 Thailand time / 01:00 UTC).
/// Analyzes historical data, identifies patterns, matches known fault codes,
/// and creates dashboard-only SystemAlert records.
///
/// IMPORTANT: Predictive alerts are dashboard-only — no LINE push.
/// They appear in Admin > Alerts dashboard under AlertType.PredictiveDiagnostic.
///
/// De-duplication: same prediction for same installation is not created
/// again within 7 days.
/// </summary>
public class PredictiveDiagnosticsJob
{
    private readonly ApplicationDbContext _context;
    private readonly PredictiveDiagnosticsEngine _engine;
    private readonly ILogger<PredictiveDiagnosticsJob> _logger;

    public PredictiveDiagnosticsJob(
        ApplicationDbContext context,
        PredictiveDiagnosticsEngine engine,
        ILogger<PredictiveDiagnosticsJob> logger)
    {
        _context = context;
        _engine = engine;
        _logger = logger;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public async Task ExecuteAsync()
    {
        _logger.LogInformation("Predictive diagnostics: starting analysis");

        List<PredictiveAlert> predictions;
        try
        {
            predictions = await _engine.AnalyzeAllInstallationsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Predictive diagnostics engine failed");
            return;
        }

        // Close predictions the engine no longer makes. Nothing ever closed
        // them, so they accumulated on the dashboard indefinitely — after the
        // panels were cleaned, after the season passed, after the site was
        // fixed. The engine re-evaluates every site daily, so "not predicted
        // today" means the condition behind it no longer holds. (Skipped when
        // the engine threw above — an empty result then means nothing.)
        var currentKeys = new HashSet<string>(
            predictions.Select(p => $"{p.InstallationId}|{p.Prediction}"));
        var openPredictions = await _context.SystemAlerts
            .Where(a => a.AlertType == AlertType.PredictiveDiagnostic && a.ResolvedAt == null)
            .ToListAsync();
        var closed = 0;
        foreach (var a in openPredictions)
        {
            if (currentKeys.Contains($"{a.InstallationId}|{a.Title}")) continue;
            a.ResolvedAt = DateTime.UtcNow;
            closed++;
        }
        if (closed > 0)
        {
            await _context.SaveChangesAsync();
            _logger.LogInformation("Predictive diagnostics: closed {Count} prediction(s) no longer made", closed);
        }

        if (predictions.Count == 0)
        {
            _logger.LogInformation("Predictive diagnostics: no issues detected");
            return;
        }

        // De-dup: skip a prediction that is still open, or was raised in the
        // last 7 days. The open check is new — a prediction older than a week
        // that was still open used to get a second identical row beside it.
        var weekAgo = DateTime.UtcNow.AddDays(-7);
        var recentPredictions = await _context.SystemAlerts
            .Where(a => a.AlertType == AlertType.PredictiveDiagnostic
                && (a.OccurredAt >= weekAgo || a.ResolvedAt == null))
            .Select(a => new { a.InstallationId, a.Title })
            .ToListAsync();

        var recentSet = new HashSet<string>(
            recentPredictions.Select(r => $"{r.InstallationId}|{r.Title}"));

        int created = 0;

        foreach (var pred in predictions)
        {
            var dedupKey = $"{pred.InstallationId}|{pred.Prediction}";
            if (recentSet.Contains(dedupKey)) continue;
            recentSet.Add(dedupKey);

            var rootCause = $"หลักฐาน:\n{pred.Evidence}\n\n" +
                $"สิ่งที่ต้องตรวจ:\n{string.Join("\n", pred.CheckSteps.Select(s => $"  • {s}"))}\n\n" +
                $"วิธีแก้ไข:\n{string.Join("\n", pred.FixSteps.Select(s => $"  • {s}"))}" +
                (string.IsNullOrEmpty(pred.KnownCaseRef) ? "" : $"\n\nอ้างอิง: {pred.KnownCaseRef}");

            _context.SystemAlerts.Add(new SystemAlert
            {
                InstallationId = pred.InstallationId,
                AlertType = AlertType.PredictiveDiagnostic,
                Severity = pred.RiskLevel == "High" ? AlertSeverity.High : AlertSeverity.Medium,
                Title = pred.Prediction,
                Message = pred.Evidence,
                RootCauseAnalysis = rootCause,
                // Dashboard-only — IsNotified=true blocks SystemHealthMonitorJob
                // from sending LINE notifications for these alerts.
                IsNotified = true,
            });
            created++;
        }

        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Predictive diagnostics: {Total} issues found, {Created} dashboard alerts created (LINE push disabled)",
            predictions.Count, created);
    }
}
