using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
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

    public DetailModel(
        IInstallationService installationService,
        IMaintenanceService maintenanceService,
        UserManager<ApplicationUser> userManager)
    {
        _installationService = installationService;
        _maintenanceService = maintenanceService;
        _userManager = userManager;
    }

    public Installation Installation { get; set; } = null!;
    public List<MaintenanceRecord> MaintenanceRecords { get; set; } = new();
    public List<ServiceTicket> ServiceTickets { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var installation = await _installationService.GetByIdAsync(id);
        if (installation == null) return NotFound();

        // Check access permissions
        var user = await _userManager.GetUserAsync(User);
        var roles = await _userManager.GetRolesAsync(user!);

        if (!roles.Contains(UserRoles.Admin))
        {
            if (roles.Contains(UserRoles.Partner) && installation.PartnerId != user!.Id)
                return Forbid();
            if (roles.Contains(UserRoles.Customer) && installation.CustomerId != user!.Id)
                return Forbid();
        }

        Installation = installation;
        MaintenanceRecords = await _maintenanceService.GetByInstallationAsync(id);
        ServiceTickets = await _maintenanceService.GetTicketsByInstallationAsync(id);

        return Page();
    }
}
