using System.ComponentModel.DataAnnotations;
using SolarApp.Models.Enums;

namespace SolarApp.Models;

/// <summary>
/// Unified work order for panel cleaning, PM, repair, inspection, or any
/// job — whether for a monitored installation or a standalone external site.
///
/// Key design:
///   InstallationId nullable → standalone jobs for unmonitored sites
///   ExternalSiteName/Address → ad-hoc site info when no Installation
///   RecurrenceMonths → auto-create next WO on completion (PM schedules)
///   ChecklistItems → structured task list per job type
/// </summary>
public class WorkOrder
{
    public int Id { get; set; }

    [Required, StringLength(20)]
    public string WorkOrderNumber { get; set; } = string.Empty;

    [Required]
    public WorkOrderType Type { get; set; } = WorkOrderType.PanelCleaning;

    public WorkOrderStatus Status { get; set; } = WorkOrderStatus.Draft;

    // ─── Link to Installation (optional — null = standalone job) ───
    public int? InstallationId { get; set; }
    public Installation? Installation { get; set; }

    // ─── For standalone jobs (when InstallationId is null) ───
    [StringLength(200)]
    public string? ExternalSiteName { get; set; }
    [StringLength(500)]
    public string? ExternalSiteAddress { get; set; }
    [StringLength(100)]
    public string? ExternalCustomerName { get; set; }
    [StringLength(50)]
    public string? ExternalCustomerPhone { get; set; }
    public double? ExternalLatitude { get; set; }
    public double? ExternalLongitude { get; set; }

    // ─── Link to customer (optional, for billing) ───
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    // ─── Schedule ───
    [Required]
    public DateTime ScheduledDate { get; set; }
    public TimeSpan? ScheduledTime { get; set; }
    public int EstimatedDurationMinutes { get; set; } = 120;
    public DateTime? ActualStartTime { get; set; }
    public DateTime? ActualEndTime { get; set; }

    // ─── Recurrence (for PM schedules — auto-create next WO on complete) ───
    public int? RecurrenceMonths { get; set; }
    public int? ParentWorkOrderId { get; set; }
    public WorkOrder? ParentWorkOrder { get; set; }

    // ─── Assignment ───
    [StringLength(200)]
    public string? AssignedTeam { get; set; }

    public string? AssignedToUserId { get; set; }
    public ApplicationUser? AssignedToUser { get; set; }

    public string? CreatedByUserId { get; set; }
    public ApplicationUser? CreatedByUser { get; set; }

    // ─── Description & scope ───
    [StringLength(200)]
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Scope { get; set; }
    public string? SpecialInstructions { get; set; }

    // ─── Costs ───
    public decimal LaborCost { get; set; }
    public decimal MaterialCost { get; set; }
    public decimal TravelCost { get; set; }
    public decimal OtherCost { get; set; }
    public string? CostNotes { get; set; }
    public decimal TotalCost => LaborCost + MaterialCost + TravelCost + OtherCost;

    // ─── Photos & attachments ───
    public string? BeforePhotos { get; set; }
    public string? AfterPhotos { get; set; }
    public string? Attachments { get; set; }

    // ─── Completion ───
    public string? CompletionNotes { get; set; }
    public string? CustomerSignature { get; set; }
    public bool CustomerSigned { get; set; }
    public int? CustomerRating { get; set; }
    public string? CustomerFeedback { get; set; }

    // ─── Link to Service Ticket (optional) ───
    public int? ServiceTicketId { get; set; }
    public ServiceTicket? ServiceTicket { get; set; }

    // ─── Timestamps ───
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Last time we sent a LINE reminder for this WO. Null = never reminded.</summary>
    public DateTime? ReminderSentAt { get; set; }

    // ─── Navigation ───
    public ICollection<WorkOrderItem> ChecklistItems { get; set; } = new List<WorkOrderItem>();
    public ICollection<WorkOrderPhoto> Photos { get; set; } = new List<WorkOrderPhoto>();

    // ─── Computed ───
    public string SiteName => Installation?.SiteName ?? ExternalSiteName ?? "Unspecified Site";
    public string SiteAddress => Installation?.Address ?? ExternalSiteAddress ?? "";
    public bool IsStandalone => InstallationId == null;

    /// <summary>
    /// Who the technician rings when they arrive and the gate is locked.
    /// Prefers the number recorded on the job (a caretaker, a site manager)
    /// over the account holder's, because that's the one that was written
    /// down for this visit specifically.
    /// </summary>
    public string? SiteContactPhone =>
        !string.IsNullOrWhiteSpace(ExternalCustomerPhone) ? ExternalCustomerPhone
        : !string.IsNullOrWhiteSpace(Customer?.Phone) ? Customer!.Phone
        : Installation?.Customer?.Phone;

    /// <summary>
    /// "lat,lng" for a maps link, or null when neither the installation nor the
    /// standalone job carries usable coordinates. 0,0 counts as missing — it's
    /// the Gulf of Guinea, not a site, and it's what an empty form submits.
    /// </summary>
    public string? MapCoords
    {
        get
        {
            var lat = Installation?.Latitude ?? ExternalLatitude;
            var lng = Installation?.Longitude ?? ExternalLongitude;
            if (!lat.HasValue || !lng.HasValue) return null;
            if (Math.Abs(lat.Value) < 0.0001 && Math.Abs(lng.Value) < 0.0001) return null;

            var ci = System.Globalization.CultureInfo.InvariantCulture;
            return $"{lat.Value.ToString(ci)},{lng.Value.ToString(ci)}";
        }
    }
    public bool IsOverdue => Status < WorkOrderStatus.Completed
                             && ScheduledDate.Date < SolarApp.Services.SiteTime.Today;

    public string StatusBadgeClass => Status switch
    {
        WorkOrderStatus.Draft => "bg-secondary",
        WorkOrderStatus.Scheduled => "bg-info",
        WorkOrderStatus.Assigned => "bg-primary",
        WorkOrderStatus.Accepted => "bg-primary",
        WorkOrderStatus.InProgress => "bg-warning text-dark",
        WorkOrderStatus.Completed => "bg-success",
        WorkOrderStatus.Verified => "bg-success",
        WorkOrderStatus.Invoiced => "bg-dark",
        WorkOrderStatus.Closed => "bg-secondary",
        WorkOrderStatus.Cancelled => "bg-danger",
        _ => "bg-secondary"
    };

    public List<string> BeforePhotoList => string.IsNullOrWhiteSpace(BeforePhotos) ? new() :
        BeforePhotos.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    public List<string> AfterPhotoList => string.IsNullOrWhiteSpace(AfterPhotos) ? new() :
        AfterPhotos.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
