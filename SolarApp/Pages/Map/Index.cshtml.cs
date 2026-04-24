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
        int UnresolvedAlerts);

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

        var installs = await q
            .Select(i => new { i.Id, i.SiteName, i.SiteCode, i.Latitude, i.Longitude,
                                i.Status, i.InverterBrand, i.SystemCapacityKw,
                                UnresolvedAlerts = i.Alerts.Count(a => a.ResolvedAt == null) })
            .ToListAsync();

        TotalSites = installs.Count;

        Markers = installs
            .Where(i => i.Latitude.HasValue && i.Longitude.HasValue
                         && Math.Abs(i.Latitude.Value) > 0.0001
                         && Math.Abs(i.Longitude.Value) > 0.0001)
            .Select(i => new MapMarker(
                i.Id, i.SiteName, i.Latitude!.Value, i.Longitude!.Value,
                i.Status.ToString(), i.InverterBrand.ToString(), i.SystemCapacityKw,
                i.SiteCode, i.UnresolvedAlerts))
            .ToList();

        MappedSites = Markers.Count;
    }
}
