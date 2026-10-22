using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Pages.Customers;

/// <summary>
/// Everything about one customer on one screen: contact details, the sites
/// they own, what those sites are doing right now, and the open work against
/// them.  Previously answering "what's going on with this customer" meant
/// three pages and manual filtering.
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

    public Customer Customer { get; private set; } = null!;
    public List<Installation> Sites { get; private set; } = new();
    public List<SystemAlert> OpenAlerts { get; private set; } = new();
    /// <summary>All open alerts — OpenAlerts holds only the first 20, and the header showed that as the total.</summary>
    public int OpenAlertTotal { get; private set; }
    public List<ServiceTicket> OpenTickets { get; private set; } = new();
    public int OpenTicketTotal { get; private set; }
    public bool IsAdmin { get; private set; }

    public decimal TotalCapacityKw { get; private set; }
    public decimal TodayKwh { get; private set; }
    public decimal Month30dKwh { get; private set; }
    public int OnlineSites { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Forbid();
        IsAdmin = User.IsInRole(UserRoles.Admin);

        var customer = await _context.Customers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id);
        if (customer == null) return NotFound();
        Customer = customer;

        var siteQuery = _context.Installations.AsNoTracking()
            .Include(i => i.Partner)
            .Where(i => i.CustomerId == id);

        if (!IsAdmin)
        {
            if (user.PartnerId == null) return Forbid();
            // A partner sees only the sites they service. If that leaves
            // nothing, this customer isn't theirs to look at.
            siteQuery = siteQuery.Where(i => i.PartnerId == user.PartnerId);
        }

        Sites = await siteQuery.OrderBy(i => i.SiteName).ToListAsync();
        if (!IsAdmin && Sites.Count == 0) return Forbid();

        var siteIds = Sites.Select(s => s.Id).ToList();

        OpenAlertTotal = await _context.SystemAlerts.AsNoTracking()
            .CountAsync(a => siteIds.Contains(a.InstallationId) && a.ResolvedAt == null);
        OpenAlerts = await _context.SystemAlerts.AsNoTracking()
            .Include(a => a.Installation)
            .Where(a => siteIds.Contains(a.InstallationId) && a.ResolvedAt == null)
            // Critical = 1, so ascending puts the worst first.
            .OrderBy(a => a.Severity)
            .ThenByDescending(a => a.OccurredAt)
            .Take(20)
            .ToListAsync();

        OpenTicketTotal = await _context.ServiceTickets.AsNoTracking()
            .CountAsync(t => siteIds.Contains(t.InstallationId)
                        && t.Status != TicketStatus.Closed
                        && t.Status != TicketStatus.Resolved);
        OpenTickets = await _context.ServiceTickets.AsNoTracking()
            .Include(t => t.Installation)
            .Where(t => siteIds.Contains(t.InstallationId)
                        && t.Status != TicketStatus.Closed
                        && t.Status != TicketStatus.Resolved)
            .OrderByDescending(t => t.CreatedAt)
            .Take(20)
            .ToListAsync();

        // Site-local today — UtcNow.Date reads the wrong daily row during
        // 00:00–07:00 Thailand time.
        var today = SolarApp.Services.SiteTime.Today;
        var from30d = today.AddDays(-30);

        TodayKwh = await _context.EnergyData.AsNoTracking()
            .Where(e => siteIds.Contains(e.InstallationId)
                        && e.Granularity == DataGranularity.Daily
                        && e.Timestamp.Date == today)
            .SumAsync(e => (decimal?)e.EnergyGenerationKwh) ?? 0;

        Month30dKwh = await _context.EnergyData.AsNoTracking()
            .Where(e => siteIds.Contains(e.InstallationId)
                        && e.Granularity == DataGranularity.Daily
                        && e.Timestamp >= from30d && e.Timestamp <= today)
            .SumAsync(e => (decimal?)e.EnergyGenerationKwh) ?? 0;

        TotalCapacityKw = Sites.Sum(s => s.SystemCapacityKw);
        OnlineSites = Sites.Count(s => s.Status == InstallationStatus.Active);
        return Page();
    }
}
