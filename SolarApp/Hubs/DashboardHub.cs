using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace SolarApp.Hubs;

/// <summary>
/// Real-time channel for dashboard metric updates.
/// Server pushes messages like:
///   - "SiteOnline" { InstallationId, SiteName, PowerKw }
///   - "SiteOffline" { InstallationId, SiteName }
///   - "AlertFired" { Title, Severity, InstallationId }
///   - "SyncProgress" { Completed, Total }
/// Clients subscribe to "group" like "all", "partner-{id}", "customer-{id}".
/// </summary>
[Authorize]
public class DashboardHub : Hub
{
    public async Task Subscribe(string group)
    {
        if (string.IsNullOrWhiteSpace(group)) return;
        if (group.Length > 80) return;
        await Groups.AddToGroupAsync(Context.ConnectionId, group);
    }

    public async Task Unsubscribe(string group)
    {
        if (string.IsNullOrWhiteSpace(group)) return;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, group);
    }
}
