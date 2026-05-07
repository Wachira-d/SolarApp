using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Services.Authorization;

public interface IWorkOrderAccessHelper
{
    /// <summary>Returns true if user can view the WO. Use for GET handlers.</summary>
    Task<bool> CanViewAsync(WorkOrder wo, System.Security.Claims.ClaimsPrincipal principal);

    /// <summary>Returns true if user can modify the WO (status, checklist, photos, costs). Customers cannot edit.</summary>
    Task<bool> CanEditAsync(WorkOrder wo, System.Security.Claims.ClaimsPrincipal principal);

    /// <summary>Builds a queryable filter that limits a WorkOrder query to rows the user can see.</summary>
    Task<IQueryable<WorkOrder>> ApplyVisibilityFilterAsync(IQueryable<WorkOrder> query, System.Security.Claims.ClaimsPrincipal principal);
}

/// <summary>
/// Authorization for WorkOrder access. Handles linked installations + standalone WOs:
/// - Admin: everything
/// - Partner: WOs on installations they own OR they created/were assigned to
/// - Customer: VIEW-ONLY for WOs on their installations OR linked to their CustomerId OR they created
/// </summary>
public class WorkOrderAccessHelper : IWorkOrderAccessHelper
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;

    public WorkOrderAccessHelper(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    public async Task<bool> CanViewAsync(WorkOrder wo, System.Security.Claims.ClaimsPrincipal principal)
    {
        if (wo == null) return false;
        var (user, roles) = await ResolveAsync(principal);
        if (user == null) return false;

        if (roles.Contains(UserRoles.Admin)) return true;

        if (roles.Contains(UserRoles.Partner))
        {
            // Partner: own installation OR created/assigned
            if (wo.CreatedByUserId == user.Id || wo.AssignedToUserId == user.Id) return true;
            if (wo.InstallationId.HasValue && user.PartnerId.HasValue)
            {
                var installPartnerId = await _context.Installations
                    .Where(i => i.Id == wo.InstallationId.Value)
                    .Select(i => (int?)i.PartnerId)
                    .FirstOrDefaultAsync();
                return installPartnerId == user.PartnerId;
            }
            return false;
        }

        if (roles.Contains(UserRoles.Customer))
        {
            var custId = await _context.Customers
                .Where(c => c.UserId == user.Id)
                .Select(c => c.Id)
                .FirstOrDefaultAsync();
            if (custId == 0) return false;

            if (wo.CustomerId == custId) return true;
            if (wo.CreatedByUserId == user.Id) return true;
            if (wo.InstallationId.HasValue)
            {
                var installCustomerId = await _context.Installations
                    .Where(i => i.Id == wo.InstallationId.Value)
                    .Select(i => i.CustomerId)
                    .FirstOrDefaultAsync();
                return installCustomerId == custId;
            }
            return false;
        }

        // Other authenticated users only see their own
        return wo.CreatedByUserId == user.Id || wo.AssignedToUserId == user.Id;
    }

    public async Task<bool> CanEditAsync(WorkOrder wo, System.Security.Claims.ClaimsPrincipal principal)
    {
        if (wo == null) return false;
        var (user, roles) = await ResolveAsync(principal);
        if (user == null) return false;

        // Customers are view-only
        if (roles.Contains(UserRoles.Customer) && !roles.Contains(UserRoles.Admin) && !roles.Contains(UserRoles.Partner))
            return false;

        return await CanViewAsync(wo, principal);
    }

    public async Task<IQueryable<WorkOrder>> ApplyVisibilityFilterAsync(IQueryable<WorkOrder> query, System.Security.Claims.ClaimsPrincipal principal)
    {
        var (user, roles) = await ResolveAsync(principal);
        if (user == null) return query.Where(_ => false);

        if (roles.Contains(UserRoles.Admin)) return query;

        if (roles.Contains(UserRoles.Partner))
        {
            var partnerId = user.PartnerId;
            return query.Where(w => (w.Installation != null && w.Installation.PartnerId == partnerId)
                || w.CreatedByUserId == user.Id
                || w.AssignedToUserId == user.Id);
        }

        if (roles.Contains(UserRoles.Customer))
        {
            var custId = await _context.Customers
                .Where(c => c.UserId == user.Id)
                .Select(c => c.Id)
                .FirstOrDefaultAsync();
            return query.Where(w => (w.Installation != null && w.Installation.CustomerId == custId)
                || w.CustomerId == custId
                || w.CreatedByUserId == user.Id);
        }

        return query.Where(w => w.CreatedByUserId == user.Id || w.AssignedToUserId == user.Id);
    }

    private async Task<(ApplicationUser? user, IList<string> roles)> ResolveAsync(System.Security.Claims.ClaimsPrincipal principal)
    {
        var user = await _userManager.GetUserAsync(principal);
        if (user == null) return (null, Array.Empty<string>());
        var roles = await _userManager.GetRolesAsync(user);
        return (user, roles);
    }
}
