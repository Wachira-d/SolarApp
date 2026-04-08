namespace SolarApp.Models.Enums;

public enum MaintenanceType
{
    PanelCleaning = 1,
    Inspection = 2,
    Repair = 3,
    Replacement = 4,
    GeneralMaintenance = 5
}

public enum MaintenanceStatus
{
    Scheduled = 1,
    InProgress = 2,
    Completed = 3,
    Cancelled = 4
}
