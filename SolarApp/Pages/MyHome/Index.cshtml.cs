using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.Performance;

namespace SolarApp.Pages.MyHome;

[Authorize]
public class IndexModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IPerformanceMetricsService _metrics;

    public IndexModel(ApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        IPerformanceMetricsService metrics)
    {
        _context = context;
        _userManager = userManager;
        _metrics = metrics;
    }

    public class SiteSummary
    {
        public Installation Installation { get; set; } = null!;
        public decimal TodayKwh { get; set; }
        public decimal Last7dKwh { get; set; }
        public decimal Last30dKwh { get; set; }
        public decimal LifetimeKwh { get; set; }
        public int UnresolvedAlerts { get; set; }
        public bool IsOnline { get; set; }
        public decimal? CurrentPowerKw { get; set; }
        public decimal? BatterySoc { get; set; }
        public InstallationPerformance Performance { get; set; } = null!;
    }

    public List<SiteSummary> Sites { get; set; } = new();
    public Customer? Customer { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Forbid();

        Customer = await _context.Customers
            .Include(c => c.Installations)
            .FirstOrDefaultAsync(c => c.UserId == user.Id);

        if (Customer == null || !Customer.Installations.Any())
            return Page();

        var today = DateTime.UtcNow.Date;
        var last7d = today.AddDays(-7);
        var last30d = today.AddDays(-30);

        foreach (var inst in Customer.Installations)
        {
            var todayKwh = await _context.EnergyData
                .Where(e => e.InstallationId == inst.Id
                             && e.Granularity == DataGranularity.Daily
                             && e.Timestamp.Date == today)
                .Select(e => (decimal?)e.EnergyGenerationKwh)
                .FirstOrDefaultAsync() ?? 0;

            var weekKwh = await _context.EnergyData
                .Where(e => e.InstallationId == inst.Id
                             && e.Granularity == DataGranularity.Daily
                             && e.Timestamp >= last7d && e.Timestamp <= today)
                .SumAsync(e => (decimal?)e.EnergyGenerationKwh) ?? 0;

            var monthKwh = await _context.EnergyData
                .Where(e => e.InstallationId == inst.Id
                             && e.Granularity == DataGranularity.Daily
                             && e.Timestamp >= last30d && e.Timestamp <= today)
                .SumAsync(e => (decimal?)e.EnergyGenerationKwh) ?? 0;

            var lifeKwh = await _context.EnergyData
                .Where(e => e.InstallationId == inst.Id
                             && e.Granularity == DataGranularity.Daily)
                .SumAsync(e => (decimal?)e.EnergyGenerationKwh) ?? 0;

            var latestRealtime = await _context.EnergyData
                .Where(e => e.InstallationId == inst.Id
                             && e.Granularity == DataGranularity.Realtime)
                .OrderByDescending(e => e.Timestamp)
                .FirstOrDefaultAsync();

            var alerts = await _context.SystemAlerts
                .CountAsync(a => a.InstallationId == inst.Id && a.ResolvedAt == null);

            var perf = await _metrics.GetPerformanceAsync(inst.Id, last30d, today);

            Sites.Add(new SiteSummary
            {
                Installation = inst,
                TodayKwh = Math.Round(todayKwh, 2),
                Last7dKwh = Math.Round(weekKwh, 2),
                Last30dKwh = Math.Round(monthKwh, 2),
                LifetimeKwh = Math.Round(lifeKwh, 2),
                UnresolvedAlerts = alerts,
                IsOnline = inst.Status == InstallationStatus.Active,
                CurrentPowerKw = latestRealtime?.PowerGenerationKw,
                // Gate on Installation.HasBattery so customers without
                // storage don't see a phantom 0% SOC reading from
                // hybrid-inverter firmware quirks.
                BatterySoc = inst.HasBattery ? latestRealtime?.BatterySocPercent : null,
                Performance = perf
            });
        }

        return Page();
    }
}
