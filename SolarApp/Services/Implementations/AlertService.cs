using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.Interfaces;

namespace SolarApp.Services.Implementations;

public class AlertService : IAlertService
{
    private readonly ApplicationDbContext _context;

    public AlertService(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<SystemAlert>> GetActiveAlertsAsync(int? installationId = null)
    {
        var query = _context.SystemAlerts
            .AsNoTracking()                    // read-only path; tracking overhead wasted
            .Include(a => a.Installation)
            .Where(a => a.ResolvedAt == null);

        if (installationId.HasValue)
            query = query.Where(a => a.InstallationId == installationId.Value);

        return await query
            .OrderByDescending(a => a.Severity)
            .ThenByDescending(a => a.OccurredAt)
            .ToListAsync();
    }

    public async Task<List<SystemAlert>> GetAlertsByInstallationAsync(int installationId)
    {
        return await _context.SystemAlerts
            .AsNoTracking()
            .Where(a => a.InstallationId == installationId)
            .OrderByDescending(a => a.OccurredAt)
            .Take(50)
            .ToListAsync();
    }

    public async Task CreateAlertAsync(SystemAlert alert)
    {
        alert.OccurredAt = DateTime.UtcNow;
        _context.SystemAlerts.Add(alert);
        await _context.SaveChangesAsync();
    }

    public async Task AcknowledgeAlertAsync(int alertId, string userId)
    {
        var alert = await _context.SystemAlerts.FindAsync(alertId);
        if (alert == null) return;

        alert.AcknowledgedAt = DateTime.UtcNow;
        alert.AcknowledgedByUserId = userId;
        await _context.SaveChangesAsync();
    }

    public async Task ResolveAlertAsync(int alertId)
    {
        var alert = await _context.SystemAlerts.FindAsync(alertId);
        if (alert == null) return;

        alert.ResolvedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
    }

    public async Task<int> GetUnreadAlertCountAsync(string? userId, string role)
    {
        var query = _context.SystemAlerts
            .Where(a => a.ResolvedAt == null && a.AcknowledgedAt == null);

        if (role == UserRoles.Customer && userId != null)
        {
            var customer = await _context.Customers.FirstOrDefaultAsync(c => c.UserId == userId);
            if (customer != null)
            {
                var installationIds = await _context.Installations
                    .Where(i => i.CustomerId == customer.Id)
                    .Select(i => i.Id)
                    .ToListAsync();
                query = query.Where(a => installationIds.Contains(a.InstallationId));
            }
            else
            {
                return 0;
            }
        }
        else if (role == UserRoles.Partner && userId != null)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user?.PartnerId != null)
            {
                var installationIds = await _context.Installations
                    .Where(i => i.PartnerId == user.PartnerId)
                    .Select(i => i.Id)
                    .ToListAsync();
                query = query.Where(a => installationIds.Contains(a.InstallationId));
            }
            else
            {
                return 0;
            }
        }

        return await query.CountAsync();
    }
}
