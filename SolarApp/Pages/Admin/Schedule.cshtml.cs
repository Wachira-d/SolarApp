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

        await UpsertSetting("Schedule.DataSync", DataSyncCron,
            "Cron for inverter data sync (fetches power/energy from cloud)");
        await UpsertSetting("Schedule.HealthMonitor", HealthMonitorCron,
            "Cron for system health monitor (checks offline, warranty, cleaning)");

        // Apply immediately via Hangfire
        RecurringJob.AddOrUpdate<SolarDataSyncJob>(
            "sync-production-data", j => j.ExecuteAsync(), DataSyncCron);
        RecurringJob.AddOrUpdate<SystemHealthMonitorJob>(
            "check-system-health", j => j.ExecuteAsync(), HealthMonitorCron);

        StatusMessage = "Schedule updated successfully!";
        return RedirectToPage();
    }

    public IActionResult OnPostApplyRecommended()
    {
        DataSyncCron = "*/15 * * * *";
        HealthMonitorCron = "*/5 * * * *";
        return OnPostSaveAsync().Result;
    }

    public IActionResult OnPostRunNow(string jobId)
    {
        try
        {
            if (jobId == "sync-production-data")
                BackgroundJob.Enqueue<SolarDataSyncJob>(j => j.ExecuteAsync());
            else if (jobId == "check-system-health")
                BackgroundJob.Enqueue<SystemHealthMonitorJob>(j => j.ExecuteAsync());

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
        try
        {
            // Cronos is bundled with Hangfire
            Cronos.CronExpression.Parse(cron);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
