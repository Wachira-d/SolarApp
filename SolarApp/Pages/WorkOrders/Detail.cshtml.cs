using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.FileUpload;

namespace SolarApp.Pages.WorkOrders;

[Authorize]
public class DetailModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IFileUploadService _uploader;

    public DetailModel(ApplicationDbContext context, UserManager<ApplicationUser> userManager, IFileUploadService uploader)
    {
        _context = context;
        _userManager = userManager;
        _uploader = uploader;
    }

    public WorkOrder Order { get; set; } = null!;
    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    [BindProperty] public string? CompletionNotes { get; set; }
    [BindProperty] public decimal LaborCost { get; set; }
    [BindProperty] public decimal MaterialCost { get; set; }
    [BindProperty] public decimal TravelCost { get; set; }
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
        Order = wo;
        LaborCost = wo.LaborCost;
        MaterialCost = wo.MaterialCost;
        TravelCost = wo.TravelCost;
        return Page();
    }

    public async Task<IActionResult> OnPostUpdateStatusAsync(int id, WorkOrderStatus newStatus)
    {
        var wo = await _context.WorkOrders.Include(w => w.ChecklistItems).FirstOrDefaultAsync(w => w.Id == id);
        if (wo == null) return NotFound();

        var user = await _userManager.GetUserAsync(User);

        if (newStatus == WorkOrderStatus.InProgress && wo.ActualStartTime == null)
            wo.ActualStartTime = DateTime.UtcNow;

        if (newStatus == WorkOrderStatus.Completed)
        {
            wo.ActualEndTime = DateTime.UtcNow;
            wo.CompletionNotes = CompletionNotes;
            wo.LaborCost = LaborCost;
            wo.MaterialCost = MaterialCost;
            wo.TravelCost = TravelCost;

            // Auto-create next recurring WO
            if (wo.RecurrenceMonths.HasValue && wo.RecurrenceMonths > 0)
            {
                var nextDate = wo.ScheduledDate.AddMonths(wo.RecurrenceMonths.Value);
                var prefix = $"WO-{nextDate:yyyyMM}";
                var lastNum = await _context.WorkOrders
                    .Where(w => w.WorkOrderNumber.StartsWith(prefix))
                    .OrderByDescending(w => w.WorkOrderNumber)
                    .Select(w => w.WorkOrderNumber).FirstOrDefaultAsync();
                var seq = 1;
                if (lastNum != null) { int.TryParse(lastNum[(prefix.Length + 1)..], out seq); seq++; }

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
                // Copy checklist template
                foreach (var item in wo.ChecklistItems)
                {
                    nextWo.ChecklistItems.Add(new WorkOrderItem
                    {
                        SortOrder = item.SortOrder,
                        Description = item.Description
                    });
                }
                _context.WorkOrders.Add(nextWo);
                StatusMessage = $"Status → {newStatus}. Next recurring WO created: {nextWo.WorkOrderNumber} on {nextDate:dd/MM/yyyy}";
            }
            else
            {
                StatusMessage = $"Status → {newStatus}";
            }
        }
        else
        {
            StatusMessage = $"Status → {newStatus}";
        }

        wo.Status = newStatus;
        wo.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return RedirectToPage("Detail", new { id });
    }

    public async Task<IActionResult> OnPostCheckItemAsync(int id, int itemId)
    {
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
