namespace SolarApp.Models.Enums;

public enum WarrantyType
{
    Panel = 1,
    Inverter = 2,
    Battery = 3,
    Workmanship = 4,
    Performance = 5,
    Other = 99
}

public enum ClaimStatus
{
    Filed = 1,
    UnderReview = 2,
    Approved = 3,
    Rejected = 4,
    Completed = 5
}
