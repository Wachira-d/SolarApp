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
        var now = DateTime.UtcNow;
        var rules = await _context.AlertRules.Where(r => r.IsEnabled)
            .ToListAsync(cancellationToken);
        var enabledIds = rules.Select(r => r.Id).ToHashSet();
        var enabledIdList = enabledIds.ToList();   // List for the SQL IN (...)

        // Adopt alerts raised before AlertRuleId existed, by the only link they
        // have — Title == rule name — so the lifecycle below covers them too.
        var allRules = await _context.AlertRules.AsNoTracking()
            .Select(r => new { r.Id, r.Name })
            .ToListAsync(cancellationToken);
        if (allRules.Count > 0)
        {
            var idByName = allRules
                .GroupBy(r => r.Name)
                .Where(g => g.Count() == 1)            // ambiguous names: leave alone
                .ToDictionary(g => g.Key, g => g.First().Id);
            var names = idByName.Keys.ToList();
            var legacy = await _context.SystemAlerts
                .Where(a => a.AlertRuleId == null && a.ResolvedAt == null && names.Contains(a.Title))
                .ToListAsync(cancellationToken);
            foreach (var a in legacy) a.AlertRuleId = idByName[a.Title];
            // The queries below run against the database, so the adoption has
            // to land first or this run would miss them and raise duplicates.
            if (legacy.Count > 0) await _context.SaveChangesAsync(cancellationToken);
        }

        // A disabled or deleted rule's alerts are closed. They used to stay
        // open for good, and High/Critical ones kept escalating to LINE.
        var orphaned = await _context.SystemAlerts
            .Where(a => a.AlertRuleId != null && a.ResolvedAt == null
                        && !enabledIdList.Contains(a.AlertRuleId.Value))
            .ToListAsync(cancellationToken);
        foreach (var a in orphaned)
        {
            a.ResolvedAt = now;
            if (!a.IsNotified) { a.IsNotified = true; a.NotificationSuppressed = true; }
        }

        if (rules.Count == 0)
        {
            if (orphaned.Count > 0) await _context.SaveChangesAsync(cancellationToken);
            return 0;
        }

        // Monitored sites only. Every metric here is read from synced data, so
        // on a site we don't poll they all read as zero / never online, and an
        // OfflineMinutes or DailyEnergyKwh rule fired on every such site forever.
        var allInstallations = await _context.Installations
            .Where(i => i.Status != InstallationStatus.Inactive && i.IncludesMonitoring)
            .ToListAsync(cancellationToken);

        var openAlerts = await _context.SystemAlerts
            .Where(a => a.AlertRuleId != null && a.ResolvedAt == null)
            .ToListAsync(cancellationToken);
        var openByKey = openAlerts
            .GroupBy(a => (a.AlertRuleId!.Value, a.InstallationId))
            .ToDictionary(g => g.Key, g => g.ToList());

        // Last raise per (rule, site), open or closed — the cooldown is a
        // flapping guard between a close and the next raise.
        var maxCooldown = rules.Max(r => r.CooldownMinutes);
        var cooldownStart = now.AddMinutes(-Math.Max(1, maxCooldown));
        var lastRaised = (await _context.SystemAlerts
                .Where(a => a.AlertRuleId != null && a.OccurredAt > cooldownStart)
                .Select(a => new { RuleId = a.AlertRuleId!.Value, a.InstallationId, a.OccurredAt })
                .ToListAsync(cancellationToken))
            .GroupBy(a => (a.RuleId, a.InstallationId))
            .ToDictionary(g => g.Key, g => g.Max(x => x.OccurredAt));

        int fired = 0, cleared = 0;
        var evaluated = new HashSet<(int, int)>();
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
                    var key = (rule.Id, inst.Id);
                    evaluated.Add(key);
                    openByKey.TryGetValue(key, out var open);

                    var (shouldFire, observedValue, detail) = await EvaluateAsync(rule, inst, now, cancellationToken);

                    if (!shouldFire)
                    {
                        // The condition has cleared: close what this rule raised
                        // here. Before, nothing did, and closing one by hand only
                        // made it fire again two minutes later.
                        if (open != null)
                            foreach (var a in open) { a.ResolvedAt = now; cleared++; }
                        continue;
                    }

                    // Still true and already raised — one open alert, not one per
                    // cooldown period.
                    if (open is { Count: > 0 }) continue;

                    if (lastRaised.TryGetValue(key, out var lastTime)
                        && now - lastTime < TimeSpan.FromMinutes(rule.CooldownMinutes))
                        continue;

                    _context.SystemAlerts.Add(new SystemAlert
                    {
                        InstallationId = inst.Id,
                        AlertRuleId = rule.Id,
                        AlertType = rule.AlertType,
                        Severity = rule.Severity,
                        Title = rule.Name,
                        Message = $"Rule triggered: {detail}. Observed = {observedValue:0.##}, threshold = {rule.Threshold:0.##}",
                        OccurredAt = now,
                        IsNotified = false
                    });
                    lastRaised[key] = now;
                    rule.LastFiredAt = now; // still tracked for UI display
                    fired++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Rule evaluation failed for rule {RuleId} on install {InstId}", rule.Id, inst.Id);
                }
            }
        }

        // Open alerts on a site the rule no longer covers — deactivated,
        // monitoring switched off, deleted, or the rule re-scoped to another
        // site or brand. Nothing above revisits those pairs, so without this
        // they would stay open (and escalate) forever.
        foreach (var (key, list) in openByKey)
        {
            if (evaluated.Contains(key) || !enabledIds.Contains(key.Item1)) continue;
            foreach (var a in list)
            {
                a.ResolvedAt = now;
                if (!a.IsNotified) { a.IsNotified = true; a.NotificationSuppressed = true; }
                cleared++;
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Alert rules engine: evaluated {RuleCount} rule(s), fired {Fired}, cleared {Cleared}, closed {Orphaned} from disabled/deleted rules",
            rules.Count, fired, cleared, orphaned.Count);
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
                // Short-circuit: sites flagged as no-battery can still
                // receive stray phantom SOC readings from hybrid-inverter
                // firmware (e.g. SOC = 0 reported every poll).  Evaluating
                // a "SOC < 20%" rule against those phantoms would fire
                // every cycle, so refuse to evaluate the rule at all.
                if (!inst.HasBattery)
                    return (false, 0, "site has no battery");
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
