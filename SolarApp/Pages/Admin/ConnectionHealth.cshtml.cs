using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;

namespace SolarApp.Pages.Admin;

/// <summary>
/// Fleet-wide connection-health view.  Aggregates InverterApiLog into
/// per-brand and per-site success rates over a rolling window so the
/// operator can see, at a glance, which vendor integrations and which
/// sites are struggling — the app previously only had a per-site probe on
/// the Diagnostics page, with no fleet roll-up.
/// </summary>
[Authorize(Roles = "Admin")]
public class ConnectionHealthModel : PageModel
{
    private readonly ApplicationDbContext _context;

    public ConnectionHealthModel(ApplicationDbContext context) => _context = context;

    /// <summary>Rolling window in hours (from query string, default 24).</summary>
    public int WindowHours { get; private set; } = 24;

    public List<BrandHealth> Brands { get; private set; } = new();
    public List<SiteHealth> Sites { get; private set; } = new();
    public int TotalCalls { get; private set; }
    public int TotalSuccess { get; private set; }
    public double OverallRate => TotalCalls == 0 ? 100 : Math.Round(100.0 * TotalSuccess / TotalCalls, 1);

    public record BrandHealth(string Provider, int Total, int Success)
    {
        public int Failed => Total - Success;
        public double Rate => Total == 0 ? 100 : Math.Round(100.0 * Success / Total, 1);
    }

    public record SiteHealth(int Id, string SiteName, string Brand, int Total, int Success, DateTime? LastFail)
    {
        public int Failed => Total - Success;
        public double Rate => Total == 0 ? 100 : Math.Round(100.0 * Success / Total, 1);
    }

    public async Task OnGetAsync(int? hours)
    {
        WindowHours = hours is > 0 and <= 720 ? hours.Value : 24;
        var since = DateTime.UtcNow.AddHours(-WindowHours);

        // Per-brand roll-up.
        var brandRows = await _context.InverterApiLogs
            .Where(l => l.RequestTime >= since)
            .GroupBy(l => l.ApiProvider)
            .Select(g => new { Provider = g.Key, Total = g.Count(), Success = g.Count(x => x.IsSuccess) })
            .ToListAsync();
        Brands = brandRows
            .Select(b => new BrandHealth(string.IsNullOrEmpty(b.Provider) ? "(unknown)" : b.Provider, b.Total, b.Success))
            .OrderBy(b => b.Rate)
            .ToList();
        TotalCalls = brandRows.Sum(b => b.Total);
        TotalSuccess = brandRows.Sum(b => b.Success);

        // Per-site aggregates (worst first).  Join site names in memory.
        var siteAgg = await _context.InverterApiLogs
            .Where(l => l.RequestTime >= since)
            .GroupBy(l => l.InstallationId)
            .Select(g => new
            {
                InstallationId = g.Key,
                Total = g.Count(),
                Success = g.Count(x => x.IsSuccess),
                LastFail = g.Where(x => !x.IsSuccess).Max(x => (DateTime?)x.RequestTime),
            })
            .ToListAsync();

        var ids = siteAgg.Select(s => s.InstallationId).ToList();
        var siteMeta = await _context.Installations
            .Where(i => ids.Contains(i.Id))
            .Select(i => new { i.Id, i.SiteName, i.InverterBrand })
            .ToDictionaryAsync(i => i.Id);

        Sites = siteAgg
            .Select(s =>
            {
                siteMeta.TryGetValue(s.InstallationId, out var meta);
                return new SiteHealth(
                    s.InstallationId,
                    meta?.SiteName ?? $"#{s.InstallationId}",
                    meta?.InverterBrand.ToString() ?? "-",
                    s.Total, s.Success, s.LastFail);
            })
            .OrderBy(s => s.Rate)          // worst success rate first
            .ThenByDescending(s => s.Failed)
            .Take(100)
            .ToList();
    }
}
