using Hangfire;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.LineApi;

namespace SolarApp.BackgroundJobs;

/// <summary>
/// Sends LINE reminders for upcoming and overdue work orders so technicians
/// and customers don't miss scheduled cleanings/inspections — even when no
/// one is logged into the website.
///
/// Recipients per WO:
///   1. Assignee (AssignedToUser.LineUserId) — primary
///   2. Customer (Installation.Customer.LineUserId or WO.Customer.LineUserId) — courtesy ping
///   3. Alert group (Line.AlertGroupId in SystemSettings) — visibility for ops
///
/// Trigger windows:
///   - Upcoming: WO.ScheduledDate within next 36 hours, status &lt; Completed,
///     ReminderSentAt is null → send + mark
///   - Overdue: WO.ScheduledDate &lt; today, status &lt; Completed,
///     ReminderSentAt is null OR &gt; 23h ago → send + bump
/// </summary>
public class WorkOrderReminderJob
{
    private readonly ApplicationDbContext _context;
    private readonly ILineMessagingService _lineService;
    private readonly ILogger<WorkOrderReminderJob> _logger;

    public WorkOrderReminderJob(
        ApplicationDbContext context,
        ILineMessagingService lineService,
        ILogger<WorkOrderReminderJob> logger)
    {
        _context = context;
        _lineService = lineService;
        _logger = logger;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public async Task ExecuteAsync()
    {
        // Bail safely if WorkOrders table doesn't exist yet (first deploy)
        try
        {
            #pragma warning disable EF1002
            await _context.Database.ExecuteSqlRawAsync("SELECT TOP 0 * FROM [WorkOrders]");
            #pragma warning restore EF1002
        }
        catch
        {
            _logger.LogInformation("WorkOrderReminderJob: WorkOrders table not present yet — skipping");
            return;
        }

        var now = DateTime.UtcNow;
        // ScheduledDate is a site-local date, so the windows are site-local
        // too. With UTC, a job dated today counted as overdue until 07:00 and
        // got an "เลยกำหนด" nudge on the morning it was due.
        var upcomingWindowEnd = SolarApp.Services.SiteTime.Now.AddHours(36);
        var todayStart = SolarApp.Services.SiteTime.Today;

        // Statuses that still need attention
        var openStatuses = new[]
        {
            WorkOrderStatus.Scheduled,
            WorkOrderStatus.Assigned,
            WorkOrderStatus.Accepted,
            WorkOrderStatus.InProgress,
        };

        // Load alert group ID once
        var alertGroupId = await _context.SystemSettings
            .Where(s => s.Key == "Line.AlertGroupId")
            .Select(s => s.Value)
            .FirstOrDefaultAsync();

        // ── 1. Upcoming WOs (next 36 h) needing first reminder ──
        var upcoming = await _context.WorkOrders
            .Include(w => w.Installation).ThenInclude(i => i!.Customer)
            .Include(w => w.Customer)
            .Include(w => w.AssignedToUser)
            .Where(w => openStatuses.Contains(w.Status)
                && w.ScheduledDate >= todayStart
                && w.ScheduledDate <= upcomingWindowEnd
                && w.ReminderSentAt == null)
            .ToListAsync();

        // ── 2. Overdue WOs needing a daily nudge ──
        var overdueCutoff = now.AddHours(-23);
        var overdue = await _context.WorkOrders
            .Include(w => w.Installation).ThenInclude(i => i!.Customer)
            .Include(w => w.Customer)
            .Include(w => w.AssignedToUser)
            .Where(w => openStatuses.Contains(w.Status)
                && w.ScheduledDate < todayStart
                && (w.ReminderSentAt == null || w.ReminderSentAt < overdueCutoff))
            .ToListAsync();

        var sentUpcoming = 0;
        foreach (var wo in upcoming)
        {
            await SendReminderAsync(wo, alertGroupId, isOverdue: false);
            wo.ReminderSentAt = now;
            sentUpcoming++;
        }

        var sentOverdue = 0;
        foreach (var wo in overdue)
        {
            await SendReminderAsync(wo, alertGroupId, isOverdue: true);
            wo.ReminderSentAt = now;
            sentOverdue++;
        }

        if (sentUpcoming + sentOverdue > 0)
            await _context.SaveChangesAsync();

        _logger.LogInformation(
            "WorkOrderReminderJob: sent {Upcoming} upcoming reminder(s), {Overdue} overdue nudge(s)",
            sentUpcoming, sentOverdue);
    }

    private async Task SendReminderAsync(WorkOrder wo, string? alertGroupId, bool isOverdue)
    {
        var siteName = wo.SiteName;
        var typeLabel = wo.Type.ToString();
        // ScheduledDate is the date picked in the form — already site-local.
        // ToLocalTime() treated it as UTC and could shift it a day.
        var dateStr = wo.ScheduledDate.ToString("dd/MM/yyyy");
        var timeStr = wo.ScheduledTime.HasValue
            ? wo.ScheduledTime.Value.ToString(@"hh\:mm")
            : "—";

        string title, body;
        if (isOverdue)
        {
            var daysLate = Math.Max(1, (int)Math.Round((SolarApp.Services.SiteTime.Today - wo.ScheduledDate.Date).TotalDays));
            title = $"⚠ Overdue WO: {wo.WorkOrderNumber}";
            body = $"งาน{typeLabel} ที่ '{siteName}' เลยกำหนด {daysLate} วัน " +
                   $"(เดิม {dateStr}). โปรดอัพเดตสถานะหรือเลื่อนนัดหมายในระบบ.";
        }
        else
        {
            var hoursAway = Math.Max(0, (int)Math.Round((wo.ScheduledDate - DateTime.UtcNow).TotalHours));
            title = $"📅 Upcoming WO: {wo.WorkOrderNumber}";
            body = $"งาน{typeLabel} ที่ '{siteName}' " +
                   $"กำหนด {dateStr} {timeStr} (อีก ~{hoursAway} ชม.). " +
                   $"กรุณาเตรียมอุปกรณ์ + ตรวจ checklist ในแอปก่อนออกหน้างาน.";
        }

        var sent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Assignee (the person who has to actually do the work)
        var assigneeLineId = wo.AssignedToUser?.LineUserId;
        if (!string.IsNullOrWhiteSpace(assigneeLineId) && sent.Add(assigneeLineId))
        {
            await SafeSendAsync(assigneeLineId, siteName, title, body);
        }

        // 2. Customer — courtesy ping
        var customerLineId = wo.Customer?.LineUserId
            ?? wo.Installation?.Customer?.LineUserId;
        if (!string.IsNullOrWhiteSpace(customerLineId) && sent.Add(customerLineId))
        {
            await SafeSendAsync(customerLineId, siteName, title, body);
        }

        // 3. Alert group — ops visibility
        if (!string.IsNullOrWhiteSpace(alertGroupId) && sent.Add(alertGroupId))
        {
            await SafeSendAsync(alertGroupId, siteName, title, body);
        }

        if (sent.Count == 0)
        {
            _logger.LogInformation(
                "WO {Num}: no LINE recipient available (no assignee LineUserId, no customer LineUserId, no alert group)",
                wo.WorkOrderNumber);
        }
    }

    private async Task SafeSendAsync(string lineId, string siteName, string title, string body)
    {
        try
        {
            await _lineService.SendFlexAlertAsync(
                lineId, siteName, title, body,
                severity: "Medium", rootCause: null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WorkOrder reminder LINE send failed to {LineId}", lineId);
        }
    }
}
