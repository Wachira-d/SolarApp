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

    /// <summary>Monthly production summary for a customer — see MonthlyReportData.</summary>
    Task SendMonthlyReportAsync(string lineUserId, MonthlyReportData report);
}

/// <summary>
/// What a customer gets once a month. Deliberately none of the operator's
/// diagnostics: a customer who is told "PR 0.78, 3 API failures" learns
/// nothing and worries anyway. Production, money, and whether we kept the
/// system running — that's the service they're paying for.
/// </summary>
public class MonthlyReportData
{
    public string CustomerName { get; set; } = string.Empty;
    /// <summary>Month being reported, e.g. "สิงหาคม 2568".</summary>
    public string PeriodLabel { get; set; } = string.Empty;
    public int SiteCount { get; set; }
    public decimal TotalKwh { get; set; }
    public decimal SavingsThb { get; set; }
    public decimal Co2SavedKg { get; set; }
    public int TreesEquivalent { get; set; }
    /// <summary>Percentage change vs the previous month, or null on the first report.</summary>
    public decimal? ChangeVsPreviousPercent { get; set; }
    public decimal BestDayKwh { get; set; }
    public string? BestDayDate { get; set; }
    /// <summary>Days in the month with no offline alert against any of their sites.</summary>
    public int HealthyDays { get; set; }
    public int DaysInMonth { get; set; }
    public int CleaningsDone { get; set; }
    public int WorkOrdersCompleted { get; set; }
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
