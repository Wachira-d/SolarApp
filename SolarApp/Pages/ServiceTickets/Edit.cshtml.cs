using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.Interfaces;

namespace SolarApp.Pages.ServiceTickets;

[Authorize]
public class EditModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IMaintenanceService _service;

    public EditModel(ApplicationDbContext context, UserManager<ApplicationUser> userManager, IMaintenanceService service)
    {
        _context = context;
        _userManager = userManager;
        _service = service;
    }

    [BindProperty]
    public TicketEditInput Input { get; set; } = new();

    public ServiceTicket Ticket { get; set; } = null!;
    public List<SelectListItem> Assignees { get; set; } = new();
    /// <summary>Work orders raised for this ticket — the visit that answers the complaint.</summary>
    public List<WorkOrder> WorkOrders { get; set; } = new();
    public bool IsAdminOrPartner { get; set; }

    [TempData] public string? StatusMessage { get; set; }

    public class TicketEditInput
    {
        public int Id { get; set; }
        public string Subject { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public TicketPriority Priority { get; set; }
        public TicketStatus Status { get; set; }
        public string? AssignedToUserId { get; set; }
        public string? Resolution { get; set; }
        public string? Notes { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var ticket = await _context.ServiceTickets
            .Include(t => t.Installation).ThenInclude(i => i.Customer)
            .Include(t => t.ReportedByUser)
            .Include(t => t.AssignedToUser)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (ticket == null) return NotFound();

        // Access check
        var user = await _userManager.GetUserAsync(User);
        var roles = await _userManager.GetRolesAsync(user!);
        if (!roles.Contains(UserRoles.Admin))
        {
            if (roles.Contains(UserRoles.Partner))
            {
                if (user!.PartnerId == null || ticket.Installation.PartnerId != user.PartnerId) return Forbid();
            }
            else if (roles.Contains(UserRoles.Customer))
            {
                var customer = await _context.Customers.FirstOrDefaultAsync(c => c.UserId == user!.Id);
                if (customer == null || ticket.Installation.CustomerId != customer.Id) return Forbid();
            }
        }

        Ticket = ticket;
        IsAdminOrPartner = roles.Contains(UserRoles.Admin) || roles.Contains(UserRoles.Partner);
        WorkOrders = await _context.WorkOrders.AsNoTracking()
            .Include(w => w.AssignedToUser)
            .Where(w => w.ServiceTicketId == id)
            .OrderByDescending(w => w.ScheduledDate)
            .ToListAsync();
        Input = new TicketEditInput
        {
            Id = ticket.Id,
            Subject = ticket.Subject,
            Description = ticket.Description,
            Priority = ticket.Priority,
            Status = ticket.Status,
            AssignedToUserId = ticket.AssignedToUserId,
            Resolution = ticket.Resolution,
            Notes = ticket.Notes
        };

        await LoadAssigneesAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var ticket = await _context.ServiceTickets
            .Include(t => t.Installation)
            .FirstOrDefaultAsync(t => t.Id == Input.Id);
        if (ticket == null) return NotFound();

        // Access check (only Admin/Partner can fully edit; Customer can only add notes)
        var user = await _userManager.GetUserAsync(User);
        var roles = await _userManager.GetRolesAsync(user!);
        var isAdminOrPartner = roles.Contains(UserRoles.Admin) || roles.Contains(UserRoles.Partner);

        ticket.Subject = Input.Subject;
        ticket.Description = Input.Description;
        ticket.Priority = Input.Priority;
        ticket.Notes = Input.Notes;

        if (isAdminOrPartner)
        {
            ticket.Status = Input.Status;
            ticket.AssignedToUserId = string.IsNullOrEmpty(Input.AssignedToUserId) ? null : Input.AssignedToUserId;
            ticket.Resolution = Input.Resolution;

            if ((Input.Status == TicketStatus.Resolved || Input.Status == TicketStatus.Closed)
                && ticket.ResolvedAt == null)
            {
                ticket.ResolvedAt = DateTime.UtcNow;
            }
        }

        ticket.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        StatusMessage = "บันทึกแล้ว";
        return RedirectToPage(new { id = ticket.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (!await _userManager.IsInRoleAsync(user!, UserRoles.Admin)) return Forbid();

        var ticket = await _context.ServiceTickets.FindAsync(id);
        if (ticket == null) return NotFound();

        _context.ServiceTickets.Remove(ticket);
        await _context.SaveChangesAsync();
        StatusMessage = "ลบเรื่องแจ้งซ่อมแล้ว";
        return RedirectToPage("Index");
    }

    private async Task LoadAssigneesAsync()
    {
        var adminRoleId = await _context.Roles.Where(r => r.Name == UserRoles.Admin)
            .Select(r => r.Id).FirstOrDefaultAsync();
        var partnerRoleId = await _context.Roles.Where(r => r.Name == UserRoles.Partner)
            .Select(r => r.Id).FirstOrDefaultAsync();

        var allowedUserIds = await _context.UserRoles
            .Where(ur => ur.RoleId == adminRoleId || ur.RoleId == partnerRoleId)
            .Select(ur => ur.UserId).ToListAsync();

        var users = await _context.Users.Where(u => allowedUserIds.Contains(u.Id) && u.IsActive)
            .ToListAsync();

        Assignees = users.Select(u => new SelectListItem($"{u.FullName} ({u.Email})", u.Id)).ToList();
    }
}
