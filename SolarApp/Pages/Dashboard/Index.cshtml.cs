using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
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

    [BindProperty(SupportsGet = true)]
    public int? PartnerId { get; set; }

    /// <summary>Project-type preset filter (ServiceLevel enum value).</summary>
    [BindProperty(SupportsGet = true)]
    public ServiceLevel? ServiceLevel { get; set; }

    /// <summary>
    /// Monitoring filter from the dropdown.  "monitored" / "unmonitored" /
    /// "" (all).  Parsed into a bool? for the service.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public string? Monitoring { get; set; }

    [BindProperty(SupportsGet = true)]
    public InverterBrand? Brand { get; set; }

    public async Task OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        var roles = await _userManager.GetRolesAsync(user!);
        var role = roles.FirstOrDefault() ?? UserRoles.Customer;

        bool? monitoredOnly = Monitoring switch
        {
            "monitored" => true,
            "unmonitored" => false,
            _ => null,
        };

        Dashboard = await _dashboardService.GetDashboardAsync(
            user!.Id, role, PartnerId, ServiceLevel, monitoredOnly, Brand);
    }
}
