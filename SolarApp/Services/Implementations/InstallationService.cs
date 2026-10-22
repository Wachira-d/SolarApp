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

        // Only seed the end date when the caller didn't supply one. Computing
        // it unconditionally here was half of why an edited warranty date
        // could never stick.
        if (!installation.HasWarrantyDate)
            installation.WarrantyExpiryDate =
                Installation.CalculateWarrantyEnd(installation.InstallationDate, installation.WarrantyYears);

        if (installation.InverterWarrantyExpiryDate == null && installation.InverterWarrantyYears > 0)
            installation.InverterWarrantyExpiryDate =
                Installation.CalculateWarrantyEnd(installation.InstallationDate, installation.InverterWarrantyYears);

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

    public async Task DeleteAsync(int id, string? deletedByUserId = null)
    {
        var installation = await _context.Installations
            .FirstOrDefaultAsync(i => i.Id == id);
        if (installation == null) return;

        // Soft delete. This used to cascade-delete the site's entire history —
        // years of production data, every cleaning record, the photos proving
        // the work happened — which is what an O&M contract is settled on, with
        // no undo and no backup granular enough to restore one site. The flag
        // takes the site out of every query (global filter in the DbContext)
        // and leaves the evidence in place; PurgeAsync still does the real
        // thing for a site created by mistake.
        installation.DeletedAt = DateTime.UtcNow;
        installation.DeletedByUserId = deletedByUserId;
        installation.Status = InstallationStatus.Inactive;
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Irreversibly removes a site and every record attached to it. For a site
    /// created in error — a typo'd duplicate, a test row — where keeping the
    /// history would be noise rather than evidence. Everything else should go
    /// through <see cref="DeleteAsync"/>.
    /// </summary>
    public async Task PurgeAsync(int id)
    {
        // Every Installation FK in the DbContext is configured with
        // DeleteBehavior.Restrict (intentional — we never want EF to silently
        // nuke historical energy data on a routine .Remove call), so the
        // dependent rows come out by hand, in FK order, inside a transaction
        // so a mid-delete failure doesn't leave half-deleted state.
        //
        // AlertRule.InstallationId is already Cascade, so EF clears those.
        //
        // Callers do permission + confirmation upstream; this just executes.
        var installation = await _context.Installations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == id);
        if (installation == null) return;

        await using var tx = await _context.Database.BeginTransactionAsync();

        // 1) Time-series + log data (largest tables first so we fail fast
        //    if SQL Server rejects the bulk delete — typically permissions).
        await _context.EnergyData.Where(e => e.InstallationId == id).ExecuteDeleteAsync();
        await _context.InverterApiLogs.Where(l => l.InstallationId == id).ExecuteDeleteAsync();

        // 2) Per-site operational records.  WorkOrders may reference an
        //    Equipment row (cascade), so delete WO before Equipment.
        await _context.WorkOrders.Where(w => w.InstallationId == id).ExecuteDeleteAsync();
        await _context.SystemAlerts.Where(a => a.InstallationId == id).ExecuteDeleteAsync();
        await _context.ServiceTickets.Where(t => t.InstallationId == id).ExecuteDeleteAsync();
        await _context.MaintenanceRecords.Where(m => m.InstallationId == id).ExecuteDeleteAsync();
        await _context.CleaningRecords.Where(c => c.InstallationId == id).ExecuteDeleteAsync();
        await _context.WarrantyRecords.Where(w => w.InstallationId == id).ExecuteDeleteAsync();
        await _context.InstallationDocuments.Where(d => d.InstallationId == id).ExecuteDeleteAsync();
        await _context.ElectricityTariffs.Where(t => t.InstallationId == id).ExecuteDeleteAsync();

        // 3) Equipment last among the dependents — other tables may FK
        //    into it (WarrantyRecord.EquipmentId, etc.) and they've just
        //    been cleared above.
        await _context.Equipment.Where(e => e.InstallationId == id).ExecuteDeleteAsync();

        // 4) Finally the Installation row itself.  AlertRule.InstallationId
        //    is Cascade so SQL Server clears those automatically.
        _context.Installations.Remove(installation);
        await _context.SaveChangesAsync();

        await tx.CommitAsync();
    }

    public async Task UpdateCleaningAsync(int installationId, DateTime cleaningDate)
    {
        var installation = await _context.Installations.FindAsync(installationId);
        if (installation == null) return;

        installation.RecordCompletedCleaning(cleaningDate);

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
            .Where(Installation.CleaningDueAsOf(SolarApp.Services.SiteTime.Today))
            .ToListAsync();
    }

    public async Task<List<Installation>> GetWarrantyExpiringAsync(int withinDays = 90)
    {
        // Site-local dates, and skip rows with no recorded end date — those
        // sit at 0001-01-01 and would otherwise read as long expired.
        var today = SolarApp.Services.SiteTime.Today;
        var threshold = today.AddDays(withinDays);
        return await _context.Installations
            .Include(i => i.Customer)
            .Where(i => i.WarrantyExpiryDate.Year > 1900
                        && i.WarrantyExpiryDate <= threshold
                        && i.WarrantyExpiryDate >= today)
            .OrderBy(i => i.WarrantyExpiryDate)
            .ToListAsync();
    }
}
