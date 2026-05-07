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

namespace SolarApp.Pages.WorkOrders;

[Authorize(Roles = "Admin,Partner")]
public class CreateModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IInstallationAccessHelper _installAccess;

    public CreateModel(
        ApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        IInstallationAccessHelper installAccess)
    {
        _context = context;
        _userManager = userManager;
        _installAccess = installAccess;
    }

    [BindProperty] public WorkOrderInput Input { get; set; } = new();

    public List<SelectListItem> Installations { get; set; } = new();
    public List<SelectListItem> Customers { get; set; } = new();
    public List<SelectListItem> Users { get; set; } = new();

    public class WorkOrderInput
    {
        [Required] public WorkOrderType Type { get; set; } = WorkOrderType.PanelCleaning;
        [Required, StringLength(200)] public string Title { get; set; } = "";
        [StringLength(2000)] public string? Description { get; set; }
        [StringLength(2000)] public string? Scope { get; set; }
        [StringLength(2000)] public string? SpecialInstructions { get; set; }

        // Linked installation (optional)
        public int? InstallationId { get; set; }

        // Standalone site info
        [StringLength(200)] public string? ExternalSiteName { get; set; }
        [StringLength(500)] public string? ExternalSiteAddress { get; set; }
        [StringLength(200)] public string? ExternalCustomerName { get; set; }
        [StringLength(50)] public string? ExternalCustomerPhone { get; set; }
        public double? ExternalLatitude { get; set; }
        public double? ExternalLongitude { get; set; }

        public int? CustomerId { get; set; }

        [Required] public DateTime ScheduledDate { get; set; } = DateTime.Today.AddDays(1);
        public string? ScheduledTimeStr { get; set; }
        [Range(15, 24 * 60)] public int EstimatedDurationMinutes { get; set; } = 120;

        [Range(0, 120, ErrorMessage = "Recurrence must be 0-120 months")]
        public int? RecurrenceMonths { get; set; }
        [StringLength(100)] public string? AssignedTeam { get; set; }
        public string? AssignedToUserId { get; set; }

        // Checklist template
        public string? ChecklistItems { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(int? installationId = null, string? type = null)
    {
        await LoadDropdownsAsync();

        if (installationId.HasValue)
        {
            // Validate the user is allowed to create against this installation
            if (!await _installAccess.CanAccessAsync(installationId.Value, User))
                return Forbid();
            Input.InstallationId = installationId;
        }

        if (Enum.TryParse<WorkOrderType>(type, true, out var woType))
            Input.Type = woType;

        // Set default title based on type
        if (string.IsNullOrEmpty(Input.Title))
        {
            Input.Title = Input.Type switch
            {
                WorkOrderType.PanelCleaning => "Panel Cleaning",
                WorkOrderType.PreventiveMaintenance => "Preventive Maintenance",
                WorkOrderType.CorrectiveRepair => "Corrective Repair",
                WorkOrderType.Inspection => "Site Inspection",
                _ => ""
            };
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        // Custom validation
        if (Input.ScheduledDate.Date < DateTime.UtcNow.Date.AddDays(-30))
            ModelState.AddModelError("Input.ScheduledDate", "Scheduled date cannot be more than 30 days in the past.");

        if (Input.ScheduledDate.Date > DateTime.UtcNow.Date.AddYears(5))
            ModelState.AddModelError("Input.ScheduledDate", "Scheduled date cannot be more than 5 years in the future.");

        // Either linked installation OR standalone site name must be provided
        if (!Input.InstallationId.HasValue && string.IsNullOrWhiteSpace(Input.ExternalSiteName))
            ModelState.AddModelError("Input.ExternalSiteName", "Provide either a linked installation or a standalone site name.");

        // Validate Partner cannot create against an installation they don't own
        if (Input.InstallationId.HasValue && !await _installAccess.CanAccessAsync(Input.InstallationId.Value, User))
            ModelState.AddModelError("Input.InstallationId", "You do not have access to that installation.");

        if (!ModelState.IsValid)
        {
            await LoadDropdownsAsync();
            return Page();
        }

        var user = await _userManager.GetUserAsync(User);

        // Generate WO number: WO-YYYYMM-NNNN
        var prefix = $"WO-{DateTime.UtcNow:yyyyMM}";
        var lastNum = await _context.WorkOrders
            .Where(w => w.WorkOrderNumber.StartsWith(prefix))
            .OrderByDescending(w => w.WorkOrderNumber)
            .Select(w => w.WorkOrderNumber)
            .FirstOrDefaultAsync();
        var seq = 1;
        if (lastNum != null && lastNum.Length > prefix.Length + 1
            && int.TryParse(lastNum[(prefix.Length + 1)..], out var parsed))
        {
            seq = parsed + 1;
        }

        TimeSpan? scheduledTime = null;
        if (TimeSpan.TryParse(Input.ScheduledTimeStr, out var ts)) scheduledTime = ts;

        var wo = new WorkOrder
        {
            WorkOrderNumber = $"{prefix}-{seq:D4}",
            Type = Input.Type,
            Status = !string.IsNullOrWhiteSpace(Input.AssignedToUserId)
                ? WorkOrderStatus.Assigned : WorkOrderStatus.Scheduled,
            Title = Input.Title.Trim(),
            Description = Input.Description,
            Scope = Input.Scope,
            SpecialInstructions = Input.SpecialInstructions,
            InstallationId = Input.InstallationId,
            ExternalSiteName = Input.ExternalSiteName,
            ExternalSiteAddress = Input.ExternalSiteAddress,
            ExternalCustomerName = Input.ExternalCustomerName,
            ExternalCustomerPhone = Input.ExternalCustomerPhone,
            ExternalLatitude = Input.ExternalLatitude,
            ExternalLongitude = Input.ExternalLongitude,
            CustomerId = Input.CustomerId,
            ScheduledDate = Input.ScheduledDate,
            ScheduledTime = scheduledTime,
            EstimatedDurationMinutes = Input.EstimatedDurationMinutes,
            RecurrenceMonths = Input.RecurrenceMonths,
            AssignedTeam = Input.AssignedTeam,
            AssignedToUserId = Input.AssignedToUserId,
            CreatedByUserId = user?.Id,
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
                    Description = line.Trim()
                });
            }
        }

        _context.WorkOrders.Add(wo);
        await _context.SaveChangesAsync();

        return RedirectToPage("Detail", new { id = wo.Id });
    }

    private async Task LoadDropdownsAsync()
    {
        // Filter installations by what the user can access
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
}
