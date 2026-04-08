namespace SolarApp.Models.Enums;

public enum AlertType
{
    Offline = 1,
    Error = 2,
    Warning = 3,
    MaintenanceDue = 4,
    WarrantyExpiring = 5,
    LowPerformance = 6
}

public enum AlertSeverity
{
    Critical = 1,
    High = 2,
    Medium = 3,
    Low = 4
}
