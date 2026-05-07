using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.Authorization;
using SolarApp.Services.FileUpload;

namespace SolarApp.Pages.WorkOrders;

[Authorize]
public class DetailModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IFileUploadService _uploader;
    private readonly IWorkOrderAccessHelper _access;

    public DetailModel(
        ApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        IFileUploadService uploader,
        IWorkOrderAccessHelper access)
    {
        _context = context;
        _userManager = userManager;
        _uploader = uploader;
        _access = access;
    }

    public WorkOrder Order { get; set; } = null!;
    public bool CanEdit { get; set; }
    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    [BindProperty] public string? CompletionNotes { get; set; }
    [BindProperty, System.ComponentModel.DataAnnotations.Range(0, 9_999_999)] public decimal LaborCost { get; set; }
    [BindProperty, System.ComponentModel.DataAnnotations.Range(0, 9_999_999)] public decimal MaterialCost { get; set; }
    [BindProperty, System.ComponentModel.DataAnnotations.Range(0, 9_999_999)] public decimal TravelCost { get; set; }
    [BindProperty] public List<IFormFile> BeforePhotoFiles { get; set; } = new();
    [BindProperty] public List<IFormFile> AfterPhotoFiles { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var wo = await _context.WorkOrders
            .Include(w => w.Installation)
            .Include(w => w.Customer)
            .Include(w => w.AssignedToUser)
            .Include(w => w.CreatedByUser)
            .Include(w => w.ChecklistItems.OrderBy(i => i.SortOrder))
            .Include(w => w.Photos)
            .Include(w => w.ServiceTicket)
            .FirstOrDefaultAsync(w => w.Id == id);
        if (wo == null) return NotFound();
        if (!await _access.CanViewAsync(wo, User)) return Forbid();

        Order = wo;
        CanEdit = await _access.CanEditAsync(wo, User);
        LaborCost = wo.LaborCost;
        MaterialCost = wo.MaterialCost;
        TravelCost = wo.TravelCost;
        return Page();
    }

    public async Task<IActionResult> OnPostUpdateStatusAsync(int id, WorkOrderStatus newStatus)
    {
        var wo = await _context.WorkOrders.Include(w => w.ChecklistItems).FirstOrDefaultAsync(w => w.Id == id);
        if (wo == null) return NotFound();
        if (!await _access.CanEditAsync(wo, User)) return Forbid();

        var user = await _userManager.GetUserAsync(User);

        if (newStatus == WorkOrderStatus.InProgress && wo.ActualStartTime == null)
            wo.ActualStartTime = DateTime.UtcNow;

        if (newStatus == WorkOrderStatus.Completed)
        {
            if (LaborCost < 0 || MaterialCost < 0 || TravelCost < 0)
            {
                ErrorMessage = "Costs must be zero or positive.";
                return RedirectToPage("Detail", new { id });
            }

            // Wrap completion + recurring-WO creation in a transaction so we don't
            // create duplicate next-cycle WOs when two users click "Complete" simultaneously.
            await using var tx = await _context.Database.BeginTransactionAsync();

            // Re-load with row-lock semantics (EF translates to UPDLOCK on SaveChanges)
            // and bail if already completed (idempotent guard against double-clicks).
            if (wo.Status == WorkOrderStatus.Completed
                || wo.Status == WorkOrderStatus.Verified
                || wo.Status == WorkOrderStatus.Closed)
            {
                StatusMessage = "Work order already completed.";
                return RedirectToPage("Detail", new { id });
            }

            wo.ActualEndTime = DateTime.UtcNow;
            wo.CompletionNotes = CompletionNotes;
            wo.LaborCost = LaborCost;
            wo.MaterialCost = MaterialCost;
            wo.TravelCost = TravelCost;

            string? createdNumber = null;
            if (wo.RecurrenceMonths.HasValue && wo.RecurrenceMonths > 0 && wo.RecurrenceMonths <= 120)
            {
                var nextDate = wo.ScheduledDate.AddMonths(wo.RecurrenceMonths.Value);

                // Idempotency: if a child WO already exists for this parent + recurrence date, skip creation.
                var existingChild = await _context.WorkOrders
                    .AnyAsync(w => w.ParentWorkOrderId == wo.Id && w.ScheduledDate.Date == nextDate.Date);

                if (!existingChild)
                {
                    var prefix = $"WO-{nextDate:yyyyMM}";
                    var lastNum = await _context.WorkOrders
                        .Where(w => w.WorkOrderNumber.StartsWith(prefix))
                        .OrderByDescending(w => w.WorkOrderNumber)
                        .Select(w => w.WorkOrderNumber).FirstOrDefaultAsync();
                    var seq = 1;
                    if (lastNum != null && lastNum.Length > prefix.Length + 1
                        && int.TryParse(lastNum[(prefix.Length + 1)..], out var parsed))
                    {
                        seq = parsed + 1;
                    }

                    var nextWo = new WorkOrder
                    {
                        WorkOrderNumber = $"{prefix}-{seq:D4}",
                        Type = wo.Type,
                        Status = WorkOrderStatus.Scheduled,
                        Title = wo.Title,
                        Description = wo.Description,
                        Scope = wo.Scope,
                        SpecialInstructions = wo.SpecialInstructions,
                        InstallationId = wo.InstallationId,
                        ExternalSiteName = wo.ExternalSiteName,
                        ExternalSiteAddress = wo.ExternalSiteAddress,
                        ExternalCustomerName = wo.ExternalCustomerName,
                        ExternalCustomerPhone = wo.ExternalCustomerPhone,
                        ExternalLatitude = wo.ExternalLatitude,
                        ExternalLongitude = wo.ExternalLongitude,
                        CustomerId = wo.CustomerId,
                        ScheduledDate = nextDate,
                        ScheduledTime = wo.ScheduledTime,
                        EstimatedDurationMinutes = wo.EstimatedDurationMinutes,
                        RecurrenceMonths = wo.RecurrenceMonths,
                        ParentWorkOrderId = wo.Id,
                        AssignedTeam = wo.AssignedTeam,
                        AssignedToUserId = wo.AssignedToUserId,
                        CreatedByUserId = user?.Id,
                    };
                    foreach (var item in wo.ChecklistItems)
                    {
                        nextWo.ChecklistItems.Add(new WorkOrderItem
                        {
                            SortOrder = item.SortOrder,
                            Description = item.Description
                        });
                    }
                    _context.WorkOrders.Add(nextWo);
                    createdNumber = nextWo.WorkOrderNumber;
                }
            }

            wo.Status = newStatus;
            wo.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            await tx.CommitAsync();

            StatusMessage = createdNumber != null
                ? $"Status → {newStatus}. Next recurring WO created: {createdNumber}"
                : $"Status → {newStatus}";
            return RedirectToPage("Detail", new { id });
        }

        wo.Status = newStatus;
        wo.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        StatusMessage = $"Status → {newStatus}";
        return RedirectToPage("Detail", new { id });
    }

    public async Task<IActionResult> OnPostCheckItemAsync(int id, int itemId)
    {
        var wo = await _context.WorkOrders.FirstOrDefaultAsync(w => w.Id == id);
        if (wo == null) return NotFound();
        if (!await _access.CanEditAsync(wo, User)) return Forbid();

        var item = await _context.WorkOrderItems.FindAsync(itemId);
        if (item == null || item.WorkOrderId != id) return NotFound();
        var user = await _userManager.GetUserAsync(User);
        item.IsCompleted = !item.IsCompleted;
        item.CompletedAt = item.IsCompleted ? DateTime.UtcNow : null;
        item.CompletedByUserId = item.IsCompleted ? user?.Id : null;
        await _context.SaveChangesAsync();
        return RedirectToPage("Detail", new { id });
    }

    public async Task<IActionResult> OnPostUploadPhotosAsync(int id, string kind)
    {
        var wo = await _context.WorkOrders.FindAsync(id);
        if (wo == null) return NotFound();
        if (!await _access.CanEditAsync(wo, User)) return Forbid();

        var files = kind == "before" ? BeforePhotoFiles : AfterPhotoFiles;
        foreach (var file in files.Where(f => f != null && f.Length > 0).Take(10))
        {
            var (ok, url) = await _uploader.UploadAsync(file, $"wo-{kind}", id);
            if (ok)
            {
                if (kind == "before")
                    wo.BeforePhotos = _uploader.AddToCsv(wo.BeforePhotos, url);
                else
                    wo.AfterPhotos = _uploader.AddToCsv(wo.AfterPhotos, url);
            }
        }
        wo.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return RedirectToPage("Detail", new { id });
    }
}
