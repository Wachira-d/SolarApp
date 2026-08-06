using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Pages.Partners;

/// <summary>
/// Partner directory.  Answers "which installer is responsible for what, and
/// how is their fleet doing" — which previously required filtering the
/// installation list one partner at a time.
/// </summary>
[Authorize(Roles = "Admin,Partner")]
public class IndexModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;

    public IndexModel(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    public class PartnerRow
    {
        public int Id { get; set; }
        public string CompanyName { get; set; } = string.Empty;
        public string ContactEmail { get; set; } = string.Empty;
        public string ContactPhone { get; set; } = string.Empty;
        public string? Province { get; set; }
        public bool IsActive { get; set; }
        public DateTime? ContractEndDate { get; set; }
        public int UserCount { get; set; }
        public int SiteCount { get; set; }
        public int CustomerCount { get; set; }
        public int OfflineCount { get; set; }
        public decimal CapacityKw { get; set; }
        public int OpenAlerts { get; set; }

        /// <summary>Contract lapsed or lapsing inside 30 days — worth chasing.</summary>
        public bool ContractAtRisk =>
            ContractEndDate.HasValue && ContractEndDate.Value <= DateTime.UtcNow.AddDays(30);
    }

    public List<PartnerRow> Rows { get; private set; } = new();
    public bool IsAdmin { get; private set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }

    public int TotalSites { get; private set; }
    public decimal TotalCapacityKw { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Forbid();
        IsAdmin = User.IsInRole(UserRoles.Admin);

        // A partner lands here from the nav too, but only ever sees their own
        // company — the directory is otherwise a list of their competitors.
        if (!IsAdmin && user.PartnerId == null) return Forbid();

        var siteStats = await _context.Installations.AsNoTracking()
            .Where(i => i.PartnerId != null)
            .GroupBy(i => i.PartnerId!.Value)
            .Select(g => new
            {
                PartnerId = g.Key,
                SiteCount = g.Count(),
                CustomerCount = g.Select(i => i.CustomerId).Distinct().Count(),
                OfflineCount = g.Count(i => i.Status == InstallationStatus.Offline),
                CapacityKw = g.Sum(i => (decimal?)i.SystemCapacityKw) ?? 0
            })
            .ToDictionaryAsync(x => x.PartnerId);

        var alertStats = await _context.SystemAlerts.AsNoTracking()
            .Where(a => a.ResolvedAt == null && a.Installation.PartnerId != null)
            .GroupBy(a => a.Installation.PartnerId!.Value)
            .Select(g => new { PartnerId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PartnerId, x => x.Count);

        var userStats = await _context.Users.AsNoTracking()
            .Where(u => u.PartnerId != null)
            .GroupBy(u => u.PartnerId!.Value)
            .Select(g => new { PartnerId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PartnerId, x => x.Count);

        var query = _context.Partners.AsNoTracking();
        if (!IsAdmin)
            query = query.Where(p => p.Id == user.PartnerId);

        if (!string.IsNullOrWhiteSpace(Q))
        {
            var q = Q.Trim();
            query = query.Where(p =>
                p.CompanyName.Contains(q)
                || p.ContactEmail.Contains(q)
                || p.ContactPhone.Contains(q)
                || (p.Province != null && p.Province.Contains(q)));
        }

        var partners = await query.ToListAsync();

        Rows = partners.Select(p =>
        {
            siteStats.TryGetValue(p.Id, out var s);
            alertStats.TryGetValue(p.Id, out var alerts);
            userStats.TryGetValue(p.Id, out var users);
            return new PartnerRow
            {
                Id = p.Id,
                CompanyName = p.CompanyName,
                ContactEmail = p.ContactEmail,
                ContactPhone = p.ContactPhone,
                Province = p.Province,
                IsActive = p.IsActive,
                ContractEndDate = p.ContractEndDate,
                UserCount = users,
                SiteCount = s?.SiteCount ?? 0,
                CustomerCount = s?.CustomerCount ?? 0,
                OfflineCount = s?.OfflineCount ?? 0,
                CapacityKw = s?.CapacityKw ?? 0,
                OpenAlerts = alerts
            };
        })
        .OrderByDescending(r => r.OfflineCount > 0)
        .ThenByDescending(r => r.SiteCount)
        .ThenBy(r => r.CompanyName)
        .ToList();

        TotalSites = Rows.Sum(r => r.SiteCount);
        TotalCapacityKw = Rows.Sum(r => r.CapacityKw);
        return Page();
    }
}
