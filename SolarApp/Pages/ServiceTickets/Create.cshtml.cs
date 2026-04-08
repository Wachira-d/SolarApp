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
using SolarApp.Services.Interfaces;

namespace SolarApp.Pages.ServiceTickets;

[Authorize]
public class CreateModel : PageModel
{
    private readonly IMaintenanceService _maintenanceService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ApplicationDbContext _context;

    public CreateModel(
        IMaintenanceService maintenanceService,
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext context)
    {
        _maintenanceService = maintenanceService;
        _userManager = userManager;
        _context = context;
    }

    [BindProperty]
    public int InstallationId { get; set; }

    [BindProperty]
    [Required]
    public string Subject { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    public string Description { get; set; } = string.Empty;

    [BindProperty]
    public TicketPriority Priority { get; set; } = TicketPriority.Normal;

    public List<SelectListItem> Installations { get; set; } = new();

    public async Task OnGetAsync()
    {
        await LoadInstallationsAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadInstallationsAsync();
            return Page();
        }

        var user = await _userManager.GetUserAsync(User);

        var ticket = new ServiceTicket
        {
            InstallationId = InstallationId,
            Subject = Subject,
            Description = Description,
            Priority = Priority,
            Status = TicketStatus.Open,
            ReportedByUserId = user!.Id
        };

        await _maintenanceService.CreateTicketAsync(ticket);
        return RedirectToPage("Index");
    }

    private async Task LoadInstallationsAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        var roles = await _userManager.GetRolesAsync(user!);

        var query = _context.Installations.AsQueryable();

        if (roles.Contains(UserRoles.Customer))
        {
            var customer = await _context.Customers.FirstOrDefaultAsync(c => c.UserId == user!.Id);
            if (customer != null)
                query = query.Where(i => i.CustomerId == customer.Id);
            else
                query = query.Where(i => false);
        }
        else if (roles.Contains(UserRoles.Partner))
        {
            if (user!.PartnerId != null)
                query = query.Where(i => i.PartnerId == user.PartnerId);
            else
                query = query.Where(i => false);
        }

        Installations = await query
            .Select(i => new SelectListItem($"{i.SiteName} ({i.SiteCode})", i.Id.ToString()))
            .ToListAsync();
    }
}
