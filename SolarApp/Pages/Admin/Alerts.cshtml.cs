using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SolarApp.Models;
using SolarApp.Services.Interfaces;

namespace SolarApp.Pages.Admin;

[Authorize(Roles = "Admin")]
public class AlertsModel : PageModel
{
    private readonly IAlertService _alertService;
    private readonly UserManager<ApplicationUser> _userManager;

    public AlertsModel(IAlertService alertService, UserManager<ApplicationUser> userManager)
    {
        _alertService = alertService;
        _userManager = userManager;
    }

    public List<SystemAlert> ActiveAlerts { get; set; } = new();

    public async Task OnGetAsync()
    {
        ActiveAlerts = await _alertService.GetActiveAlertsAsync();
    }

    public async Task<IActionResult> OnPostAcknowledgeAsync(int alertId)
    {
        var user = await _userManager.GetUserAsync(User);
        await _alertService.AcknowledgeAlertAsync(alertId, user!.Id);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResolveAsync(int alertId)
    {
        await _alertService.ResolveAlertAsync(alertId);
        return RedirectToPage();
    }
}
