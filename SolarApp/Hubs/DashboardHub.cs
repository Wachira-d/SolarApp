using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;

namespace SolarApp.Hubs;

/// <summary>
/// Real-time channel for dashboard metric updates.
/// Server pushes messages like:
///   - "SiteOnline" { InstallationId, SiteName, PowerKw }
///   - "SiteOffline" { InstallationId, SiteName }
///   - "AlertFired" { Title, Severity, InstallationId }
///   - "SyncProgress" { Completed, Total }
///
/// Clients subscribe to "group" like "all", "partner-{id}", "customer-{id}".
/// Group names are validated against the caller's identity to prevent
/// cross-tenant data leakage (a Partner user cannot listen to another
/// Partner's group, even if they know the ID).
/// </summary>
[Authorize]
public class DashboardHub : Hub
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ApplicationDbContext _context;

    public DashboardHub(UserManager<ApplicationUser> userManager, ApplicationDbContext context)
    {
        _userManager = userManager;
        _context = context;
    }

    public async Task Subscribe(string group)
    {
        if (string.IsNullOrWhiteSpace(group) || group.Length > 80) return;
        if (!await CanJoinAsync(group)) return;
        await Groups.AddToGroupAsync(Context.ConnectionId, group);
    }

    public async Task Unsubscribe(string group)
    {
        if (string.IsNullOrWhiteSpace(group)) return;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, group);
    }

    /// <summary>
    /// Authorise a (caller × group-name) pair. Rules:
    ///   "all"             → Admin only (fleet-wide events)
    ///   "partner-{id}"    → Admin OR matching Partner user
    ///   "customer-{id}"   → Admin OR the linked Customer user
    ///   "installation-{id}" → Admin OR partner whose installation it is
    /// </summary>
    private async Task<bool> CanJoinAsync(string group)
    {
        var user = await _userManager.GetUserAsync(Context.User!);
        if (user == null) return false;

        var isAdmin = await _userManager.IsInRoleAsync(user, "Admin");
        if (isAdmin) return true; // Admins see everything

        if (group == "all") return false; // Non-admins cannot listen fleet-wide

        // partner-{id}
        if (group.StartsWith("partner-", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(group.AsSpan(8), out var partnerId))
        {
            return user.PartnerId == partnerId;
        }

        // customer-{id}
        if (group.StartsWith("customer-", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(group.AsSpan(9), out var customerId))
        {
            return await _context.Customers
                .AnyAsync(c => c.Id == customerId && c.UserId == user.Id);
        }

        // installation-{id}
        if (group.StartsWith("installation-", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(group.AsSpan(13), out var installationId))
        {
            return await _context.Installations.AnyAsync(i =>
                i.Id == installationId
                && (
                    (user.PartnerId.HasValue && i.PartnerId == user.PartnerId)
                    || _context.Customers.Any(c => c.UserId == user.Id && c.Id == i.CustomerId)
                ));
        }

        return false; // Unknown group format
    }
}
