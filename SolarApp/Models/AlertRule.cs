using System.ComponentModel.DataAnnotations;
using SolarApp.Models.Enums;

namespace SolarApp.Models;

/// <summary>
/// Configurable alert rule. The SystemHealthMonitorJob evaluates active rules
/// and creates SystemAlert records when conditions match.
/// </summary>
public class AlertRule
{
    public int Id { get; set; }

    [Required, StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [StringLength(300)]
    public string? Description { get; set; }

    public AlertRuleMetric Metric { get; set; }
    public AlertRuleOperator Operator { get; set; }
    public decimal Threshold { get; set; }

    /// <summary>Only fires after condition is true for this many consecutive checks (debouncing).</summary>
    public int ConsecutiveChecks { get; set; } = 1;

    public AlertSeverity Severity { get; set; } = AlertSeverity.Medium;
    public AlertType AlertType { get; set; } = AlertType.Warning;

    /// <summary>Scope: if null, applies to ALL installations. Otherwise, a specific one.</summary>
    public int? InstallationId { get; set; }
    public Installation? Installation { get; set; }

    /// <summary>Scope by brand (applies to all installations of this brand) when InstallationId is null.</summary>
    public InverterBrand? BrandScope { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>Don't re-fire more often than this window (minutes).</summary>
    public int CooldownMinutes { get; set; } = 60;

    public DateTime? LastFiredAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedByUserId { get; set; }
    public ApplicationUser? CreatedByUser { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum AlertRuleMetric
{
    PerformanceRatioDaily = 1,  // PR below X
    UptimePercent7d = 2,         // uptime % over last 7d
    DailyEnergyKwh = 3,          // yesterday's energy
    OfflineMinutes = 4,          // minutes since last contact
    ApiFailureRate24h = 5,       // % of failed API calls in last 24h
    BatterySoc = 6,              // Battery state of charge %
    ConsecutiveZeroEnergyDays = 7
}

public enum AlertRuleOperator
{
    LessThan = 1,
    LessThanOrEqual = 2,
    GreaterThan = 3,
    GreaterThanOrEqual = 4,
    Equal = 5
}
