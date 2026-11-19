using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Services;
using SolarApp.Services.Analytics;
using SolarApp.Services.Authorization;

namespace SolarApp.Pages.Installations;

/// <summary>
/// Printable monthly report for one site (DESIGN.md §11 phase 2, "PDF report"):
/// a print-optimised A4 page the browser saves as PDF. Staff route; the owner
/// sees the same sheet at /My/Report/{id}. Access as the Analytics page.
/// </summary>
[Authorize(Roles = "Admin,Partner")]
public class ReportModel : PageModel
{
    private readonly ApplicationDbContext _db;
    private readonly IInstallationAccessHelper _access;
    private readonly SiteAnalyticsService _analytics;

    public ReportModel(ApplicationDbContext db, IInstallationAccessHelper access, SiteAnalyticsService analytics)
    {
        _db = db;
        _access = access;
        _analytics = analytics;
    }

    public MonthlySiteReport Report { get; private set; } = null!;

    /// <summary>Site-local today; settable so tests can pin the default month.</summary>
    public DateTime Today { get; set; } = SiteTime.Today;

    public async Task<IActionResult> OnGetAsync(int id, string? month = null)
    {
        if (!await _access.CanAccessAsync(id, User)) return Forbid();
        var site = await _db.Installations.AsNoTracking().Include(i => i.Customer).FirstOrDefaultAsync(i => i.Id == id);
        if (site == null) return NotFound();
        Report = await MonthlyReportBuilder.BuildAsync(_db, _analytics, site, month, Today, HttpContext.RequestAborted);
        return Page();
    }
}
