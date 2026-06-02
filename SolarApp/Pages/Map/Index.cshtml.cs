using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Pages.Map;

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

    public record MapMarker(
        int Id,
        string Name,
        double Lat,
        double Lng,
        string Status,
        string? Brand,
        decimal CapacityKw,
        string? SiteCode,
        int UnresolvedAlerts,
        bool NeedsMaintenance,
        int OpenMaintenanceCount,
        int OpenWorkOrderCount,
        bool CleaningOverdue,
        // ISO-formatted yyyy-MM-dd of the next scheduled cleaning, or null
        // when no cleaning is pending.  Surfaces in the popup so the
        // operator planning a route can see "this site is due in 18 days"
        // without leaving the map.
        string? NextCleaningDueDate,
        // Days from today until the next cleaning.  Negative = overdue.
        // Null when no cleaning is pending.  Drives the gradient colour:
        // < 0 → red, 0..7 → orange, 8..30 → yellow, > 30 → green.
        int? DaysToNextCleaning,
        // Next pending PM visit (date string + days).  Same gradient
        // rules so operators can also see "PM due in 12 days" while
        // driving past, even if the cleaning is months away.
        string? NextPmDueDate,
        int? DaysToNextPm);

    public List<MapMarker> Markers { get; set; } = new();
    public int TotalSites { get; set; }
    public int MappedSites { get; set; }

    public async Task OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        var roles = user != null ? await _userManager.GetRolesAsync(user) : new List<string>();

        var q = _context.Installations.AsQueryable();

        if (!roles.Contains(UserRoles.Admin))
        {
            if (roles.Contains(UserRoles.Partner))
                q = q.Where(i => user!.PartnerId != null && i.PartnerId == user.PartnerId);
            else if (roles.Contains(UserRoles.Customer))
            {
                var cust = await _context.Customers
                    .Where(c => c.UserId == user!.Id).Select(c => c.Id).FirstOrDefaultAsync();
                q = q.Where(i => i.CustomerId == cust);
            }
            else
                q = q.Where(i => false);
        }

        var now = DateTime.UtcNow;
        var today = now.Date;

        // WorkOrders may not exist on first deploy — guard the query
        var workOrdersTableExists = true;
        try
        {
            #pragma warning disable EF1002
            await _context.Database.ExecuteSqlRawAsync("SELECT TOP 0 * FROM [WorkOrders]");
            #pragma warning restore EF1002
        }
        catch
        {
            workOrdersTableExists = false;
        }

        var openWoStatuses = new[]
        {
            WorkOrderStatus.Scheduled,
            WorkOrderStatus.Assigned,
            WorkOrderStatus.Accepted,
            WorkOrderStatus.InProgress,
        };

        var installs = await q
            .Select(i => new
            {
                i.Id,
                i.SiteName,
                i.SiteCode,
                i.Latitude,
                i.Longitude,
                i.Status,
                i.InverterBrand,
                i.SystemCapacityKw,
                i.NextCleaningDueDate,
                // Earliest scheduled PM record that's still pending — i.e. the
                // next visit the field crew has on the books for this site.
                NextPmDate = i.MaintenanceRecords
                    .Where(m => m.Status == MaintenanceStatus.Scheduled
                                && m.ScheduledDate >= now)
                    .OrderBy(m => m.ScheduledDate)
                    .Select(m => (DateTime?)m.ScheduledDate)
                    .FirstOrDefault(),
                UnresolvedAlerts = i.Alerts.Count(a => a.ResolvedAt == null),
                OpenMaintenance = i.MaintenanceRecords.Count(m =>
                    m.Status == MaintenanceStatus.Scheduled
                    || m.Status == MaintenanceStatus.InProgress),
                OpenWorkOrders = workOrdersTableExists
                    ? _context.WorkOrders.Count(w =>
                        w.InstallationId == i.Id
                        && openWoStatuses.Contains(w.Status))
                    : 0,
            })
            .ToListAsync();

        TotalSites = installs.Count;

        Markers = installs
            .Where(i => i.Latitude.HasValue && i.Longitude.HasValue
                         && Math.Abs(i.Latitude.Value) > 0.0001
                         && Math.Abs(i.Longitude.Value) > 0.0001)
            .Select(i =>
            {
                var cleaningOverdue = i.NextCleaningDueDate.HasValue
                    && i.NextCleaningDueDate.Value <= now;
                var needsMa = i.Status == InstallationStatus.Maintenance
                    || cleaningOverdue
                    || i.OpenMaintenance > 0
                    || i.OpenWorkOrders > 0;

                // Effective status string used by the front-end colour mapper.
                // Offline / Inactive take precedence; otherwise, if MA is needed
                // we report "Maintenance" so the pin turns yellow.
                var effectiveStatus = i.Status switch
                {
                    InstallationStatus.Offline => "Offline",
                    InstallationStatus.Inactive => "Inactive",
                    _ when needsMa => "Maintenance",
                    _ => "Active",
                };

                int? cleaningDays = i.NextCleaningDueDate.HasValue
                    ? (int)Math.Floor((i.NextCleaningDueDate.Value.Date - today).TotalDays)
                    : (int?)null;
                int? pmDays = i.NextPmDate.HasValue
                    ? (int)Math.Floor((i.NextPmDate.Value.Date - today).TotalDays)
                    : (int?)null;

                return new MapMarker(
                    i.Id, i.SiteName, i.Latitude!.Value, i.Longitude!.Value,
                    effectiveStatus, i.InverterBrand.ToString(), i.SystemCapacityKw,
                    i.SiteCode, i.UnresolvedAlerts, needsMa,
                    i.OpenMaintenance, i.OpenWorkOrders, cleaningOverdue,
                    i.NextCleaningDueDate?.ToString("yyyy-MM-dd"),
                    cleaningDays,
                    i.NextPmDate?.ToString("yyyy-MM-dd"),
                    pmDays);
            })
            .ToList();

        MappedSites = Markers.Count;
    }
}
