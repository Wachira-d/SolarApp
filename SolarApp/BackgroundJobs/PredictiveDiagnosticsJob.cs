using Hangfire;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.LineApi;
using SolarApp.Services.PredictiveDiagnostics;

namespace SolarApp.BackgroundJobs;

/// <summary>
/// Runs predictive diagnostics daily (default: 08:00 Thailand time / 01:00 UTC).
/// Analyzes historical data, identifies patterns, matches known fault codes,
/// and sends predictive LINE alerts with check/fix steps.
///
/// De-duplication: same prediction for same installation is not sent again within 7 days.
/// </summary>
public class PredictiveDiagnosticsJob
{
    private readonly ApplicationDbContext _context;
    private readonly PredictiveDiagnosticsEngine _engine;
    private readonly ILineMessagingService _lineService;
    private readonly ILogger<PredictiveDiagnosticsJob> _logger;

    public PredictiveDiagnosticsJob(
        ApplicationDbContext context,
        PredictiveDiagnosticsEngine engine,
        ILineMessagingService lineService,
        ILogger<PredictiveDiagnosticsJob> logger)
    {
        _context = context;
        _engine = engine;
        _lineService = lineService;
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

        if (predictions.Count == 0)
        {
            _logger.LogInformation("Predictive diagnostics: no issues detected");
            return;
        }

        // De-dup: don't send the same prediction within 7 days
        var weekAgo = DateTime.UtcNow.AddDays(-7);
        var recentPredictions = await _context.SystemAlerts
            .Where(a => a.AlertType == AlertType.PredictiveDiagnostic
                && a.OccurredAt >= weekAgo)
            .Select(a => new { a.InstallationId, a.Title })
            .ToListAsync();

        var recentSet = new HashSet<string>(
            recentPredictions.Select(r => $"{r.InstallationId}|{r.Title}"));

        var alertGroupId = await _context.SystemSettings
            .Where(s => s.Key == "Line.AlertGroupId")
            .Select(s => s.Value)
            .FirstOrDefaultAsync();

        int created = 0, sent = 0;

        foreach (var pred in predictions)
        {
            var dedupKey = $"{pred.InstallationId}|{pred.Prediction}";
            if (recentSet.Contains(dedupKey)) continue;
            recentSet.Add(dedupKey);

            // Create SystemAlert record
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
                IsNotified = false,
            });
            created++;

            // Send LINE notification directly (predictive alerts use different format)
            if (!string.IsNullOrWhiteSpace(alertGroupId) && sent < 20)
            {
                try
                {
                    var installation = await _context.Installations
                        .Include(i => i.Customer)
                        .FirstOrDefaultAsync(i => i.Id == pred.InstallationId);

                    await _lineService.SendPredictiveAlertAsync(
                        alertGroupId, pred.SiteName, pred.Prediction,
                        pred.Evidence, pred.CheckSteps, pred.FixSteps,
                        pred.RiskLevel, pred.KnownCaseRef);
                    sent++;

                    // Also send to customer if they have LINE
                    if (installation?.Customer?.LineUserId != null)
                    {
                        await _lineService.SendPredictiveAlertAsync(
                            installation.Customer.LineUserId, pred.SiteName, pred.Prediction,
                            pred.Evidence, pred.CheckSteps, pred.FixSteps,
                            pred.RiskLevel, pred.KnownCaseRef);
                        sent++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send predictive LINE for {Site}", pred.SiteName);
                }
            }
        }

        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Predictive diagnostics: {Total} issues found, {Created} alerts created, {Sent} LINE notifications sent",
            predictions.Count, created, sent);
    }
}
