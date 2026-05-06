using System.ComponentModel.DataAnnotations;

namespace SolarApp.Models.Enums;

public enum WorkOrderType
{
    PanelCleaning = 1,
    PreventiveMaintenance = 2,
    CorrectiveRepair = 3,
    Inspection = 4,
    NewInstallation = 5,
    Commissioning = 6,
    Decommission = 7,
    Other = 99
}

public enum WorkOrderStatus
{
    Draft = 0,
    Scheduled = 1,
    Assigned = 2,
    Accepted = 3,
    InProgress = 4,
    Completed = 5,
    Verified = 6,
    Invoiced = 7,
    Closed = 8,
    Cancelled = 9
}
