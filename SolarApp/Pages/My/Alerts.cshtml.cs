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

        // Same filter as the menu badge (SystemAlert.CustomerVisible), so the
        // count there is the number of open rows here. Open and closed are
        // fetched separately: one "latest 50" list could push open alerts off
        // the page behind a run of recently closed ones.
        var baseQuery = _context.SystemAlerts
            .Where(SystemAlert.CustomerVisible)
            .Where(a => a.Installation.CustomerId == customerId);

        var raw = (await Project(baseQuery.Where(a => a.ResolvedAt == null)
                    .OrderByDescending(a => a.OccurredAt).Take(100))
                .ToListAsync())
            .Concat(await Project(baseQuery.Where(a => a.ResolvedAt != null)
                    .OrderByDescending(a => a.ResolvedAt).Take(20))
                .ToListAsync());

        var rows = raw.Select(a => new AlertRow(
                a.SiteName,
                a.InstallationId,
                SystemAlert.TitleFor(a.Title, a.Message),
                a.Severity.ToString().ToLowerInvariant(),
                a.OccurredAt,
                a.ResolvedAt))
            .ToList();

        Active = rows.Where(r => r.Resolved == null).ToList();
        Resolved = rows.Where(r => r.Resolved != null).ToList();
        return Page();
    }

    // Title comes back raw so SystemAlert.TitleFor can fill in for the vendor
    // alarms that were stored without one — EF can't translate it.
    private static IQueryable<AlertProjection> Project(IQueryable<SystemAlert> q) =>
        q.Select(a => new AlertProjection(
            a.Installation.SiteName,
            a.InstallationId,
            a.Title,
            a.Message,
            a.Severity,
            a.OccurredAt,
            a.ResolvedAt));

    private sealed record AlertProjection(string SiteName, int InstallationId, string Title,
        string Message, AlertSeverity Severity, DateTime OccurredAt, DateTime? ResolvedAt);
}
