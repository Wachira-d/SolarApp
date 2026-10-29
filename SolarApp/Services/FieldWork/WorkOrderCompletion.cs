using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Services.FieldWork;

/// <summary>
/// What finishing a work order means for the rest of the system. Two pages
/// finish work orders — Detail (the technician's "เสร็จสิ้นงาน") and Create
/// (recording a visit after the fact) — and before this neither told the
/// site about it: a completed panel-cleaning order left the site's cleaning
/// count, next-due date, cleaning sheet and monthly report untouched, so the
/// reminder kept firing for a roof that had just been washed.
/// </summary>
public static class WorkOrderCompletion
{
    /// <summary>
    /// Marks <paramref name="wo"/> completed and propagates it. Does not save;
    /// the caller owns the transaction.
    /// </summary>
    public static async Task ApplyAsync(
        ApplicationDbContext context, WorkOrder wo, DateTime completedAtUtc,
        string? userId, string? completionNotes)
    {
        wo.Status = WorkOrderStatus.Completed;
        wo.ActualStartTime ??= completedAtUtc;
        wo.ActualEndTime = completedAtUtc;
        if (!string.IsNullOrWhiteSpace(completionNotes))
            wo.CompletionNotes = completionNotes.Trim();
        wo.UpdatedAt = completedAtUtc;

        if (wo.CountsAsCleaning && wo.InstallationId.HasValue)
        {
            var inst = wo.Installation
                       ?? await context.Installations.FirstOrDefaultAsync(i => i.Id == wo.InstallationId.Value);
            // An admin can reopen a completed order and close it again; the
            // visit happened once, so it is counted once.
            var tag = $"จากใบสั่งงาน {wo.WorkOrderNumber}";
            var alreadyCounted = inst != null && await context.CleaningRecords
                .AnyAsync(c => c.InstallationId == inst.Id && c.Notes != null && c.Notes.StartsWith(tag));
            if (inst != null && !alreadyCounted)
            {
                // The cleaning sheet is what the customer portal, the monthly
                // report and the contract count read, so the order's visit
                // becomes a row there — photos included — rather than a
                // counter bump nobody can see.
                var cleaned = SiteTime.ToSiteTime(completedAtUtc).Date;
                inst.RecordCompletedCleaning(cleaned);
                context.CleaningRecords.Add(new CleaningRecord
                {
                    InstallationId = inst.Id,
                    ScheduledDate = wo.ScheduledDate.Date,
                    ActualDate = cleaned,
                    Status = CleaningStatus.Completed,
                    PerformedByUserId = wo.AssignedToUserId ?? userId,
                    PerformedByExternal = string.IsNullOrWhiteSpace(wo.AssignedTeam) ? null : wo.AssignedTeam,
                    BeforePhotos = wo.BeforePhotos,
                    AfterPhotos = wo.AfterPhotos,
                    Notes = string.IsNullOrWhiteSpace(wo.CompletionNotes)
                        ? $"จากใบสั่งงาน {wo.WorkOrderNumber}"
                        : $"จากใบสั่งงาน {wo.WorkOrderNumber}: {wo.CompletionNotes}",
                    CreatedAt = completedAtUtc,
                    UpdatedAt = completedAtUtc
                });
            }
        }

        // A ticket that spawned this order is answered by it.
        if (wo.ServiceTicketId.HasValue)
        {
            var ticket = wo.ServiceTicket
                         ?? await context.ServiceTickets.FirstOrDefaultAsync(t => t.Id == wo.ServiceTicketId.Value);
            if (ticket != null && ticket.Status != TicketStatus.Resolved && ticket.Status != TicketStatus.Closed)
            {
                ticket.Status = TicketStatus.Resolved;
                ticket.ResolvedAt = completedAtUtc;
                ticket.UpdatedAt = completedAtUtc;
                if (string.IsNullOrWhiteSpace(ticket.Resolution))
                    ticket.Resolution = string.IsNullOrWhiteSpace(wo.CompletionNotes)
                        ? $"ดำเนินการแล้วตามใบสั่งงาน {wo.WorkOrderNumber}"
                        : $"ใบสั่งงาน {wo.WorkOrderNumber}: {wo.CompletionNotes}";
            }
        }
    }

    /// <summary>
    /// Allowed status moves. The server used to accept any target status, so
    /// a cancelled order could be "started" and a Partner could post Verified
    /// through the URL that the page only hid.
    /// </summary>
    public static bool CanTransition(WorkOrderStatus from, WorkOrderStatus to, bool isAdmin) => (from, to) switch
    {
        (WorkOrderStatus.Draft or WorkOrderStatus.Scheduled or WorkOrderStatus.Assigned or WorkOrderStatus.Accepted,
            WorkOrderStatus.InProgress) => true,
        (WorkOrderStatus.Draft or WorkOrderStatus.Scheduled or WorkOrderStatus.Assigned or WorkOrderStatus.Accepted or WorkOrderStatus.InProgress,
            WorkOrderStatus.Completed) => true,
        (WorkOrderStatus.Draft or WorkOrderStatus.Scheduled or WorkOrderStatus.Assigned or WorkOrderStatus.Accepted or WorkOrderStatus.InProgress,
            WorkOrderStatus.Cancelled) => true,
        (WorkOrderStatus.Completed, WorkOrderStatus.Verified) => isAdmin,
        (WorkOrderStatus.Verified, WorkOrderStatus.Closed) => isAdmin,
        (WorkOrderStatus.Completed or WorkOrderStatus.Verified, WorkOrderStatus.InProgress) => isAdmin, // reopen
        _ => false
    };

    /// <summary>Next WO-YYYYMM-NNNN number for the given month.</summary>
    public static async Task<string> NextNumberAsync(ApplicationDbContext context, DateTime forDate)
    {
        var prefix = $"WO-{forDate:yyyyMM}";
        var lastNum = await context.WorkOrders
            .Where(w => w.WorkOrderNumber.StartsWith(prefix))
            .OrderByDescending(w => w.WorkOrderNumber)
            .Select(w => w.WorkOrderNumber)
            .FirstOrDefaultAsync();
        var seq = 1;
        if (lastNum != null && lastNum.Length > prefix.Length + 1
            && int.TryParse(lastNum[(prefix.Length + 1)..], out var parsed))
            seq = parsed + 1;
        return $"{prefix}-{seq:D4}";
    }
}
