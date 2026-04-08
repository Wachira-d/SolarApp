using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.Interfaces;

namespace SolarApp.Services.Implementations;

public class InstallationService : IInstallationService
{
    private readonly ApplicationDbContext _context;

    public InstallationService(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<Installation>> GetAllAsync()
    {
        return await _context.Installations
            .Include(i => i.Customer)
            .Include(i => i.Partner)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync();
    }

    public async Task<List<Installation>> GetByCustomerIdAsync(string customerId)
    {
        return await _context.Installations
            .Include(i => i.Customer)
            .Include(i => i.Partner)
            .Where(i => i.CustomerId == customerId)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync();
    }

    public async Task<List<Installation>> GetByPartnerIdAsync(string partnerId)
    {
        return await _context.Installations
            .Include(i => i.Customer)
            .Include(i => i.Partner)
            .Where(i => i.PartnerId == partnerId)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync();
    }

    public async Task<Installation?> GetByIdAsync(int id)
    {
        return await _context.Installations
            .Include(i => i.Customer)
            .Include(i => i.Partner)
            .Include(i => i.Equipment)
            .Include(i => i.MaintenanceRecords.OrderByDescending(m => m.ScheduledDate))
            .Include(i => i.Alerts.Where(a => a.ResolvedAt == null).OrderByDescending(a => a.OccurredAt))
            .FirstOrDefaultAsync(i => i.Id == id);
    }

    public async Task<Installation> CreateAsync(Installation installation)
    {
        installation.WarrantyExpiryDate = installation.InstallationDate.AddYears(installation.WarrantyYears);
        installation.CreatedAt = DateTime.UtcNow;
        installation.UpdatedAt = DateTime.UtcNow;

        _context.Installations.Add(installation);
        await _context.SaveChangesAsync();
        return installation;
    }

    public async Task UpdateAsync(Installation installation)
    {
        installation.UpdatedAt = DateTime.UtcNow;
        _context.Installations.Update(installation);
        await _context.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        var installation = await _context.Installations.FindAsync(id);
        if (installation != null)
        {
            _context.Installations.Remove(installation);
            await _context.SaveChangesAsync();
        }
    }

    public async Task UpdateCleaningAsync(int installationId, DateTime cleaningDate)
    {
        var installation = await _context.Installations.FindAsync(installationId);
        if (installation == null) return;

        installation.CompletedCleaningCount++;
        installation.LastCleaningDate = cleaningDate;

        if (installation.CompletedCleaningCount < installation.TotalCleaningCount)
        {
            // Schedule next cleaning in 6 months by default
            installation.NextCleaningDueDate = cleaningDate.AddMonths(6);
        }
        else
        {
            installation.NextCleaningDueDate = null;
        }

        installation.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
    }

    public async Task<List<Installation>> GetInstallationsNeedingCleaningAsync()
    {
        return await _context.Installations
            .Include(i => i.Customer)
            .Where(i => i.NextCleaningDueDate != null && i.NextCleaningDueDate <= DateTime.UtcNow)
            .ToListAsync();
    }

    public async Task<List<Installation>> GetWarrantyExpiringAsync(int withinDays = 90)
    {
        var threshold = DateTime.UtcNow.AddDays(withinDays);
        return await _context.Installations
            .Include(i => i.Customer)
            .Where(i => i.WarrantyExpiryDate <= threshold && i.WarrantyExpiryDate > DateTime.UtcNow)
            .ToListAsync();
    }
}
