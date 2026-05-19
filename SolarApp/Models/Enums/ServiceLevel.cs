namespace SolarApp.Models.Enums;

/// <summary>
/// Scope of service the operator provides for an installation.
///
/// The enum picks a preset; the actual gating is done by the boolean flags
/// on Installation (IncludesMonitoring / IncludesCleaning / ...).  Picking
/// a preset auto-checks/unchecks those flags via the Edit/Create UI so
/// 95 % of sites can be set up with one click, while "Custom" lets the
/// operator pick any combination for unusual contracts.
///
/// Why both an enum AND flags instead of just flags:
///   * Reports / dashboards need a stable single-value column for grouping
///     ("how many sites are FullOM vs CleaningOnly").
///   * Saves the operator from ticking five boxes for the common case.
///   * The flags remain the source of truth for runtime behaviour, so
///     legacy rows (no preset selected) still work.
/// </summary>
public enum ServiceLevel
{
    /// <summary>
    /// Full O&amp;M: we monitor the cloud, clean the panels on schedule,
    /// run preventive + corrective maintenance, and handle warranty claims.
    /// Default for newly-created installations.
    /// </summary>
    FullOM = 0,

    /// <summary>
    /// Monitoring only — we have the cloud account and watch performance,
    /// but the customer/partner handles cleaning and on-site work.
    /// </summary>
    MonitoringOnly = 1,

    /// <summary>
    /// Cleaning only — scheduled panel-washing visits, no online
    /// monitoring (the partner monitors the system themselves and we
    /// don't have a cloud account for this site).
    /// </summary>
    CleaningOnly = 2,

    /// <summary>
    /// Preventive-maintenance rounds: scheduled site visits for cleaning
    /// + visual inspection + checks, but no online monitoring and no
    /// corrective work.  Common for partner-monitored sites where we're
    /// contracted for PM rounds only.
    /// </summary>
    PreventiveOnly = 3,

    /// <summary>
    /// Custom mix — the operator ticks the individual service flags
    /// directly.  Used when none of the presets match (e.g. monitoring
    /// + warranty but no cleaning).
    /// </summary>
    Custom = 99,
}
