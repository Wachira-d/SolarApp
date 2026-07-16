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
    Task SendRecoveryNotificationAsync(string lineUserId, string siteName,
        string alertTitle, string recoveryMessage, TimeSpan downtime);
    Task SendDailySummaryAsync(string lineUserId, DailySummaryData summary);
    Task SendEscalationAsync(string lineUserId, string siteName, string alertTitle,
        string alertMessage, string severity, TimeSpan openDuration, int escalationLevel);
    Task SendPredictiveAlertAsync(string lineUserId, string siteName,
        string prediction, string evidence, string[] checkSteps, string[] fixSteps,
        string riskLevel, string? knownCaseRef);
    Task SendDiagnosticAlertAsync(string lineUserId, string siteName,
        string alertTitle, string alertMessage, string severity,
        string? rootCause, string? predictedCause,
        string[] checkSteps, string[] fixSteps, string? knownCaseRef,
        string? safetyWarning, string[]? contextNotes);
}

public class DailySummaryData
{
    public int TotalSites { get; set; }
    public int OnlineSites { get; set; }
    public int OfflineSites { get; set; }
    public decimal TotalEnergyTodayKwh { get; set; }
    /// <summary>
    /// Unresolved SYSTEM-PROBLEM alerts only (offline / error / low perf /
    /// predictive / warning).  Scheduled reminders are reported separately
    /// so a cleaning-due day no longer reads as "system has alerts".
    /// </summary>
    public int ActiveAlerts { get; set; }
    public int ResolvedTodayAlerts { get; set; }
    public int NewAlertsToday { get; set; }
    /// <summary>Unresolved "cleaning due / overdue" reminders (AlertType.MaintenanceDue).</summary>
    public int CleaningDueCount { get; set; }
    /// <summary>Unresolved "warranty expiring" reminders (AlertType.WarrantyExpiring).</summary>
    public int WarrantyExpiringCount { get; set; }
    public int PendingWorkOrders { get; set; }
    public int CompletedWorkOrdersToday { get; set; }
    public List<string> CriticalSites { get; set; } = new();
    public List<string> RecoveredSites { get; set; } = new();
    /// <summary>Site names with an outstanding cleaning-due reminder (up to a few, for the report body).</summary>
    public List<string> CleaningDueSites { get; set; } = new();
}
