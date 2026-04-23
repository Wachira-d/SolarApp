namespace SolarApp.Services.LineApi;

public interface ILineMessagingService
{
    Task<(bool Success, string Message)> SendTestMessageAsync(string lineUserId, string? overrideToken = null);
    Task SendMessageAsync(string lineUserId, string message);
    Task SendFlexAlertAsync(string lineUserId, string siteName, string alertTitle,
        string alertMessage, string severity, string? rootCause);
    Task SendAlertAsync(string lineUserId, string siteName, string alertTitle,
        string alertMessage, string? rootCause);
    Task SendMaintenanceReminderAsync(string lineUserId, string siteName,
        string maintenanceType, DateTime dueDate);
    Task SendWarrantyExpiryAsync(string lineUserId, string siteName, int daysRemaining);
}
