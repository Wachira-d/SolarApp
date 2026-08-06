using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Pages.Partners;

/// <summary>
/// One partner's fleet: contract terms, the users who can log in on their
/// behalf, and every site they service with its current state.
/// A partner may open their own record; admins may open any.
/// </summary>
[Authorize(Roles = "Admin,Partner")]
public class DetailModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;

    public DetailModel(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    public Partner Partner { get; private set; } = null!;
    public List<Installation> Sites { get; private set; } = new();
    public List<ApplicationUser> Users { get; private set; } = new();
    public List<SystemAlert> OpenAlerts { get; private set; } = new();
    public bool IsAdmin { get; private set; }

    public decimal TotalCapacityKw { get; private set; }
    public decimal TodayKwh { get; private set; }
    public int OnlineSites { get; private set; }
    public int CustomerCount { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Forbid();
        IsAdmin = User.IsInRole(UserRoles.Admin);

        // A partner user can only open their own record — otherwise the page
        // would leak a competitor's customer list and contract terms.
        if (!IsAdmin && user.PartnerId != id) return Forbid();

        var partner = await _context.Partners.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (partner == null) return NotFound();
        Partner = partner;

        Sites = await _context.Installations.AsNoTracking()
            .Include(i => i.Customer)
            .Where(i => i.PartnerId == id)
            .OrderBy(i => i.SiteName)
            .ToListAsync();

        Users = await _context.Users.AsNoTracking()
            .Where(u => u.PartnerId == id)
            .OrderBy(u => u.FullName)
            .ToListAsync();

        var siteIds = Sites.Select(s => s.Id).ToList();

        OpenAlerts = await _context.SystemAlerts.AsNoTracking()
            .Include(a => a.Installation)
            .Where(a => siteIds.Contains(a.InstallationId) && a.ResolvedAt == null)
            // Critical = 1, so ascending puts the worst first.
            .OrderBy(a => a.Severity)
            .ThenByDescending(a => a.OccurredAt)
            .Take(20)
            .ToListAsync();

        var today = SolarApp.Services.SiteTime.Today;
        TodayKwh = await _context.EnergyData.AsNoTracking()
            .Where(e => siteIds.Contains(e.InstallationId)
                        && e.Granularity == DataGranularity.Daily
                        && e.Timestamp.Date == today)
            .SumAsync(e => (decimal?)e.EnergyGenerationKwh) ?? 0;

        TotalCapacityKw = Sites.Sum(s => s.SystemCapacityKw);
        OnlineSites = Sites.Count(s => s.Status == InstallationStatus.Active);
        CustomerCount = Sites.Select(s => s.CustomerId).Distinct().Count();
        return Page();
    }
}
