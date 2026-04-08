namespace SolarApp.Models.Enums;

public enum NotificationChannel
{
    Line = 1,
    Email = 2,
    InApp = 3,
    SMS = 4
}

public enum NotificationStatus
{
    Pending = 1,
    Sent = 2,
    Delivered = 3,
    Failed = 4
}
