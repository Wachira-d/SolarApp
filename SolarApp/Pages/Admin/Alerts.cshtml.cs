using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Services.Authorization;
using SolarApp.Services.Interfaces;

namespace SolarApp.Pages.Admin;

[Authorize(Roles = "Admin,Partner")]
public class AlertsModel : PageModel
{
    private readonly IAlertService _alertService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ApplicationDbContext _context;
    private readonly IInstallationAccessHelper _access;

    public AlertsModel(IAlertService alertService, UserManager<ApplicationUser> userManager,
        ApplicationDbContext context, IInstallationAccessHelper access)
    {
        _alertService = alertService;
        _userManager = userManager;
        _context = context;
        _access = access;
    }

    public List<SystemAlert> ActiveAlerts { get; set; } = new();

    public async Task OnGetAsync()
    {
        ActiveAlerts = await _alertService.GetActiveAlertsAsync();
    }

    public async Task<IActionResult> OnPostAcknowledgeAsync(int alertId)
    {
        // Authorization: verify the alert's installation is accessible to this user
        var alert = await _context.SystemAlerts
            .Select(a => new { a.Id, a.InstallationId })
            .FirstOrDefaultAsync(a => a.Id == alertId);
        if (alert == null) return NotFound();
        if (!await _access.CanAccessAsync(alert.InstallationId, User)) return Forbid();

        var user = await _userManager.GetUserAsync(User);
        await _alertService.AcknowledgeAlertAsync(alertId, user!.Id);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResolveAsync(int alertId)
    {
        var alert = await _context.SystemAlerts
            .Select(a => new { a.Id, a.InstallationId })
            .FirstOrDefaultAsync(a => a.Id == alertId);
        if (alert == null) return NotFound();
        if (!await _access.CanAccessAsync(alert.InstallationId, User)) return Forbid();

        await _alertService.ResolveAlertAsync(alertId);
        return RedirectToPage();
    }
}
