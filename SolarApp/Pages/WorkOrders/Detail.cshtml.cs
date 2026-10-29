using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.Authorization;
using SolarApp.Services.FieldWork;
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
                // Customer comes along so the page can offer the site contact
                // number — a locked gate is a phone call, not a support ticket.
                .ThenInclude(i => i!.Customer)
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
        var wo = await _context.WorkOrders
            .Include(w => w.Installation)
            .Include(w => w.ServiceTicket)
            .Include(w => w.ChecklistItems)
            .FirstOrDefaultAsync(w => w.Id == id);
        if (wo == null) return NotFound();
        if (!await _access.CanEditAsync(wo, User)) return Forbid();

        var user = await _userManager.GetUserAsync(User);
        var isAdmin = User.IsInRole(UserRoles.Admin);

        // The page only hides the buttons that don't apply; the URL still has
        // to be refused, or a cancelled order could be "started" again.
        if (!WorkOrderCompletion.CanTransition(wo.Status, newStatus, isAdmin))
        {
            ErrorMessage = $"เปลี่ยนสถานะจาก \"{UiLabels.Label(wo.Status)}\" เป็น \"{UiLabels.Label(newStatus)}\" ไม่ได้";
            return RedirectToPage("Detail", new { id });
        }

        var now = DateTime.UtcNow;

        if (newStatus == WorkOrderStatus.InProgress)
        {
            wo.ActualStartTime ??= now;
            // Reopening a completed order: it is being worked on again.
            wo.ActualEndTime = null;
        }

        if (newStatus == WorkOrderStatus.Completed)
        {
            if (LaborCost < 0 || MaterialCost < 0 || TravelCost < 0)
            {
                ErrorMessage = "ค่าใช้จ่ายต้องเป็นศูนย์หรือมากกว่า";
                return RedirectToPage("Detail", new { id });
            }

            // Completion + next-cycle creation in one transaction so two
            // taps on "ปิดงาน" can't create two next orders.
            await using var tx = await _context.Database.BeginTransactionAsync();

            wo.LaborCost = LaborCost;
            wo.MaterialCost = MaterialCost;
            wo.TravelCost = TravelCost;
            await WorkOrderCompletion.ApplyAsync(_context, wo, now, user?.Id, CompletionNotes);

            string? createdNumber = null;
            if (wo.RecurrenceMonths.HasValue && wo.RecurrenceMonths > 0 && wo.RecurrenceMonths <= 120)
            {
                var nextDate = wo.ScheduledDate.AddMonths(wo.RecurrenceMonths.Value);

                // Idempotency: if a child WO already exists for this parent + recurrence date, skip creation.
                var existingChild = await _context.WorkOrders
                    .AnyAsync(w => w.ParentWorkOrderId == wo.Id && w.ScheduledDate.Date == nextDate.Date);

                if (!existingChild)
                {
                    var nextWo = new WorkOrder
                    {
                        WorkOrderNumber = await WorkOrderCompletion.NextNumberAsync(_context, nextDate),
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

            await _context.SaveChangesAsync();
            await tx.CommitAsync();

            StatusMessage = createdNumber != null
                ? $"ปิดงานแล้ว · สร้างงานรอบถัดไป {createdNumber} ให้แล้ว"
                : wo.Type == WorkOrderType.PanelCleaning && wo.InstallationId.HasValue
                    ? "ปิดงานแล้ว · บันทึกลงประวัติล้างแผงของไซต์และตั้งรอบถัดไปให้แล้ว"
                    : "ปิดงานแล้ว";
            return RedirectToPage("Detail", new { id });
        }

        if (newStatus == WorkOrderStatus.Cancelled && wo.ServiceTicket != null
            && wo.ServiceTicket.Status == TicketStatus.InProgress)
        {
            // The ticket goes back to the queue; it was not answered.
            wo.ServiceTicket.Status = TicketStatus.Open;
            wo.ServiceTicket.UpdatedAt = now;
        }

        wo.Status = newStatus;
        wo.UpdatedAt = now;
        await _context.SaveChangesAsync();
        StatusMessage = newStatus switch
        {
            WorkOrderStatus.InProgress => "เริ่มงานแล้ว",
            WorkOrderStatus.Verified => "ตรวจรับงานแล้ว",
            WorkOrderStatus.Closed => "ปิดงานถาวรแล้ว",
            WorkOrderStatus.Cancelled => "ยกเลิกงานแล้ว",
            _ => $"สถานะ: {UiLabels.Label(newStatus)}"
        };
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
