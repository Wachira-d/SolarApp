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

    /// <summary>
    /// Sites whose cleaning is due, by the shared rule (Installation.CleaningDueAsOf)
    /// — the same one the reminder job, LINE summary, dashboard and map use.
    /// </summary>
    public List<Installation> CleaningDue { get; set; } = new();

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

        // Most overdue first. This used to list every site past its date
        // whether or not we clean it, then a later version split the
        // switched-off ones into a box of their own — which meant turning
        // cleaning off moved a site from one "overdue" list to another rather
        // than making it go away. Switching cleaning off now clears the due
        // date, so there is only one list and it means one thing.
        CleaningDue = await installationQuery
            .Include(i => i.Customer)
            .Where(Installation.CleaningDueAsOf(SolarApp.Services.SiteTime.Today))
            .OrderBy(i => i.NextCleaningDueDate)
            .ToListAsync();
    }
}
