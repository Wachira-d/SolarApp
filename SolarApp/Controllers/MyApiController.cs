using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Controllers;

/// <summary>
/// JSON endpoints for the /My/* customer portal pages.  Used by
/// my-portal.js to poll live numbers without a full page reload.
/// Authz: signed-in customer can only see their own installations
/// (filter by Customer.UserId == current user).
/// </summary>
[ApiController]
[Authorize]
[Route("api/my")]
public class MyApiController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;

    public MyApiController(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    private async Task<int?> ResolveCustomerIdAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return null;
        return await _context.Customers
            .Where(c => c.UserId == user.Id)
            .Select(c => (int?)c.Id)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Overview totals across every site the customer owns.  Same shape
    /// as IndexModel.OverviewVm so the JS counter helpers can swap in.
    /// </summary>
    [HttpGet("overview")]
    public async Task<IActionResult> Overview()
    {
        var custId = await ResolveCustomerIdAsync();
        if (custId == null) return Forbid();

        var instIds = await _context.Installations
            .Where(i => i.CustomerId == custId.Value)
            .Select(i => i.Id)
            .ToListAsync();
        if (instIds.Count == 0) return Ok(new { totalCurrentPowerKw = 0, totalTodayKwh = 0 });

        var today = DateTime.UtcNow.Date;
        var totalToday = await _context.EnergyData
            .Where(e => instIds.Contains(e.InstallationId)
                && e.Granularity == DataGranularity.Daily
                && e.Timestamp.Date == today)
            .SumAsync(e => (decimal?)e.EnergyGenerationKwh) ?? 0;

        // Latest realtime per installation, then sum power.  Doing this as a
        // single GroupBy with subquery would be ideal but EF translates the
        // sub-select awkwardly; a small in-memory rollup over the latest
        // record per site is plenty fast for the typical 1-20 sites/customer.
        var latestRows = await _context.EnergyData
            .Where(e => instIds.Contains(e.InstallationId) && e.Granularity == DataGranularity.Realtime)
            .GroupBy(e => e.InstallationId)
            .Select(g => new { InstId = g.Key, Latest = g.OrderByDescending(x => x.Timestamp).FirstOrDefault() })
            .ToListAsync();
        var totalPower = latestRows.Sum(r => r.Latest?.PowerGenerationKw ?? 0);

        return Ok(new
        {
            totalCurrentPowerKw = Math.Round(totalPower, 2),
            totalTodayKwh = Math.Round(totalToday, 1),
        });
    }

    /// <summary>
    /// Per-site live data (current power, today total, battery soc).
    /// Owned-by check uses the join from Installation → Customer.UserId.
    /// </summary>
    [HttpGet("site/{id:int}/realtime")]
    public async Task<IActionResult> SiteRealtime(int id)
    {
        var custId = await ResolveCustomerIdAsync();
        if (custId == null) return Forbid();

        var owned = await _context.Installations
            .AnyAsync(i => i.Id == id && i.CustomerId == custId.Value);
        if (!owned) return NotFound();

        var today = DateTime.UtcNow.Date;
        var rt = await _context.EnergyData
            .Where(e => e.InstallationId == id && e.Granularity == DataGranularity.Realtime)
            .OrderByDescending(e => e.Timestamp)
            .FirstOrDefaultAsync();
        var todayKwh = await _context.EnergyData
            .Where(e => e.InstallationId == id
                && e.Granularity == DataGranularity.Daily
                && e.Timestamp.Date == today)
            .SumAsync(e => (decimal?)e.EnergyGenerationKwh) ?? 0;

        return Ok(new
        {
            currentPowerKw = Math.Round(rt?.PowerGenerationKw ?? 0, 2),
            batterySoc = rt?.BatterySocPercent,
            todayKwh = Math.Round(todayKwh, 1),
            asOf = rt?.Timestamp,
        });
    }
}
