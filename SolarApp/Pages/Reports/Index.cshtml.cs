using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.Authorization;
using SolarApp.Services.Interfaces;
using SolarApp.ViewModels;

namespace SolarApp.Pages.Reports;

[Authorize]
public class IndexModel : PageModel
{
    private readonly IInstallationService _installationService;
    private readonly IDashboardService _dashboardService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IInstallationAccessHelper _access;

    public IndexModel(
        IInstallationService installationService,
        IDashboardService dashboardService,
        UserManager<ApplicationUser> userManager,
        IInstallationAccessHelper access)
    {
        _installationService = installationService;
        _dashboardService = dashboardService;
        _userManager = userManager;
        _access = access;
    }

    [BindProperty(SupportsGet = true)]
    public int? InstallationId { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTime? FromDate { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTime? ToDate { get; set; }

    public List<SelectListItem> InstallationOptions { get; set; } = new();
    public ProductionReportViewModel? Report { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadInstallationsAsync();

        FromDate ??= DateTime.UtcNow.AddMonths(-1);
        ToDate ??= DateTime.UtcNow;

        if (InstallationId.HasValue)
        {
            // Authorization: user can only view reports for installations they have access to
            if (!await _access.CanAccessAsync(InstallationId.Value, User))
                return Forbid();

            Report = await _dashboardService.GetProductionReportAsync(
                InstallationId.Value, FromDate.Value, ToDate.Value);
        }
        return Page();
    }

    private async Task LoadInstallationsAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        var roles = await _userManager.GetRolesAsync(user!);

        List<Installation> installations;
        if (roles.Contains(UserRoles.Admin))
            installations = await _installationService.GetAllAsync();
        else if (roles.Contains(UserRoles.Partner))
            installations = await _installationService.GetByPartnerIdAsync(user!.Id);
        else
            installations = await _installationService.GetByCustomerIdAsync(user!.Id);

        InstallationOptions = installations.Select(i => new SelectListItem(
            $"{i.SiteName} ({i.SystemCapacityKw} kWp)", i.Id.ToString())).ToList();
    }
}
