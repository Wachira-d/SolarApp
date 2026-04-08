namespace SolarApp.Services.LineApi;

public interface ILineMessagingService
{
    Task SendMessageAsync(string lineUserId, string message);
    Task SendAlertAsync(string lineUserId, string siteName, string alertTitle, string alertMessage, string? rootCause);
    Task SendMaintenanceReminderAsync(string lineUserId, string siteName, string maintenanceType, DateTime dueDate);
}
