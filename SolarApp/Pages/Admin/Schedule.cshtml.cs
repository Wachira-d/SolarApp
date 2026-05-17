using Hangfire;
using Hangfire.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.BackgroundJobs;
using SolarApp.Data;
using SolarApp.Models;

namespace SolarApp.Pages.Admin;

[Authorize(Roles = "Admin")]
public class ScheduleModel : PageModel
{
    private readonly ApplicationDbContext _context;

    public ScheduleModel(ApplicationDbContext context) => _context = context;

    [BindProperty] public string DataSyncCron { get; set; } = "*/15 * * * *";
    [BindProperty] public string HealthMonitorCron { get; set; } = "*/5 * * * *";
    [BindProperty] public string WorkOrderRemindersCron { get; set; } = "*/30 * * * *";

    public List<JobStatus> Jobs { get; set; } = new();
    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public class JobStatus
    {
        public string Id { get; set; } = "";
        public string Cron { get; set; } = "";
        public DateTime? LastExecution { get; set; }
        public DateTime? NextExecution { get; set; }
        public string? LastJobState { get; set; }
    }

    public async Task OnGetAsync()
    {
        await LoadSettingsAsync();
        LoadJobStatus();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        // Validate cron expressions
        if (!IsValidCron(DataSyncCron))
        {
            ErrorMessage = $"Invalid Data Sync cron expression: '{DataSyncCron}'";
            return RedirectToPage();
        }
        if (!IsValidCron(HealthMonitorCron))
        {
            ErrorMessage = $"Invalid Health Monitor cron expression: '{HealthMonitorCron}'";
            return RedirectToPage();
        }
        if (!IsValidCron(WorkOrderRemindersCron))
        {
            ErrorMessage = $"Invalid Work Order Reminders cron expression: '{WorkOrderRemindersCron}'";
            return RedirectToPage();
        }

        await UpsertSetting("Schedule.DataSync", DataSyncCron,
            "Cron for inverter data sync (fetches power/energy from cloud)");
        await UpsertSetting("Schedule.HealthMonitor", HealthMonitorCron,
            "Cron for system health monitor (checks offline, warranty, cleaning)");
        await UpsertSetting("Schedule.WorkOrderReminders", WorkOrderRemindersCron,
            "Cron for LINE reminders for upcoming/overdue work orders");

        // Apply immediately via Hangfire
        RecurringJob.AddOrUpdate<SolarDataSyncJob>(
            "sync-production-data", j => j.ExecuteAsync(), DataSyncCron);
        RecurringJob.AddOrUpdate<SystemHealthMonitorJob>(
            "check-system-health", j => j.ExecuteAsync(), HealthMonitorCron);
        RecurringJob.AddOrUpdate<WorkOrderReminderJob>(
            "workorder-reminders", j => j.ExecuteAsync(), WorkOrderRemindersCron);

        StatusMessage = "Schedule updated successfully!";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostApplyRecommendedAsync()
    {
        DataSyncCron = "*/15 * * * *";
        HealthMonitorCron = "*/5 * * * *";
        WorkOrderRemindersCron = "*/30 * * * *";
        return await OnPostSaveAsync();
    }

    public IActionResult OnPostRunNow(string jobId)
    {
        try
        {
            // Pre-check: if this recurring job is already running, [DisableConcurrentExecution]
            // means a manual enqueue will sit in the queue waiting up to 30 minutes for the
            // distributed lock — admin gets no feedback. Detect and warn instead.
            try
            {
                using var conn = JobStorage.Current.GetConnection();
                var monitoringApi = JobStorage.Current.GetMonitoringApi();
                var processing = monitoringApi.ProcessingJobs(0, 50);
                var alreadyRunning = processing.Any(p =>
                    p.Value.Job?.Type?.Name == jobId switch
                    {
                        "sync-production-data" => nameof(SolarDataSyncJob),
                        "check-system-health" => nameof(SystemHealthMonitorJob),
                        "workorder-reminders" => nameof(WorkOrderReminderJob),
                        _ => null
                    });

                if (alreadyRunning)
                {
                    ErrorMessage = $"Job '{jobId}' is already running. Wait for it to finish " +
                        "(check Hangfire Dashboard → Processing). Manual enqueue while running " +
                        "would just sit in queue until the current cycle completes.";
                    return RedirectToPage();
                }
            }
            catch
            {
                // If monitoring API isn't available (e.g. Hangfire not yet started),
                // fall through to the enqueue — best effort.
            }

            if (jobId == "sync-production-data")
                BackgroundJob.Enqueue<SolarDataSyncJob>(j => j.ExecuteAsync());
            else if (jobId == "check-system-health")
                BackgroundJob.Enqueue<SystemHealthMonitorJob>(j => j.ExecuteAsync());
            else if (jobId == "workorder-reminders")
                BackgroundJob.Enqueue<WorkOrderReminderJob>(j => j.ExecuteAsync());

            StatusMessage = $"Job '{jobId}' queued for immediate execution";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to queue job: {ex.Message}";
        }
        return RedirectToPage();
    }

    private async Task LoadSettingsAsync()
    {
        DataSyncCron = (await _context.SystemSettings
            .FirstOrDefaultAsync(s => s.Key == "Schedule.DataSync"))?.Value ?? "*/15 * * * *";
        HealthMonitorCron = (await _context.SystemSettings
            .FirstOrDefaultAsync(s => s.Key == "Schedule.HealthMonitor"))?.Value ?? "*/5 * * * *";
        WorkOrderRemindersCron = (await _context.SystemSettings
            .FirstOrDefaultAsync(s => s.Key == "Schedule.WorkOrderReminders"))?.Value ?? "*/30 * * * *";
    }

    private async Task UpsertSetting(string key, string value, string description)
    {
        var s = await _context.SystemSettings.FirstOrDefaultAsync(x => x.Key == key);
        if (s == null)
        {
            _context.SystemSettings.Add(new SystemSetting { Key = key, Value = value, Description = description });
        }
        else
        {
            s.Value = value;
            s.Description = description;
            s.UpdatedAt = DateTime.UtcNow;
        }
        await _context.SaveChangesAsync();
    }

    private void LoadJobStatus()
    {
        try
        {
            using var conn = JobStorage.Current.GetConnection();
            var recurringJobs = conn.GetRecurringJobs();

            foreach (var j in recurringJobs)
            {
                Jobs.Add(new JobStatus
                {
                    Id = j.Id,
                    Cron = j.Cron,
                    LastExecution = j.LastExecution?.ToLocalTime(),
                    NextExecution = j.NextExecution?.ToLocalTime(),
                    LastJobState = j.LastJobState
                });
            }
        }
        catch
        {
            // Hangfire might not be ready yet
        }
    }

    private static bool IsValidCron(string cron)
    {
        if (string.IsNullOrWhiteSpace(cron)) return false;

        // Basic validation: 5 or 6 space-separated fields.
        // Hangfire will throw at AddOrUpdate if the expression itself is invalid.
        var parts = cron.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5 || parts.Length > 6) return false;

        try
        {
            // Let Hangfire try to parse via its own internal validator.
            // If invalid, AddOrUpdate will throw - we catch that.
            var testJobId = $"_cron-validation-{Guid.NewGuid():N}";
            Hangfire.RecurringJob.AddOrUpdate(testJobId, () => Console.WriteLine("noop"), cron);
            Hangfire.RecurringJob.RemoveIfExists(testJobId);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
