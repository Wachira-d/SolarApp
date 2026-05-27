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
/// Single-site detail view — animated power gauge, today's production
/// curve, lifetime stats, equipment list, recent customer-friendly
/// alerts.  Bound by route id (/My/Site/{id}); authz enforces the
/// installation belongs to the signed-in customer.
/// </summary>
[Authorize]
public class SiteModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;

    public SiteModel(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    public Installation? Site { get; private set; }
    public decimal CurrentPowerKw { get; private set; }
    public decimal? BatterySoc { get; private set; }
    public decimal TodayKwh { get; private set; }
    public decimal WeekKwh { get; private set; }
    public decimal MonthKwh { get; private set; }
    public decimal YearKwh { get; private set; }
    public decimal LifetimeKwh { get; private set; }
    public decimal EstimatedSavingsThbMonth { get; private set; }
    public decimal Co2SavedKgMonth { get; private set; }
    public int TreesEquivalent { get; private set; }
    public List<HourlyPoint> TodayCurve { get; private set; } = new();
    public List<DailyPoint> Last30Days { get; private set; } = new();
    public List<EquipmentVm> Equipment { get; private set; } = new();
    public List<AlertVm> RecentAlerts { get; private set; } = new();

    public record HourlyPoint(string Label, decimal Kw);
    public record DailyPoint(string Label, decimal Kwh);
    public record EquipmentVm(string Name, string Type, string? Model, bool IsOnline, decimal? RatingKw);
    public record AlertVm(string Title, string Severity, DateTime When, bool Resolved);

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Forbid();

        var customer = await _context.Customers
            .Where(c => c.UserId == user.Id)
            .Select(c => new { c.Id })
            .FirstOrDefaultAsync();
        if (customer == null) return NotFound();

        Site = await _context.Installations
            .Include(i => i.Equipment)
            .FirstOrDefaultAsync(i => i.Id == id && i.CustomerId == customer.Id);
        if (Site == null) return NotFound();

        var today = SolarApp.Services.SiteTime.Today;
        var weekStart = today.AddDays(-7);
        var monthStart = today.AddDays(-30);
        var yearStart = new DateTime(today.Year, 1, 1);

        var latestRealtime = await _context.EnergyData
            .Where(e => e.InstallationId == id && e.Granularity == DataGranularity.Realtime)
            .OrderByDescending(e => e.Timestamp)
            .FirstOrDefaultAsync();
        CurrentPowerKw = Math.Round(latestRealtime?.PowerGenerationKw ?? 0, 2);
        BatterySoc = latestRealtime?.BatterySocPercent;

        async Task<decimal> SumSince(DateTime since)
        {
            var s = await _context.EnergyData
                .Where(e => e.InstallationId == id
                    && e.Granularity == DataGranularity.Daily
                    && e.Timestamp >= since && e.Timestamp <= today)
                .SumAsync(e => (decimal?)e.EnergyGenerationKwh) ?? 0;
            return Math.Round(s, 1);
        }
        TodayKwh = await SumSince(today);
        WeekKwh = await SumSince(weekStart);
        MonthKwh = await SumSince(monthStart);
        YearKwh = await SumSince(yearStart);
        LifetimeKwh = await _context.EnergyData
            .Where(e => e.InstallationId == id && e.Granularity == DataGranularity.Daily)
            .SumAsync(e => (decimal?)e.EnergyGenerationKwh) ?? 0;
        LifetimeKwh = Math.Round(LifetimeKwh, 0);

        // Today's hourly curve (real samples) for the chart.  Falls back to
        // the latest few real-time points when no hourly aggregate exists.
        var hourly = await _context.EnergyData
            .Where(e => e.InstallationId == id
                && e.Granularity == DataGranularity.Hourly
                && e.Timestamp >= today)
            .OrderBy(e => e.Timestamp)
            .Select(e => new { e.Timestamp, e.PowerGenerationKw })
            .ToListAsync();
        TodayCurve = hourly.Select(h => new HourlyPoint(
            h.Timestamp.ToLocalTime().ToString("HH:mm"),
            Math.Round(h.PowerGenerationKw, 2))).ToList();

        // 30-day production bars (Daily aggregate)
        var daily30 = await _context.EnergyData
            .Where(e => e.InstallationId == id
                && e.Granularity == DataGranularity.Daily
                && e.Timestamp >= monthStart)
            .OrderBy(e => e.Timestamp)
            .Select(e => new { e.Timestamp, e.EnergyGenerationKwh })
            .ToListAsync();
        Last30Days = daily30.Select(d => new DailyPoint(
            d.Timestamp.ToLocalTime().ToString("d/M"),
            Math.Round(d.EnergyGenerationKwh, 1))).ToList();

        var rate = Site.ElectricityRatePerKwh > 0 ? Site.ElectricityRatePerKwh : 4.0m;
        EstimatedSavingsThbMonth = Math.Round(MonthKwh * rate, 0);
        Co2SavedKgMonth = Math.Round(MonthKwh * 0.5m, 0);
        TreesEquivalent = (int)Math.Round(Co2SavedKgMonth / 21m);

        Equipment = Site.Equipment
            .Select(e => new EquipmentVm(
                !string.IsNullOrEmpty(e.SerialNumber) ? e.SerialNumber : e.Model,
                e.EquipmentType.ToString(),
                string.IsNullOrEmpty(e.Model) ? null : e.Model,
                e.IsActive,
                e.RatingKw))
            .ToList();

        // Customer-friendly alerts: most recent 5 that the operator's filters
        // would deliver to LINE (offline / system errors).  Internal stuff
        // (API failure, low production internal) is hidden — they get the
        // technical channel.
        RecentAlerts = await _context.SystemAlerts
            .Where(a => a.InstallationId == id
                && (a.AlertType == AlertType.Offline || a.AlertType == AlertType.Error))
            .OrderByDescending(a => a.OccurredAt)
            .Take(5)
            .Select(a => new AlertVm(
                a.Title,
                a.Severity.ToString().ToLowerInvariant(),
                a.OccurredAt,
                a.ResolvedAt != null))
            .ToListAsync();

        return Page();
    }
}
