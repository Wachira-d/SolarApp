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
/// Customer portal entry — combined overview for multi-site customers
/// + hero summary for single-site customers.
///
/// Data philosophy: only what a customer cares about (production,
/// savings, status, alerts) — never operator-only diagnostic noise.
/// Heavy lifting (per-day series, recent alarms) is loaded via the
/// MyApi controller so the page renders fast and refreshes live.
/// </summary>
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

    public Customer? Customer { get; private set; }
    public List<SiteCardVm> Sites { get; private set; } = new();
    public OverviewVm Overview { get; private set; } = new();

    public class SiteCardVm
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Brand { get; set; } = "";
        public decimal CapacityKw { get; set; }
        public string StatusCss { get; set; } = "offline";
        public string StatusLabel { get; set; } = "";
        public decimal CurrentPowerKw { get; set; }
        public decimal TodayKwh { get; set; }
        public int OpenAlerts { get; set; }
    }
    public class OverviewVm
    {
        public decimal TotalCurrentPowerKw { get; set; }
        public decimal TotalTodayKwh { get; set; }
        public decimal TotalMonthKwh { get; set; }
        public decimal TotalLifetimeKwh { get; set; }
        public decimal EstimatedSavingsThbMonth { get; set; }
        public decimal Co2SavedKgMonth { get; set; }
        public int TreesEquivalentMonth { get; set; }
        public int OnlineSites { get; set; }
        public int TotalSites { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Forbid();

        Customer = await _context.Customers
            .Include(c => c.Installations)
            .FirstOrDefaultAsync(c => c.UserId == user.Id);

        if (Customer == null) return Page();

        var instIds = Customer.Installations.Select(i => i.Id).ToList();
        if (instIds.Count == 0) return Page();

        var today = SolarApp.Services.SiteTime.Today;
        var monthStart = today.AddDays(-30);

        // Per-installation aggregates in one query each — keep the page snappy
        var todayPerInstall = await _context.EnergyData
            .Where(e => instIds.Contains(e.InstallationId)
                && e.Granularity == DataGranularity.Daily
                && e.Timestamp.Date == today)
            .GroupBy(e => e.InstallationId)
            .Select(g => new { InstId = g.Key, Sum = g.Sum(e => (decimal?)e.EnergyGenerationKwh) ?? 0 })
            .ToDictionaryAsync(x => x.InstId, x => x.Sum);

        var monthPerInstall = await _context.EnergyData
            .Where(e => instIds.Contains(e.InstallationId)
                && e.Granularity == DataGranularity.Daily
                && e.Timestamp >= monthStart && e.Timestamp <= today)
            .GroupBy(e => e.InstallationId)
            .Select(g => new { InstId = g.Key, Sum = g.Sum(e => (decimal?)e.EnergyGenerationKwh) ?? 0 })
            .ToDictionaryAsync(x => x.InstId, x => x.Sum);

        var lifetimePerInstall = await _context.EnergyData
            .Where(e => instIds.Contains(e.InstallationId)
                && e.Granularity == DataGranularity.Daily)
            .GroupBy(e => e.InstallationId)
            .Select(g => new { InstId = g.Key, Sum = g.Sum(e => (decimal?)e.EnergyGenerationKwh) ?? 0 })
            .ToDictionaryAsync(x => x.InstId, x => x.Sum);

        var realtimePerInstall = await _context.EnergyData
            .Where(e => instIds.Contains(e.InstallationId)
                && e.Granularity == DataGranularity.Realtime)
            .GroupBy(e => e.InstallationId)
            .Select(g => new
            {
                InstId = g.Key,
                Latest = g.OrderByDescending(x => x.Timestamp).FirstOrDefault()
            })
            .ToDictionaryAsync(x => x.InstId, x => x.Latest);

        var alertCountsByInst = await _context.SystemAlerts
            .Where(SystemAlert.CustomerVisible)   // same set as the alerts page
            .Where(a => instIds.Contains(a.InstallationId) && a.ResolvedAt == null)
            .GroupBy(a => a.InstallationId)
            .Select(g => new { InstId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.InstId, x => x.Count);

        decimal totalToday = 0, totalMonth = 0, totalLife = 0, totalPower = 0;
        int onlineSites = 0;
        decimal monthRevenueSum = 0;

        foreach (var inst in Customer.Installations)
        {
            todayPerInstall.TryGetValue(inst.Id, out var todayKwh);
            monthPerInstall.TryGetValue(inst.Id, out var monthKwh);
            lifetimePerInstall.TryGetValue(inst.Id, out var lifeKwh);
            realtimePerInstall.TryGetValue(inst.Id, out var realtime);
            alertCountsByInst.TryGetValue(inst.Id, out var alerts);

            var powerKw = realtime?.PowerGenerationKw ?? 0;

            var (cssClass, label) = inst.Status switch
            {
                InstallationStatus.Active      => ("online",      "ทำงานปกติ"),
                InstallationStatus.Maintenance => ("maintenance", "ตรวจสอบ"),
                InstallationStatus.Offline     => ("offline",     "ขาดการเชื่อมต่อ"),
                _                              => ("offline",     inst.Status.ToString()),
            };

            Sites.Add(new SiteCardVm
            {
                Id = inst.Id,
                Name = inst.SiteName,
                Brand = inst.InverterBrand.ToString(),
                CapacityKw = inst.SystemCapacityKw,
                StatusCss = cssClass,
                StatusLabel = label,
                CurrentPowerKw = Math.Round(powerKw, 2),
                TodayKwh = Math.Round(todayKwh, 1),
                OpenAlerts = alerts,
            });

            totalToday += todayKwh;
            totalMonth += monthKwh;
            totalLife  += lifeKwh;
            totalPower += powerKw;
            if (inst.Status == InstallationStatus.Active) onlineSites++;

            monthRevenueSum += monthKwh * (inst.ElectricityRatePerKwh > 0 ? inst.ElectricityRatePerKwh : 4.0m);
        }

        // 0.5 kgCO2 per kWh is the rule of thumb for Thailand's grid mix;
        // tree-year equivalent ≈ 21 kg CO2 absorbed per tree per year, so
        // we present the rounded count of trees needed to absorb the same.
        var co2Kg = totalMonth * 0.5m;
        Overview = new OverviewVm
        {
            TotalCurrentPowerKw = Math.Round(totalPower, 2),
            TotalTodayKwh = Math.Round(totalToday, 1),
            TotalMonthKwh = Math.Round(totalMonth, 1),
            TotalLifetimeKwh = Math.Round(totalLife, 0),
            EstimatedSavingsThbMonth = Math.Round(monthRevenueSum, 0),
            Co2SavedKgMonth = Math.Round(co2Kg, 0),
            TreesEquivalentMonth = (int)Math.Round(co2Kg / 21m),
            OnlineSites = onlineSites,
            TotalSites = Customer.Installations.Count,
        };
        return Page();
    }
}
