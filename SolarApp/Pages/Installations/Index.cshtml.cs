using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.Interfaces;

namespace SolarApp.Pages.Installations;

[Authorize]
public class IndexModel : PageModel
{
    private readonly IInstallationService _installationService;
    private readonly UserManager<ApplicationUser> _userManager;

    public IndexModel(IInstallationService installationService, UserManager<ApplicationUser> userManager)
    {
        _installationService = installationService;
        _userManager = userManager;
    }

    public List<Installation> Installations { get; set; } = new();

    public async Task OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        var roles = await _userManager.GetRolesAsync(user!);

        if (roles.Contains(UserRoles.Admin))
            Installations = await _installationService.GetAllAsync();
        else if (roles.Contains(UserRoles.Partner))
            Installations = await _installationService.GetByPartnerIdAsync(user!.Id);
        else
            Installations = await _installationService.GetByCustomerIdAsync(user!.Id);
    }
}
