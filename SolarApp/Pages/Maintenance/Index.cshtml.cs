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
    public List<Installation> CleaningDue { get; set; } = new();

    public async Task OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        var roles = await _userManager.GetRolesAsync(user!);

        var installationQuery = _context.Installations.AsQueryable();
        if (roles.Contains(UserRoles.Customer))
            installationQuery = installationQuery.Where(i => i.CustomerId == user!.Id);
        else if (roles.Contains(UserRoles.Partner))
            installationQuery = installationQuery.Where(i => i.PartnerId == user!.Id);

        var installationIds = await installationQuery.Select(i => i.Id).ToListAsync();

        Records = await _context.MaintenanceRecords
            .Include(m => m.Installation)
            .Include(m => m.PerformedByUser)
            .Where(m => installationIds.Contains(m.InstallationId))
            .OrderByDescending(m => m.ScheduledDate)
            .Take(50)
            .ToListAsync();

        CleaningDue = await installationQuery
            .Where(i => i.NextCleaningDueDate != null && i.NextCleaningDueDate <= DateTime.UtcNow)
            .ToListAsync();
    }
}
