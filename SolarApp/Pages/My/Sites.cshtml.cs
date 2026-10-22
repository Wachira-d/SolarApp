using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Pages.My;

/// <summary>
/// Sites list — single tab that always shows the per-site cards even
/// when the customer only has one site (deep-link target from the
/// bottom-nav so the tab is meaningful regardless of customer size).
/// </summary>
[Authorize]
public class SitesModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;

    public SitesModel(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    public List<IndexModel.SiteCardVm> Sites { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Forbid();

        var customer = await _context.Customers
            .Include(c => c.Installations)
            .FirstOrDefaultAsync(c => c.UserId == user.Id);
        if (customer == null || customer.Installations.Count == 0) return Page();

        var instIds = customer.Installations.Select(i => i.Id).ToList();
        var today = SolarApp.Services.SiteTime.Today;

        var todayPer = await _context.EnergyData
            .Where(e => instIds.Contains(e.InstallationId)
                && e.Granularity == DataGranularity.Daily
                && e.Timestamp.Date == today)
            .GroupBy(e => e.InstallationId)
            .Select(g => new { InstId = g.Key, Sum = g.Sum(e => (decimal?)e.EnergyGenerationKwh) ?? 0 })
            .ToDictionaryAsync(x => x.InstId, x => x.Sum);

        var rtPer = await _context.EnergyData
            .Where(e => instIds.Contains(e.InstallationId) && e.Granularity == DataGranularity.Realtime)
            .GroupBy(e => e.InstallationId)
            .Select(g => new { InstId = g.Key, Latest = g.OrderByDescending(x => x.Timestamp).FirstOrDefault() })
            .ToDictionaryAsync(x => x.InstId, x => x.Latest);

        var alertsPer = await _context.SystemAlerts
            .Where(SystemAlert.CustomerVisible)   // same set as the alerts page
            .Where(a => instIds.Contains(a.InstallationId) && a.ResolvedAt == null)
            .GroupBy(a => a.InstallationId)
            .Select(g => new { InstId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.InstId, x => x.Count);

        foreach (var inst in customer.Installations.OrderBy(i => i.SiteName))
        {
            todayPer.TryGetValue(inst.Id, out var today_);
            rtPer.TryGetValue(inst.Id, out var rt);
            alertsPer.TryGetValue(inst.Id, out var alerts);
            var (css, label) = inst.Status switch
            {
                InstallationStatus.Active      => ("online",      "ทำงานปกติ"),
                InstallationStatus.Maintenance => ("maintenance", "ตรวจสอบ"),
                InstallationStatus.Offline     => ("offline",     "ขาดการเชื่อมต่อ"),
                _                              => ("offline",     inst.Status.ToString()),
            };
            Sites.Add(new IndexModel.SiteCardVm
            {
                Id = inst.Id,
                Name = inst.SiteName,
                Brand = inst.InverterBrand.ToString(),
                CapacityKw = inst.SystemCapacityKw,
                StatusCss = css,
                StatusLabel = label,
                CurrentPowerKw = Math.Round(rt?.PowerGenerationKw ?? 0, 2),
                TodayKwh = Math.Round(today_, 1),
                OpenAlerts = alerts,
            });
        }
        return Page();
    }
}
