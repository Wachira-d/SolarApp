using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Services;
using SolarApp.Services.Analytics;

namespace SolarApp.Pages.My;

/// <summary>
/// The owner's printable monthly report — the same sheet staff see at
/// /Installations/Report/{id}. Authorisation as /My/Analytics: the signed-in
/// user's linked Customer must own the site, otherwise 404.
/// </summary>
[Authorize]
public class ReportModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SiteAnalyticsService _analytics;

    public ReportModel(ApplicationDbContext context, UserManager<ApplicationUser> userManager, SiteAnalyticsService analytics)
    {
        _context = context;
        _userManager = userManager;
        _analytics = analytics;
    }

    public MonthlySiteReport Report { get; private set; } = null!;

    /// <summary>Site-local today; settable so tests can pin the default month.</summary>
    public DateTime Today { get; set; } = SiteTime.Today;

    public async Task<IActionResult> OnGetAsync(int id, string? month = null)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Forbid();

        var customer = await _context.Customers
            .Where(c => c.UserId == user.Id)
            .Select(c => new { c.Id })
            .FirstOrDefaultAsync();
        if (customer == null) return NotFound();

        var site = await _context.Installations.AsNoTracking().Include(i => i.Customer)
            .FirstOrDefaultAsync(i => i.Id == id && i.CustomerId == customer.Id);
        if (site == null) return NotFound();

        Report = await MonthlyReportBuilder.BuildAsync(_context, _analytics, site, month, Today, HttpContext.RequestAborted);
        return Page();
    }
}
