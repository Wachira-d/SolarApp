using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Pages.Maintenance;

[Authorize]
public class IndexModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;

    public IndexModel(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    public List<MaintenanceRecord> Records { get; set; } = new();

    /// <summary>Past their cleaning date AND on our cleaning contract — the actionable list.</summary>
    public List<Installation> CleaningDue { get; set; } = new();

    /// <summary>
    /// Past their cleaning date but flagged IncludesCleaning = false.
    ///
    /// This page used to lump these in with the rest, so it reported seven
    /// sites needing cleaning while LINE reported one. Neither number was
    /// wrong for its own definition and nothing on screen explained the gap.
    /// The alert job, the dashboard tile and the map all skip these sites,
    /// because raising a reminder for work we are not contracted to do sends a
    /// crew out unpaid. Listing them separately means the operator can tell
    /// which situation they are in: either the flag is right and these are the
    /// partner's problem, or it is wrong and they have just found six sites
    /// that have silently stopped being reminded.
    /// </summary>
    public List<Installation> CleaningOverdueNotContracted { get; set; } = new();

    public async Task OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        var roles = await _userManager.GetRolesAsync(user!);

        var installationQuery = _context.Installations.AsQueryable();

        if (roles.Contains(UserRoles.Customer))
        {
            var customer = await _context.Customers.FirstOrDefaultAsync(c => c.UserId == user!.Id);
            if (customer != null)
                installationQuery = installationQuery.Where(i => i.CustomerId == customer.Id);
            else
                installationQuery = installationQuery.Where(i => false);
        }
        else if (roles.Contains(UserRoles.Partner))
        {
            if (user!.PartnerId != null)
                installationQuery = installationQuery.Where(i => i.PartnerId == user.PartnerId);
            else
                installationQuery = installationQuery.Where(i => false);
        }

        var installationIds = await installationQuery.Select(i => i.Id).ToListAsync();

        Records = await _context.MaintenanceRecords
            .Include(m => m.Installation)
            .Include(m => m.PerformedByUser)
            .Where(m => installationIds.Contains(m.InstallationId))
            .OrderByDescending(m => m.ScheduledDate)
            .Take(50)
            .ToListAsync();

        // Same rule as InstallationService.GetInstallationsNeedingCleaningAsync,
        // SystemHealthMonitorJob.CheckMaintenanceDueAsync and the dashboard
        // tile: due date passed AND we are the ones who clean it.
        var overdue = await installationQuery
            .Include(i => i.Customer)
            .Where(i => i.NextCleaningDueDate != null && i.NextCleaningDueDate <= DateTime.UtcNow)
            .ToListAsync();

        // Most overdue first. The old list came back in whatever order the
        // database felt like, so a site eleven months late sat between two
        // that were a fortnight late.
        CleaningDue = overdue
            .Where(i => i.IncludesCleaning)
            .OrderBy(i => i.NextCleaningDueDate)
            .ToList();

        CleaningOverdueNotContracted = overdue
            .Where(i => !i.IncludesCleaning)
            .OrderBy(i => i.NextCleaningDueDate)
            .ToList();
    }
}
