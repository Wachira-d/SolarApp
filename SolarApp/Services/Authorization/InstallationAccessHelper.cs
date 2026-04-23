using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Services.Authorization;

public interface IInstallationAccessHelper
{
    Task<bool> CanAccessAsync(int installationId, System.Security.Claims.ClaimsPrincipal principal);
    Task<bool> CanAccessAsync(Installation installation, System.Security.Claims.ClaimsPrincipal principal);
}

/// <summary>
/// Central authorization check for installation access.
/// - Admin: all installations
/// - Partner: only where installation.PartnerId == user.PartnerId
/// - Customer: only where installation.CustomerId == linkedCustomer.Id (UserId match)
/// </summary>
public class InstallationAccessHelper : IInstallationAccessHelper
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;

    public InstallationAccessHelper(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    public async Task<bool> CanAccessAsync(int installationId, System.Security.Claims.ClaimsPrincipal principal)
    {
        var installation = await _context.Installations
            .Select(i => new { i.Id, i.CustomerId, i.PartnerId })
            .FirstOrDefaultAsync(i => i.Id == installationId);

        if (installation == null) return false;

        var user = await _userManager.GetUserAsync(principal);
        if (user == null) return false;

        var roles = await _userManager.GetRolesAsync(user);

        // Admin sees everything
        if (roles.Contains(UserRoles.Admin)) return true;

        // Partner: must have PartnerId and it must match
        if (roles.Contains(UserRoles.Partner))
            return user.PartnerId != null && installation.PartnerId == user.PartnerId;

        // Customer: linked Customer entity must match
        if (roles.Contains(UserRoles.Customer))
        {
            var customer = await _context.Customers
                .Where(c => c.UserId == user.Id)
                .Select(c => c.Id)
                .FirstOrDefaultAsync();
            return customer != 0 && installation.CustomerId == customer;
        }

        return false;
    }

    public async Task<bool> CanAccessAsync(Installation installation, System.Security.Claims.ClaimsPrincipal principal)
    {
        if (installation == null) return false;
        return await CanAccessAsync(installation.Id, principal);
    }
}
