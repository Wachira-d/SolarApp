using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Pages.Customers;

/// <summary>
/// Customer directory.  Admin/Users can create and edit a customer record,
/// but there was nowhere to answer "who are our customers, how many sites do
/// they have, and which of them need attention right now" — that meant
/// scanning the installation list and grouping by hand.
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

    public class CustomerRow
    {
        public int Id { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string? Email { get; set; }
        public string Phone { get; set; } = string.Empty;
        public string? Province { get; set; }
        public bool IsActive { get; set; }
        public bool HasPortalLogin { get; set; }
        public bool HasLine { get; set; }
        public int SiteCount { get; set; }
        public int OfflineCount { get; set; }
        public decimal CapacityKw { get; set; }
        public int OpenAlerts { get; set; }
    }

    public List<CustomerRow> Rows { get; private set; } = new();
    public bool IsAdmin { get; private set; }

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    /// <summary>null = all, true = only customers with an unresolved alert.</summary>
    [BindProperty(SupportsGet = true)] public bool NeedsAttention { get; set; }

    public int TotalCustomers { get; private set; }
    public int TotalSites { get; private set; }
    public decimal TotalCapacityKw { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Forbid();
        IsAdmin = User.IsInRole(UserRoles.Admin);

        // A partner only ever sees customers they actually service — scoping on
        // the installation, because a customer can hold sites under more than
        // one partner and the partner must not see the others.
        var installQuery = _context.Installations.AsNoTracking();
        if (!IsAdmin)
        {
            if (user.PartnerId == null) return Forbid();
            installQuery = installQuery.Where(i => i.PartnerId == user.PartnerId);
        }

        var siteStats = await installQuery
            .GroupBy(i => i.CustomerId)
            .Select(g => new
            {
                CustomerId = g.Key,
                SiteCount = g.Count(),
                OfflineCount = g.Count(i => i.Status == InstallationStatus.Offline),
                CapacityKw = g.Sum(i => (decimal?)i.SystemCapacityKw) ?? 0
            })
            .ToDictionaryAsync(x => x.CustomerId);

        var alertQuery = _context.SystemAlerts.AsNoTracking().Where(a => a.ResolvedAt == null);
        if (!IsAdmin)
            alertQuery = alertQuery.Where(a => a.Installation.PartnerId == user.PartnerId);

        var alertStats = await alertQuery
            .GroupBy(a => a.Installation.CustomerId)
            .Select(g => new { CustomerId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CustomerId, x => x.Count);

        var customerQuery = _context.Customers.AsNoTracking();
        if (!IsAdmin)
        {
            var visibleIds = siteStats.Keys.ToList();
            customerQuery = customerQuery.Where(c => visibleIds.Contains(c.Id));
        }

        if (!string.IsNullOrWhiteSpace(Q))
        {
            var q = Q.Trim();
            customerQuery = customerQuery.Where(c =>
                c.FirstName.Contains(q) || c.LastName.Contains(q)
                || (c.Email != null && c.Email.Contains(q))
                || c.Phone.Contains(q)
                || (c.Province != null && c.Province.Contains(q)));
        }

        var customers = await customerQuery
            .Select(c => new
            {
                c.Id,
                c.FirstName,
                c.LastName,
                c.Email,
                c.Phone,
                c.Province,
                c.IsActive,
                HasPortalLogin = c.UserId != null,
                HasLine = c.LineUserId != null && c.LineUserId != ""
            })
            .ToListAsync();

        Rows = customers.Select(c =>
        {
            siteStats.TryGetValue(c.Id, out var s);
            alertStats.TryGetValue(c.Id, out var alerts);
            return new CustomerRow
            {
                Id = c.Id,
                FullName = $"{c.FirstName} {c.LastName}".Trim(),
                Email = c.Email,
                Phone = c.Phone,
                Province = c.Province,
                IsActive = c.IsActive,
                HasPortalLogin = c.HasPortalLogin,
                HasLine = c.HasLine,
                SiteCount = s?.SiteCount ?? 0,
                OfflineCount = s?.OfflineCount ?? 0,
                CapacityKw = s?.CapacityKw ?? 0,
                OpenAlerts = alerts
            };
        }).ToList();

        if (NeedsAttention)
            Rows = Rows.Where(r => r.OpenAlerts > 0 || r.OfflineCount > 0).ToList();

        // Customers with problems first, then the biggest accounts — the list
        // is read top-down when deciding who to call.
        Rows = Rows
            .OrderByDescending(r => r.OfflineCount > 0)
            .ThenByDescending(r => r.OpenAlerts)
            .ThenByDescending(r => r.CapacityKw)
            .ThenBy(r => r.FullName)
            .ToList();

        TotalCustomers = Rows.Count;
        TotalSites = Rows.Sum(r => r.SiteCount);
        TotalCapacityKw = Rows.Sum(r => r.CapacityKw);
        return Page();
    }
}
