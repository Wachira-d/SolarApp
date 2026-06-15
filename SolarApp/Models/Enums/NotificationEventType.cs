namespace SolarApp.Models.Enums;

/// <summary>
/// The event categories an operator can route to extra LINE recipients
/// via <see cref="SolarApp.Models.NotificationRoute"/>.
///
/// These are intentionally a SUPERSET of AlertType so the routing UI can
/// also target non-alert events (the daily summary, recovery "all clear"
/// messages).  <see cref="All"/> is a wildcard that matches every event —
/// handy for "send EVERYTHING to the ops group" without listing each type.
///
/// Mapping from AlertType is done in
/// NotificationRouteService.FromAlertType so the two enums can evolve
/// independently.
/// </summary>
public enum NotificationEventType
{
    /// <summary>Grid loss / inverter offline / cloud unreachable down-alerts.</summary>
    GridLossOffline = 1,

    /// <summary>"✅ ระบบกลับมาแล้ว" recovery / all-clear messages.</summary>
    Recovery = 2,

    /// <summary>Production well below the expected curve for the day.</summary>
    LowProduction = 3,

    /// <summary>Cloud API stopped responding (system-internal).</summary>
    ApiFailure = 4,

    /// <summary>Scheduled panel cleaning is due.</summary>
    CleaningDue = 5,

    /// <summary>Warranty expiry approaching.</summary>
    WarrantyExpiry = 6,

    /// <summary>The once-a-day operator summary report.</summary>
    DailySummary = 7,

    /// <summary>Station reported faulty (Huawei real_health_state=1 etc.).</summary>
    StationFaulty = 8,

    /// <summary>Predictive diagnostic / early-warning alerts.</summary>
    PredictiveDiagnostic = 9,

    /// <summary>Wildcard — a route with this type receives EVERY event.</summary>
    All = 99,
}
