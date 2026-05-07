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
public class EditModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IWorkOrderAccessHelper _access;

    public EditModel(
        ApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        IWorkOrderAccessHelper access)
    {
        _context = context;
        _userManager = userManager;
        _access = access;
    }

    [BindProperty] public EditInput Input { get; set; } = new();
    public WorkOrder Order { get; set; } = null!;
    public List<SelectListItem> Users { get; set; } = new();
    [TempData] public string? StatusMessage { get; set; }

    public class EditInput
    {
        public int Id { get; set; }
        [Required, StringLength(200)] public string Title { get; set; } = "";
        [StringLength(2000)] public string? Description { get; set; }
        [StringLength(2000)] public string? Scope { get; set; }
        [StringLength(2000)] public string? SpecialInstructions { get; set; }
        [Required] public DateTime ScheduledDate { get; set; }
        public string? ScheduledTimeStr { get; set; }
        [Range(15, 24 * 60)] public int EstimatedDurationMinutes { get; set; } = 120;
        [Range(0, 120, ErrorMessage = "Recurrence must be 0-120 months")]
        public int? RecurrenceMonths { get; set; }
        [StringLength(100)] public string? AssignedTeam { get; set; }
        public string? AssignedToUserId { get; set; }
        [Required] public WorkOrderType Type { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var wo = await _context.WorkOrders
            .Include(w => w.Installation)
            .FirstOrDefaultAsync(w => w.Id == id);
        if (wo == null) return NotFound();
        if (!await _access.CanEditAsync(wo, User)) return Forbid();

        Order = wo;
        Input = new EditInput
        {
            Id = wo.Id,
            Title = wo.Title,
            Description = wo.Description,
            Scope = wo.Scope,
            SpecialInstructions = wo.SpecialInstructions,
            ScheduledDate = wo.ScheduledDate,
            ScheduledTimeStr = wo.ScheduledTime?.ToString(@"hh\:mm"),
            EstimatedDurationMinutes = wo.EstimatedDurationMinutes,
            RecurrenceMonths = wo.RecurrenceMonths,
            AssignedTeam = wo.AssignedTeam,
            AssignedToUserId = wo.AssignedToUserId,
            Type = wo.Type,
        };
        await LoadUsersAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var wo = await _context.WorkOrders.FirstOrDefaultAsync(w => w.Id == Input.Id);
        if (wo == null) return NotFound();
        if (!await _access.CanEditAsync(wo, User)) return Forbid();

        // Cannot edit a closed/cancelled WO
        if (wo.Status == WorkOrderStatus.Closed || wo.Status == WorkOrderStatus.Cancelled)
        {
            ModelState.AddModelError(string.Empty, $"Cannot edit a {wo.Status} work order.");
        }

        if (Input.ScheduledDate.Date > DateTime.UtcNow.Date.AddYears(5))
            ModelState.AddModelError("Input.ScheduledDate", "Scheduled date cannot be more than 5 years in the future.");

        if (!ModelState.IsValid)
        {
            Order = wo;
            await LoadUsersAsync();
            return Page();
        }

        // Apply updates
        wo.Title = Input.Title.Trim();
        wo.Description = Input.Description;
        wo.Scope = Input.Scope;
        wo.SpecialInstructions = Input.SpecialInstructions;
        wo.Type = Input.Type;

        // If rescheduling to a different date, clear the reminder flag so the
        // background job fires a fresh LINE notification for the new date.
        if (wo.ScheduledDate.Date != Input.ScheduledDate.Date)
            wo.ReminderSentAt = null;

        wo.ScheduledDate = Input.ScheduledDate;
        wo.ScheduledTime = TimeSpan.TryParse(Input.ScheduledTimeStr, out var ts) ? ts : null;
        wo.EstimatedDurationMinutes = Input.EstimatedDurationMinutes;
        wo.RecurrenceMonths = Input.RecurrenceMonths;
        wo.AssignedTeam = Input.AssignedTeam;

        // If the assignee changed and WO is in Scheduled, bump it to Assigned
        var oldAssignee = wo.AssignedToUserId;
        wo.AssignedToUserId = Input.AssignedToUserId;
        if (oldAssignee != Input.AssignedToUserId
            && wo.Status == WorkOrderStatus.Scheduled
            && !string.IsNullOrEmpty(Input.AssignedToUserId))
        {
            wo.Status = WorkOrderStatus.Assigned;
        }

        wo.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        StatusMessage = "Work order updated.";
        return RedirectToPage("Detail", new { id = wo.Id });
    }

    private async Task LoadUsersAsync()
    {
        var allUsers = await _userManager.Users.Where(u => u.IsActive).OrderBy(u => u.FullName).ToListAsync();
        Users = allUsers.Select(u => new SelectListItem(u.FullName ?? u.Email ?? u.Id, u.Id)).ToList();
    }
}
