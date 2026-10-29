using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.Authorization;
using SolarApp.Services.FieldWork;
using SolarApp.Services.FileUpload;
using SolarApp.Services.WorkOrderTemplates;

namespace SolarApp.Pages.WorkOrders;

/// <summary>
/// One form for every site visit — planned ahead, or written up after the
/// crew is back in the truck. The second case is the one that used to be
/// confusing: a visit already done had to be "scheduled", then "started",
/// then "completed" across three screens, or filed on a separate maintenance
/// page that nothing else read. "ทำเสร็จแล้ว" on this form now records it in
/// one step, photos included, the way the cleaning sheet always has.
/// </summary>
[Authorize(Roles = "Admin,Partner")]
public class CreateModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IInstallationAccessHelper _installAccess;
    private readonly IWorkOrderTemplateService _templates;
    private readonly IFileUploadService _uploader;

    /// <summary>Per-type defaults injected into the page as JSON for the client-side type handler.</summary>
    public Dictionary<int, object> TypeDefaults { get; set; } = new();

    public CreateModel(
        ApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        IInstallationAccessHelper installAccess,
        IWorkOrderTemplateService templates,
        IFileUploadService uploader)
    {
        _context = context;
        _userManager = userManager;
        _installAccess = installAccess;
        _templates = templates;
        _uploader = uploader;
    }

    [BindProperty] public WorkOrderInput Input { get; set; } = new();
    [BindProperty] public List<IFormFile> BeforePhotoFiles { get; set; } = new();
    [BindProperty] public List<IFormFile> AfterPhotoFiles { get; set; } = new();

    public List<SelectListItem> Installations { get; set; } = new();
    public List<SelectListItem> Customers { get; set; } = new();
    public List<SelectListItem> Users { get; set; } = new();

    /// <summary>
    /// Set when the form was opened from a site page: the site is fixed and
    /// shown as a heading instead of a picker, and the form starts in its
    /// short layout.
    /// </summary>
    public Installation? LockedSite { get; set; }

    /// <summary>The ticket this order answers, when opened from one.</summary>
    public ServiceTicket? SourceTicket { get; set; }

    public class WorkOrderInput
    {
        [Required(ErrorMessage = "กรุณาเลือกประเภทงาน")]
        public WorkOrderType Type { get; set; } = WorkOrderType.PanelCleaning;

        [Required(ErrorMessage = "กรุณากรอกหัวข้องาน")]
        [StringLength(200, ErrorMessage = "หัวข้องานต้องไม่เกิน 200 ตัวอักษร")]
        public string Title { get; set; } = "";

        [StringLength(2000, ErrorMessage = "รายละเอียดต้องไม่เกิน 2000 ตัวอักษร")]
        public string? Description { get; set; }
        [StringLength(2000, ErrorMessage = "ขอบเขตงานต้องไม่เกิน 2000 ตัวอักษร")]
        public string? Scope { get; set; }
        [StringLength(2000, ErrorMessage = "หมายเหตุพิเศษต้องไม่เกิน 2000 ตัวอักษร")]
        public string? SpecialInstructions { get; set; }

        // Linked installation (optional)
        public int? InstallationId { get; set; }
        public int? ServiceTicketId { get; set; }

        // Standalone site info
        [StringLength(200, ErrorMessage = "ชื่อไซต์ต้องไม่เกิน 200 ตัวอักษร")]
        public string? ExternalSiteName { get; set; }
        [StringLength(500, ErrorMessage = "ที่อยู่ต้องไม่เกิน 500 ตัวอักษร")]
        public string? ExternalSiteAddress { get; set; }
        [StringLength(200, ErrorMessage = "ชื่อลูกค้าต้องไม่เกิน 200 ตัวอักษร")]
        public string? ExternalCustomerName { get; set; }
        [StringLength(50, ErrorMessage = "เบอร์โทรต้องไม่เกิน 50 ตัวอักษร")]
        public string? ExternalCustomerPhone { get; set; }
        public double? ExternalLatitude { get; set; }
        public double? ExternalLongitude { get; set; }

        public int? CustomerId { get; set; }

        [Required(ErrorMessage = "กรุณาเลือกวันที่")]
        public DateTime ScheduledDate { get; set; } = SolarApp.Services.SiteTime.Today;
        public string? ScheduledTimeStr { get; set; }

        [Range(15, 24 * 60, ErrorMessage = "ระยะเวลาต้องอยู่ระหว่าง 15 นาที ถึง 24 ชั่วโมง")]
        public int EstimatedDurationMinutes { get; set; } = 120;

        [Range(0, 120, ErrorMessage = "การทำซ้ำต้องอยู่ระหว่าง 0-120 เดือน")]
        public int? RecurrenceMonths { get; set; }
        [StringLength(100, ErrorMessage = "ชื่อทีมต้องไม่เกิน 100 ตัวอักษร")]
        public string? AssignedTeam { get; set; }
        public string? AssignedToUserId { get; set; }

        // Checklist template
        public string? ChecklistItems { get; set; }

        /// <summary>The visit already happened: file it as completed in one step.</summary>
        public bool MarkCompleted { get; set; }
        [StringLength(2000, ErrorMessage = "บันทึกปิดงานต้องไม่เกิน 2000 ตัวอักษร")]
        public string? CompletionNotes { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(int? installationId = null, string? type = null,
        int? ticketId = null, bool done = false)
    {
        await LoadDropdownsAsync();
        await LoadTypeDefaultsAsync();

        if (installationId.HasValue)
        {
            if (!await _installAccess.CanAccessAsync(installationId.Value, User))
                return Forbid();
            Input.InstallationId = installationId;
            LockedSite = await _context.Installations.AsNoTracking()
                .Include(i => i.Customer)
                .FirstOrDefaultAsync(i => i.Id == installationId.Value);
        }

        if (ticketId.HasValue)
        {
            var ticket = await _context.ServiceTickets.AsNoTracking()
                .Include(t => t.Installation).ThenInclude(i => i.Customer)
                .FirstOrDefaultAsync(t => t.Id == ticketId.Value);
            if (ticket != null && await _installAccess.CanAccessAsync(ticket.InstallationId, User))
            {
                SourceTicket = ticket;
                Input.ServiceTicketId = ticket.Id;
                Input.InstallationId = ticket.InstallationId;
                LockedSite = ticket.Installation;
                Input.Type = WorkOrderType.CorrectiveRepair;
                Input.Title = $"แจ้งซ่อม {ticket.TicketNumber}: {ticket.Subject}";
                Input.Description = ticket.Description;
            }
        }

        if (Enum.TryParse<WorkOrderType>(type, true, out var woType))
            Input.Type = woType;

        Input.MarkCompleted = done;

        // Client-side JS fills Title + ChecklistItems from per-type defaults
        // when they are empty, so a ticket's title above is kept.
        return Page();
    }

    // Recording a visit after the fact needs a longer window than planning
    // one ahead: paperwork from the last quarter is normal, next decade is not.
    private const int MaxBackdateDays = 120;
    private const int MaxFutureYears = 5;

    public async Task<IActionResult> OnPostAsync()
    {
        if (Input.ScheduledDate.Date < SolarApp.Services.SiteTime.Today.AddDays(-MaxBackdateDays))
            ModelState.AddModelError("Input.ScheduledDate",
                $"วันที่ย้อนหลังได้ไม่เกิน {MaxBackdateDays} วัน");

        if (Input.ScheduledDate.Date > SolarApp.Services.SiteTime.Today.AddYears(MaxFutureYears))
            ModelState.AddModelError("Input.ScheduledDate",
                $"วันที่นัดไม่สามารถเกิน {MaxFutureYears} ปีในอนาคตได้");

        if (Input.MarkCompleted && Input.ScheduledDate.Date > SolarApp.Services.SiteTime.Today)
            ModelState.AddModelError("Input.ScheduledDate",
                "งานที่ทำเสร็จแล้วต้องเป็นวันนี้หรือย้อนหลัง");

        // Either linked installation OR standalone site name must be provided
        if (!Input.InstallationId.HasValue && string.IsNullOrWhiteSpace(Input.ExternalSiteName))
            ModelState.AddModelError("Input.ExternalSiteName",
                "กรุณาเลือกไซต์ในระบบ หรือกรอกชื่อไซต์นอกระบบ อย่างใดอย่างหนึ่ง");

        if (Input.InstallationId.HasValue && !string.IsNullOrWhiteSpace(Input.ExternalSiteName))
            ModelState.AddModelError("Input.ExternalSiteName",
                "เลือกไซต์ในระบบไว้แล้ว — ลบชื่อไซต์นอกระบบออก หรือยกเลิกการเลือกไซต์ในระบบ");

        if (Input.InstallationId.HasValue && !await _installAccess.CanAccessAsync(Input.InstallationId.Value, User))
            ModelState.AddModelError("Input.InstallationId", "คุณไม่มีสิทธิ์เข้าถึงไซต์นี้");

        ServiceTicket? ticket = null;
        if (Input.ServiceTicketId.HasValue)
        {
            ticket = await _context.ServiceTickets.FirstOrDefaultAsync(t => t.Id == Input.ServiceTicketId.Value);
            if (ticket == null || ticket.InstallationId != Input.InstallationId)
                ModelState.AddModelError("Input.ServiceTicketId", "เรื่องแจ้งซ่อมไม่ตรงกับไซต์ที่เลือก");
            // The button is hidden once a visit is booked, but a stale tab
            // would still book a second one.
            else if (await _context.WorkOrders.AnyAsync(w => w.ServiceTicketId == ticket.Id
                                                             && w.Status != WorkOrderStatus.Cancelled))
                ModelState.AddModelError("Input.ServiceTicketId", "เรื่องแจ้งซ่อมนี้มีใบสั่งงานอยู่แล้ว");
        }

        if (!ModelState.IsValid)
        {
            await LoadDropdownsAsync();
            await LoadTypeDefaultsAsync();
            if (Input.InstallationId.HasValue)
                LockedSite = await _context.Installations.AsNoTracking().Include(i => i.Customer)
                    .FirstOrDefaultAsync(i => i.Id == Input.InstallationId.Value);
            if (Input.ServiceTicketId.HasValue)
                SourceTicket = await _context.ServiceTickets.AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Id == Input.ServiceTicketId.Value);
            return Page();
        }

        var user = await _userManager.GetUserAsync(User);
        var now = DateTime.UtcNow;

        // A visit written up afterwards is completed on the day it was done,
        // not the moment it was typed in — checklist ticks included.
        var completedAt = Input.ScheduledDate.Date == SolarApp.Services.SiteTime.Today
            ? now
            : SolarApp.Services.SiteTime.ToUtc(Input.ScheduledDate.Date.AddHours(12));

        TimeSpan? scheduledTime = null;
        if (TimeSpan.TryParse(Input.ScheduledTimeStr, out var ts)) scheduledTime = ts;

        var wo = new WorkOrder
        {
            WorkOrderNumber = await WorkOrderCompletion.NextNumberAsync(_context, now),
            Type = Input.Type,
            Status = !string.IsNullOrWhiteSpace(Input.AssignedToUserId)
                ? WorkOrderStatus.Assigned : WorkOrderStatus.Scheduled,
            Title = Input.Title.Trim(),
            Description = Input.Description,
            Scope = Input.Scope,
            SpecialInstructions = Input.SpecialInstructions,
            InstallationId = Input.InstallationId,
            ServiceTicketId = ticket?.Id,
            ExternalSiteName = Input.InstallationId.HasValue ? null : Input.ExternalSiteName,
            ExternalSiteAddress = Input.InstallationId.HasValue ? null : Input.ExternalSiteAddress,
            ExternalCustomerName = Input.InstallationId.HasValue ? null : Input.ExternalCustomerName,
            ExternalCustomerPhone = Input.InstallationId.HasValue ? null : Input.ExternalCustomerPhone,
            ExternalLatitude = Input.InstallationId.HasValue ? null : Input.ExternalLatitude,
            ExternalLongitude = Input.InstallationId.HasValue ? null : Input.ExternalLongitude,
            CustomerId = Input.CustomerId,
            ScheduledDate = Input.ScheduledDate.Date,
            ScheduledTime = scheduledTime,
            EstimatedDurationMinutes = Input.EstimatedDurationMinutes,
            RecurrenceMonths = Input.RecurrenceMonths,
            AssignedTeam = Input.AssignedTeam,
            AssignedToUserId = Input.AssignedToUserId,
            CreatedByUserId = user?.Id,
            CreatedAt = now,
            UpdatedAt = now
        };

        // Parse checklist items (newline-separated from textarea)
        if (!string.IsNullOrWhiteSpace(Input.ChecklistItems))
        {
            var lines = Input.ChecklistItems.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int order = 0;
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                wo.ChecklistItems.Add(new WorkOrderItem
                {
                    SortOrder = order++,
                    Description = line.Trim(),
                    // A visit written up afterwards was done in full.
                    IsCompleted = Input.MarkCompleted,
                    CompletedAt = Input.MarkCompleted ? completedAt : null,
                    CompletedByUserId = Input.MarkCompleted ? user?.Id : null
                });
            }
        }

        _context.WorkOrders.Add(wo);

        // Photos need the id for their folder, so the order is saved first.
        await _context.SaveChangesAsync();

        var uploaded = false;
        foreach (var file in BeforePhotoFiles.Where(f => f != null && f.Length > 0).Take(10))
        {
            var (ok, url) = await _uploader.UploadAsync(file, "wo-before", wo.Id);
            if (ok) { wo.BeforePhotos = _uploader.AddToCsv(wo.BeforePhotos, url); uploaded = true; }
        }
        foreach (var file in AfterPhotoFiles.Where(f => f != null && f.Length > 0).Take(10))
        {
            var (ok, url) = await _uploader.UploadAsync(file, "wo-after", wo.Id);
            if (ok) { wo.AfterPhotos = _uploader.AddToCsv(wo.AfterPhotos, url); uploaded = true; }
        }

        if (ticket != null && ticket.Status == TicketStatus.Open)
        {
            ticket.Status = TicketStatus.InProgress;
            ticket.UpdatedAt = now;
        }

        if (Input.MarkCompleted)
        {
            await WorkOrderCompletion.ApplyAsync(_context, wo, completedAt, user?.Id, Input.CompletionNotes);
        }

        if (uploaded || ticket != null || Input.MarkCompleted)
            await _context.SaveChangesAsync();

        TempData["StatusMessage"] = Input.MarkCompleted
            ? $"บันทึกงาน {wo.WorkOrderNumber} เป็นเสร็จแล้ว"
            : $"สร้างใบสั่งงาน {wo.WorkOrderNumber} แล้ว";
        return RedirectToPage("Detail", new { id = wo.Id });
    }

    private async Task LoadDropdownsAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        var roles = user != null ? await _userManager.GetRolesAsync(user) : new List<string>();

        var installQ = _context.Installations.AsQueryable();
        if (user != null && !roles.Contains(UserRoles.Admin) && roles.Contains(UserRoles.Partner))
            installQ = installQ.Where(i => i.PartnerId == user.PartnerId);

        Installations = await installQ
            .OrderBy(i => i.SiteName)
            .Select(i => new SelectListItem($"{i.SiteName} ({i.SiteCode})", i.Id.ToString()))
            .ToListAsync();

        Customers = await _context.Customers
            .Where(c => c.IsActive)
            .OrderBy(c => c.FirstName)
            .Select(c => new SelectListItem(c.FirstName + " " + c.LastName, c.Id.ToString()))
            .ToListAsync();

        var allUsers = await _userManager.Users.Where(u => u.IsActive).OrderBy(u => u.FullName).ToListAsync();
        Users = allUsers.Select(u => new SelectListItem(u.FullName ?? u.Email ?? u.Id, u.Id)).ToList();
    }

    private async Task LoadTypeDefaultsAsync()
    {
        var all = await _templates.GetAllAsync();
        TypeDefaults = all.ToDictionary(
            kv => (int)kv.Key,
            kv => (object)new { title = kv.Value.Title, checklist = kv.Value.ChecklistItems });
    }
}
