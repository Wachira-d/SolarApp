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

namespace SolarApp.Pages.WorkOrders;

[Authorize(Roles = "Admin,Partner")]
public class CreateModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;

    public CreateModel(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    [BindProperty] public WorkOrderInput Input { get; set; } = new();

    public List<SelectListItem> Installations { get; set; } = new();
    public List<SelectListItem> Customers { get; set; } = new();
    public List<SelectListItem> Users { get; set; } = new();

    public class WorkOrderInput
    {
        [Required] public WorkOrderType Type { get; set; } = WorkOrderType.PanelCleaning;
        [Required, StringLength(200)] public string Title { get; set; } = "";
        public string? Description { get; set; }
        public string? Scope { get; set; }
        public string? SpecialInstructions { get; set; }

        // Linked installation (optional)
        public int? InstallationId { get; set; }

        // Standalone site info
        public string? ExternalSiteName { get; set; }
        public string? ExternalSiteAddress { get; set; }
        public string? ExternalCustomerName { get; set; }
        public string? ExternalCustomerPhone { get; set; }
        public double? ExternalLatitude { get; set; }
        public double? ExternalLongitude { get; set; }

        public int? CustomerId { get; set; }

        [Required] public DateTime ScheduledDate { get; set; } = DateTime.Today.AddDays(1);
        public string? ScheduledTimeStr { get; set; }
        public int EstimatedDurationMinutes { get; set; } = 120;

        public int? RecurrenceMonths { get; set; }
        public string? AssignedTeam { get; set; }
        public string? AssignedToUserId { get; set; }

        // Checklist template
        public string? ChecklistItems { get; set; }
    }

    public async Task OnGetAsync(int? installationId = null, string? type = null)
    {
        await LoadDropdownsAsync();

        if (installationId.HasValue)
            Input.InstallationId = installationId;

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
    }

    public async Task<IActionResult> OnPostAsync()
    {
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
        if (lastNum != null && lastNum.Length > prefix.Length + 1)
            int.TryParse(lastNum[(prefix.Length + 1)..], out seq);
        seq = Math.Max(seq, 1);
        if (lastNum != null) seq++;

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
        Installations = await _context.Installations
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
