using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.Interfaces;

namespace SolarApp.Pages.Installations;

[Authorize]
public class DetailModel : PageModel
{
    private readonly IInstallationService _installationService;
    private readonly IMaintenanceService _maintenanceService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ApplicationDbContext _context;

    public DetailModel(
        IInstallationService installationService,
        IMaintenanceService maintenanceService,
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext context)
    {
        _installationService = installationService;
        _maintenanceService = maintenanceService;
        _userManager = userManager;
        _context = context;
    }

    public Installation Installation { get; set; } = null!;
    public List<MaintenanceRecord> MaintenanceRecords { get; set; } = new();
    public List<ServiceTicket> ServiceTickets { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var installation = await _installationService.GetByIdAsync(id);
        if (installation == null) return NotFound();

        var user = await _userManager.GetUserAsync(User);
        var roles = await _userManager.GetRolesAsync(user!);

        if (!roles.Contains(UserRoles.Admin))
        {
            if (roles.Contains(UserRoles.Partner))
            {
                if (user!.PartnerId == null || installation.PartnerId != user.PartnerId)
                    return Forbid();
            }
            else if (roles.Contains(UserRoles.Customer))
            {
                var customer = await _context.Customers.FirstOrDefaultAsync(c => c.UserId == user!.Id);
                if (customer == null || installation.CustomerId != customer.Id)
                    return Forbid();
            }
        }

        Installation = installation;
        MaintenanceRecords = await _maintenanceService.GetByInstallationAsync(id);
        ServiceTickets = await _maintenanceService.GetTicketsByInstallationAsync(id);

        return Page();
    }
}
