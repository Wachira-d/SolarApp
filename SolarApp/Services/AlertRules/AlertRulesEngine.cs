using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Services.AlertRules;

public interface IAlertRulesEngine
{
    Task<int> EvaluateAllAsync(CancellationToken cancellationToken = default);
}

public class AlertRulesEngine : IAlertRulesEngine
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<AlertRulesEngine> _logger;

    public AlertRulesEngine(ApplicationDbContext context, ILogger<AlertRulesEngine> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<int> EvaluateAllAsync(CancellationToken cancellationToken = default)
    {
        var rules = await _context.AlertRules.Where(r => r.IsEnabled)
            .ToListAsync(cancellationToken);
        if (!rules.Any()) return 0;

        var now = DateTime.UtcNow;
        var allInstallations = await _context.Installations
            .Where(i => i.Status != InstallationStatus.Inactive)
            .ToListAsync(cancellationToken);

        // Pre-load recent alerts per (rule.Title, installationId) to check cooldowns
        // without N+1 queries. We bucket by rule title since that's what we store.
        var ruleTitles = rules.Select(r => r.Name).Distinct().ToList();
        var maxCooldown = rules.Max(r => r.CooldownMinutes);
        var cooldownStart = now.AddMinutes(-maxCooldown);
        var recentAlertsRaw = await _context.SystemAlerts
            .Where(a => ruleTitles.Contains(a.Title)
                        && a.ResolvedAt == null
                        && a.OccurredAt > cooldownStart)
            .Select(a => new { a.InstallationId, a.Title, a.OccurredAt })
            .ToListAsync(cancellationToken);
        // Key: (title + installationId) -> most recent OccurredAt
        var lastAlertTime = recentAlertsRaw
            .GroupBy(a => (a.Title, a.InstallationId))
            .ToDictionary(g => g.Key, g => g.Max(x => x.OccurredAt));

        int fired = 0;
        foreach (var rule in rules)
        {
            var targets = allInstallations.Where(i =>
                (rule.InstallationId == null || i.Id == rule.InstallationId)
                && (rule.BrandScope == null || i.InverterBrand == rule.BrandScope)
            ).ToList();

            foreach (var inst in targets)
            {
                try
                {
                    // Per-rule-per-installation cooldown (fixes original bug where
                    // rule.LastFiredAt was global across all installations)
                    if (lastAlertTime.TryGetValue((rule.Name, inst.Id), out var lastTime)
                        && now - lastTime < TimeSpan.FromMinutes(rule.CooldownMinutes))
                        continue;

                    var (shouldFire, observedValue, detail) = await EvaluateAsync(rule, inst, now, cancellationToken);
                    if (!shouldFire) continue;

                    _context.SystemAlerts.Add(new SystemAlert
                    {
                        InstallationId = inst.Id,
                        AlertType = rule.AlertType,
                        Severity = rule.Severity,
                        Title = rule.Name,
                        Message = $"Rule triggered: {detail}. Observed = {observedValue:0.##}, threshold = {rule.Threshold:0.##}",
                        OccurredAt = now,
                        IsNotified = false
                    });
                    lastAlertTime[(rule.Name, inst.Id)] = now;
                    rule.LastFiredAt = now; // still tracked for UI display
                    fired++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Rule evaluation failed for rule {RuleId} on install {InstId}", rule.Id, inst.Id);
                }
            }
        }

        if (fired > 0)
            await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Alert rules engine: evaluated {RuleCount} rule(s), fired {Fired} alert(s)", rules.Count, fired);
        return fired;
    }

    private async Task<(bool ShouldFire, decimal Observed, string Detail)> EvaluateAsync(
        AlertRule rule, Installation inst, DateTime now, CancellationToken ct)
    {
        switch (rule.Metric)
        {
            case AlertRuleMetric.PerformanceRatioDaily:
            {
                var yesterday = now.Date.AddDays(-1);
                var daily = await _context.EnergyData
                    .Where(e => e.InstallationId == inst.Id
                                && e.Granularity == DataGranularity.Daily
                                && e.Timestamp.Date == yesterday)
                    .Select(e => (decimal?)e.EnergyGenerationKwh)
                    .FirstOrDefaultAsync(ct);
                if (daily == null) return (false, 0, "no data");
                var expected = inst.SystemCapacityKw * 5.0m * 0.80m;
                var pr = expected > 0 ? daily.Value / expected : 0;
                return (Compare(pr, rule), pr, $"Daily PR for {yesterday:yyyy-MM-dd}");
            }
            case AlertRuleMetric.UptimePercent7d:
            {
                var from = now.Date.AddDays(-7);
                var to = now.Date.AddDays(-1);
                var producing = await _context.EnergyData
                    .Where(e => e.InstallationId == inst.Id
                                && e.Granularity == DataGranularity.Daily
                                && e.Timestamp >= from && e.Timestamp <= to
                                && e.EnergyGenerationKwh > 0)
                    .CountAsync(ct);
                var pct = (decimal)producing / 7m * 100;
                return (Compare(pct, rule), pct, "7-day uptime %");
            }
            case AlertRuleMetric.DailyEnergyKwh:
            {
                var yesterday = now.Date.AddDays(-1);
                var daily = await _context.EnergyData
                    .Where(e => e.InstallationId == inst.Id
                                && e.Granularity == DataGranularity.Daily
                                && e.Timestamp.Date == yesterday)
                    .Select(e => (decimal?)e.EnergyGenerationKwh)
                    .FirstOrDefaultAsync(ct);
                var v = daily ?? 0;
                return (Compare(v, rule), v, $"Energy for {yesterday:yyyy-MM-dd}");
            }
            case AlertRuleMetric.OfflineMinutes:
            {
                var lastSeen = inst.LastOnlineAt;
                if (lastSeen == null) return (Compare(decimal.MaxValue / 2, rule), 9999, "never online");
                var mins = (decimal)(now - lastSeen.Value).TotalMinutes;
                return (Compare(mins, rule), mins, "minutes since last contact");
            }
            case AlertRuleMetric.ApiFailureRate24h:
            {
                var since = now.AddHours(-24);
                var total = await _context.InverterApiLogs
                    .CountAsync(l => l.InstallationId == inst.Id && l.RequestTime >= since, ct);
                if (total == 0) return (false, 0, "no API calls");
                var failed = await _context.InverterApiLogs
                    .CountAsync(l => l.InstallationId == inst.Id && l.RequestTime >= since && !l.IsSuccess, ct);
                var rate = (decimal)failed / total * 100;
                return (Compare(rate, rule), rate, "24h API failure %");
            }
            case AlertRuleMetric.BatterySoc:
            {
                var latest = await _context.EnergyData
                    .Where(e => e.InstallationId == inst.Id
                                && e.Granularity == DataGranularity.Realtime
                                && e.BatterySocPercent != null)
                    .OrderByDescending(e => e.Timestamp)
                    .Select(e => e.BatterySocPercent)
                    .FirstOrDefaultAsync(ct);
                if (latest == null) return (false, 0, "no battery data");
                return (Compare(latest.Value, rule), latest.Value, "Battery SoC");
            }
            case AlertRuleMetric.ConsecutiveZeroEnergyDays:
            {
                var since = now.Date.AddDays(-14);
                var days = await _context.EnergyData
                    .Where(e => e.InstallationId == inst.Id
                                && e.Granularity == DataGranularity.Daily
                                && e.Timestamp >= since)
                    .OrderByDescending(e => e.Timestamp)
                    .Select(e => new { e.Timestamp, e.EnergyGenerationKwh })
                    .ToListAsync(ct);
                int consecutive = 0;
                foreach (var d in days)
                {
                    if (d.EnergyGenerationKwh <= 0) consecutive++;
                    else break;
                }
                return (Compare(consecutive, rule), consecutive, "consecutive zero-energy days");
            }
        }
        return (false, 0, "unknown metric");
    }

    private static bool Compare(decimal value, AlertRule rule) => rule.Operator switch
    {
        AlertRuleOperator.LessThan => value < rule.Threshold,
        AlertRuleOperator.LessThanOrEqual => value <= rule.Threshold,
        AlertRuleOperator.GreaterThan => value > rule.Threshold,
        AlertRuleOperator.GreaterThanOrEqual => value >= rule.Threshold,
        AlertRuleOperator.Equal => value == rule.Threshold,
        _ => false
    };
}
