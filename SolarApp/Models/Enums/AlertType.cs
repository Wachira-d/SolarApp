namespace SolarApp.Models.Enums;

public enum AlertType
{
    Offline = 1,
    Error = 2,
    Warning = 3,
    MaintenanceDue = 4,
    WarrantyExpiring = 5,
    LowPerformance = 6,
    PredictiveDiagnostic = 7,
}

/// <summary>
/// Splits alert types into two operator-facing buckets so reports and
/// dashboards can separate "the plant has a fault" from "a scheduled
/// visit is due".  Mixing the two made a routine cleaning reminder read
/// as a system outage in the daily summary.
/// </summary>
public static class AlertCategory
{
    /// <summary>
    /// Scheduled / calendar-driven reminders — cleaning due, warranty
    /// expiring.  These are planning items, not faults.
    /// </summary>
    public static bool IsScheduledReminder(this AlertType t) =>
        t is AlertType.MaintenanceDue or AlertType.WarrantyExpiring;

    /// <summary>
    /// Real system problems — offline, error, low performance, predictive,
    /// generic warning.  Everything that isn't a scheduled reminder.
    /// </summary>
    public static bool IsSystemProblem(this AlertType t) => !t.IsScheduledReminder();
}

public enum AlertSeverity
{
    Critical = 1,
    High = 2,
    Medium = 3,
    Low = 4
}
