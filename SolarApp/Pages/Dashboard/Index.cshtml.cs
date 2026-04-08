using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.Interfaces;
using SolarApp.ViewModels;

namespace SolarApp.Pages.Dashboard;

[Authorize]
public class IndexModel : PageModel
{
    private readonly IDashboardService _dashboardService;
    private readonly UserManager<ApplicationUser> _userManager;

    public IndexModel(IDashboardService dashboardService, UserManager<ApplicationUser> userManager)
    {
        _dashboardService = dashboardService;
        _userManager = userManager;
    }

    public DashboardViewModel Dashboard { get; set; } = new();

    public async Task OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        var roles = await _userManager.GetRolesAsync(user!);
        var role = roles.FirstOrDefault() ?? UserRoles.Customer;

        Dashboard = await _dashboardService.GetDashboardAsync(user!.Id, role);
    }
}
