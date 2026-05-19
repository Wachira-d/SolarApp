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

    public async Task<List<Installation>> GetByCustomerIdAsync(string userId)
    {
        // Find Customer entity linked to the user
        var customer = await _context.Customers.FirstOrDefaultAsync(c => c.UserId == userId);
        if (customer == null) return new List<Installation>();

        return await _context.Installations
            .Include(i => i.Customer)
            .Include(i => i.Partner)
            .Where(i => i.CustomerId == customer.Id)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync();
    }

    public async Task<List<Installation>> GetByPartnerIdAsync(string userId)
    {
        // Find Partner via user's PartnerId
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user?.PartnerId == null) return new List<Installation>();

        return await _context.Installations
            .Include(i => i.Customer)
            .Include(i => i.Partner)
            .Where(i => i.PartnerId == user.PartnerId)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync();
    }

    public async Task<Installation?> GetByIdAsync(int id)
    {
        return await _context.Installations
            .Include(i => i.Customer)
            .Include(i => i.Partner)
            .Include(i => i.Equipment)
            .Include(i => i.WarrantyRecords)
            .Include(i => i.CleaningRecords.OrderByDescending(c => c.ScheduledDate))
            .Include(i => i.Documents)
            .Include(i => i.Tariffs.OrderByDescending(t => t.EffectiveFrom))
            .Include(i => i.MaintenanceRecords.OrderByDescending(m => m.ScheduledDate))
            .Include(i => i.Alerts.Where(a => a.ResolvedAt == null).OrderByDescending(a => a.OccurredAt))
            .FirstOrDefaultAsync(i => i.Id == id);
    }

    public async Task<Installation> CreateAsync(Installation installation)
    {
        // Auto-generate site code
        var count = await _context.Installations.CountAsync();
        installation.SiteCode = $"SI-{DateTime.UtcNow:yyyy}-{(count + 1):D4}";

        installation.WarrantyExpiryDate = installation.InstallationDate.AddYears(installation.WarrantyYears);
        if (installation.InverterWarrantyYears > 0)
            installation.InverterWarrantyExpiryDate = installation.InstallationDate.AddYears(installation.InverterWarrantyYears);

        installation.CreatedAt = DateTime.UtcNow;
        installation.UpdatedAt = DateTime.UtcNow;

        _context.Installations.Add(installation);
        await _context.SaveChangesAsync();
        return installation;
    }

    public async Task UpdateAsync(Installation installation)
    {
        installation.UpdatedAt = DateTime.UtcNow;
        // Avoid _context.Installations.Update(installation): when the
        // installation was loaded via GetByIdAsync (which Include()s
        // Customer/Partner/Equipment/etc.), Update cascades Modified state
        // onto every still-tracked navigation property.  EF then issues
        // UPDATEs on those rows using whatever's currently in memory — so a
        // reassign of installation.CustomerId without re-syncing the
        // navigation property rewrites the old Customer row with stale
        // in-memory data, manifesting as a customer rename.
        //
        // Mark the row Modified ourselves only when the entity isn't already
        // tracked (e.g. a future caller passes in a detached instance).
        var entry = _context.Entry(installation);
        if (entry.State == EntityState.Detached)
            entry.State = EntityState.Modified;
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
            installation.NextCleaningDueDate = cleaningDate.AddMonths(installation.CleaningFrequencyMonths);
        else
            installation.NextCleaningDueDate = null;

        // Create cleaning record
        _context.CleaningRecords.Add(new CleaningRecord
        {
            InstallationId = installationId,
            ScheduledDate = cleaningDate,
            ActualDate = cleaningDate,
            Status = CleaningStatus.Completed
        });

        installation.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
    }

    public async Task<List<Installation>> GetInstallationsNeedingCleaningAsync()
    {
        // Only sites we're contracted to clean.  Partner-monitored sites that
        // we don't visit must not generate cleaning reminders.
        return await _context.Installations
            .Include(i => i.Customer)
            .Where(i => i.IncludesCleaning
                        && i.NextCleaningDueDate != null
                        && i.NextCleaningDueDate <= DateTime.UtcNow)
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
