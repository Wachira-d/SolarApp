using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;

namespace SolarApp.Services.Admin;

public interface IUserAdminQueryService
{
    Task<UserAdminPageData> LoadPageDataAsync(CancellationToken ct = default);
}

public record UserAdminPageData(
    List<UserAdminRow> Users,
    List<Customer> Customers,
    List<Partner> Partners);

public record UserAdminRow(
    string Id,
    string FullName,
    string Email,
    string? CompanyName,
    string? PhoneNumber,
    string? LineUserId,
    int? PartnerId,
    string? PartnerName,
    string Role,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? LastLoginAt);

/// <summary>
/// Extracted from UsersModel.LoadDataAsync — eliminates N+1 query bug
/// (was: 1 query for users + N queries for roles + N for partner includes).
/// Now: 3 batched queries total regardless of user count.
/// </summary>
public class UserAdminQueryService : IUserAdminQueryService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly ApplicationDbContext _context;

    public UserAdminQueryService(
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager,
        ApplicationDbContext context)
    {
        _userManager = userManager;
        _roleManager = roleManager;
        _context = context;
    }

    public async Task<UserAdminPageData> LoadPageDataAsync(CancellationToken ct = default)
    {
        // 1) Users + Partner navigation
        var users = await _userManager.Users
            .Include(u => u.Partner)
            .OrderByDescending(u => u.CreatedAt)
            .ToListAsync(ct);

        // 2) Roles lookup: single JOIN replaces per-user GetRolesAsync calls
        var userIds = users.Select(u => u.Id).ToList();
        var rolesByUser = await (
            from ur in _context.UserRoles
            join r in _context.Roles on ur.RoleId equals r.Id
            where userIds.Contains(ur.UserId)
            select new { ur.UserId, r.Name }
        ).ToListAsync(ct);

        var rolesDict = rolesByUser
            .GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Name ?? "").FirstOrDefault() ?? "-");

        var userRows = users.Select(u => new UserAdminRow(
            u.Id,
            u.FullName,
            u.Email ?? "",
            u.CompanyName,
            u.PhoneNumber,
            u.LineUserId,
            u.PartnerId,
            u.Partner?.CompanyName,
            rolesDict.TryGetValue(u.Id, out var role) ? role : "-",
            u.IsActive,
            u.CreatedAt,
            u.LastLoginAt
        )).ToList();

        // 3) Customers + Partners — read-only, no tracking
        // Include(Installations) is still needed because the Razor view reads
        // @c.Installations.Count / @p.Installations.Count. A future refactor
        // could project to {Customer, InstallCount} DTOs and drop the Include.
        var customers = await _context.Customers
            .AsNoTracking()
            .Include(c => c.Installations)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);

        var partners = await _context.Partners
            .AsNoTracking()
            .Include(p => p.Installations)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);

        return new UserAdminPageData(userRows, customers, partners);
    }
}
