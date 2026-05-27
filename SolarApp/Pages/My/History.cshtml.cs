using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Pages.My;

[Authorize]
public class HistoryModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;

    public HistoryModel(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    [BindProperty(SupportsGet = true)] public string Range { get; set; } = "30d";  // 30d / 90d / 1y / all

    public List<DayPoint> Points { get; private set; } = new();
    public decimal Total { get; private set; }
    public decimal Average { get; private set; }
    public decimal BestDay { get; private set; }
    public string BestDayLabel { get; private set; } = "";

    public record DayPoint(string Label, decimal Kwh);

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
        var since = Range switch
        {
            "90d" => today.AddDays(-90),
            "1y"  => today.AddDays(-365),
            "all" => DateTime.MinValue,
            _     => today.AddDays(-30),
        };

        // Sum per-day across ALL of the customer's sites — gives them one
        // unified history rather than forcing them to add per-site numbers.
        var daily = await _context.EnergyData
            .Where(e => instIds.Contains(e.InstallationId)
                && e.Granularity == DataGranularity.Daily
                && e.Timestamp >= since && e.Timestamp <= today)
            .GroupBy(e => e.Timestamp.Date)
            .Select(g => new { Day = g.Key, Sum = g.Sum(x => (decimal?)x.EnergyGenerationKwh) ?? 0 })
            .OrderBy(x => x.Day)
            .ToListAsync();

        Points = daily.Select(d => new DayPoint(
            d.Day.ToLocalTime().ToString("d/M"),
            Math.Round(d.Sum, 1))).ToList();
        Total = Math.Round(daily.Sum(d => d.Sum), 0);
        Average = daily.Count > 0 ? Math.Round(Total / daily.Count, 1) : 0;
        var best = daily.OrderByDescending(d => d.Sum).FirstOrDefault();
        if (best != null)
        {
            BestDay = Math.Round(best.Sum, 1);
            BestDayLabel = best.Day.ToLocalTime().ToString("d/M/yyyy");
        }
        return Page();
    }
}
