using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Pages.My;

[Authorize]
public class AlertsModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;

    public AlertsModel(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    public List<AlertRow> Active { get; private set; } = new();
    public List<AlertRow> Resolved { get; private set; } = new();

    public record AlertRow(string SiteName, int SiteId, string Title, string Severity,
        DateTime When, DateTime? Resolved);

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Forbid();

        var customerId = await _context.Customers
            .Where(c => c.UserId == user.Id)
            .Select(c => (int?)c.Id)
            .FirstOrDefaultAsync();
        if (customerId == null) return Page();

        // Filter to customer-relevant alert types — same set used by
        // CustomerAlertMessageBuilder so what the customer sees here
        // matches what hits their LINE channel.
        var raw = await _context.SystemAlerts
            .Where(a => a.Installation.CustomerId == customerId
                && (a.AlertType == AlertType.Offline
                    || a.AlertType == AlertType.Error
                    || a.AlertType == AlertType.LowPerformance
                    || a.AlertType == AlertType.MaintenanceDue
                    || a.AlertType == AlertType.WarrantyExpiring))
            .OrderByDescending(a => a.OccurredAt)
            .Take(50)
            // Title comes back raw so SystemAlert.TitleFor can fill in for the
            // vendor alarms that were stored without one — EF can't translate it.
            .Select(a => new
            {
                a.Installation.SiteName,
                a.InstallationId,
                a.Title,
                a.Message,
                a.Severity,
                a.OccurredAt,
                a.ResolvedAt
            })
            .ToListAsync();

        var rows = raw.Select(a => new AlertRow(
                a.SiteName,
                a.InstallationId,
                SystemAlert.TitleFor(a.Title, a.Message),
                a.Severity.ToString().ToLowerInvariant(),
                a.OccurredAt,
                a.ResolvedAt))
            .ToList();

        Active = rows.Where(r => r.Resolved == null).ToList();
        Resolved = rows.Where(r => r.Resolved != null).Take(20).ToList();
        return Page();
    }
}
