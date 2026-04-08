using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SolarApp.Models;

namespace SolarApp.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    // Core entities
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Partner> Partners => Set<Partner>();
    public DbSet<Installation> Installations => Set<Installation>();
    public DbSet<Equipment> Equipment => Set<Equipment>();
    public DbSet<EnergyData> EnergyData => Set<EnergyData>();
    public DbSet<SystemAlert> SystemAlerts => Set<SystemAlert>();
    public DbSet<MaintenanceRecord> MaintenanceRecords => Set<MaintenanceRecord>();
    public DbSet<ServiceTicket> ServiceTickets => Set<ServiceTicket>();
    public DbSet<InverterApiLog> InverterApiLogs => Set<InverterApiLog>();

    // New entities
    public DbSet<WarrantyRecord> WarrantyRecords => Set<WarrantyRecord>();
    public DbSet<CleaningRecord> CleaningRecords => Set<CleaningRecord>();
    public DbSet<InstallationDocument> InstallationDocuments => Set<InstallationDocument>();
    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ElectricityTariff> ElectricityTariffs => Set<ElectricityTariff>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // ── Customer ──
        builder.Entity<Customer>(entity =>
        {
            entity.HasOne(c => c.User)
                .WithMany()
                .HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(c => c.UserId).IsUnique()
                .HasFilter("[UserId] IS NOT NULL");
            entity.HasIndex(c => c.Email);
        });

        // ── Partner ──
        builder.Entity<Partner>(entity =>
        {
            entity.HasMany(p => p.Users)
                .WithOne(u => u.Partner)
                .HasForeignKey(u => u.PartnerId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.Property(p => p.CommissionRate).HasPrecision(5, 2);
        });

        // ── Installation ──
        builder.Entity<Installation>(entity =>
        {
            entity.HasOne(i => i.Customer)
                .WithMany(c => c.Installations)
                .HasForeignKey(i => i.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(i => i.Partner)
                .WithMany(p => p.Installations)
                .HasForeignKey(i => i.PartnerId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(i => i.CreatedByUser)
                .WithMany()
                .HasForeignKey(i => i.CreatedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(i => i.SiteCode).IsUnique();
            entity.HasIndex(i => i.CustomerId);
            entity.HasIndex(i => i.PartnerId);
            entity.HasIndex(i => i.Status);
            entity.HasIndex(i => i.InverterBrand);

            entity.Property(i => i.SystemCapacityKw).HasPrecision(10, 3);
            entity.Property(i => i.ElectricityRatePerKwh).HasPrecision(10, 4);
            entity.Property(i => i.FeedInTariffRate).HasPrecision(10, 4);
            entity.Property(i => i.ContractValue).HasPrecision(18, 2);
            entity.Property(i => i.BatteryCapacityKwh).HasPrecision(10, 3);

            entity.Property(i => i.SiteCode).HasMaxLength(50);
            entity.Property(i => i.SiteName).HasMaxLength(200);
            entity.Property(i => i.Address).HasMaxLength(500);
        });

        // ── EnergyData ──
        builder.Entity<EnergyData>(entity =>
        {
            entity.HasIndex(e => new { e.InstallationId, e.Timestamp, e.Granularity });
            entity.HasIndex(e => e.Timestamp);

            entity.Property(e => e.PowerGenerationKw).HasPrecision(12, 3);
            entity.Property(e => e.EnergyGenerationKwh).HasPrecision(12, 3);
            entity.Property(e => e.GridExportKwh).HasPrecision(12, 3);
            entity.Property(e => e.GridImportKwh).HasPrecision(12, 3);
            entity.Property(e => e.SelfConsumptionKwh).HasPrecision(12, 3);
            entity.Property(e => e.BatterySocPercent).HasPrecision(5, 2);
            entity.Property(e => e.BatteryChargeKwh).HasPrecision(12, 3);
            entity.Property(e => e.BatteryDischargeKwh).HasPrecision(12, 3);
        });

        // ── SystemAlert ──
        builder.Entity<SystemAlert>(entity =>
        {
            entity.HasIndex(a => new { a.InstallationId, a.OccurredAt });
            entity.HasIndex(a => a.AlertType);
            entity.HasIndex(a => a.IsNotified);
            entity.HasIndex(a => new { a.ResolvedAt }).HasFilter("[ResolvedAt] IS NULL");
        });

        // ── ServiceTicket ──
        builder.Entity<ServiceTicket>(entity =>
        {
            entity.HasIndex(t => t.TicketNumber).IsUnique();
            entity.HasIndex(t => t.Status);

            entity.HasOne(t => t.ReportedByUser)
                .WithMany()
                .HasForeignKey(t => t.ReportedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(t => t.AssignedToUser)
                .WithMany()
                .HasForeignKey(t => t.AssignedToUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.Property(t => t.TicketNumber).HasMaxLength(50);
        });

        // ── MaintenanceRecord ──
        builder.Entity<MaintenanceRecord>(entity =>
        {
            entity.HasOne(m => m.PerformedByUser)
                .WithMany()
                .HasForeignKey(m => m.PerformedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.Property(m => m.Cost).HasPrecision(12, 2);
        });

        // ── InverterApiLog ──
        builder.Entity<InverterApiLog>(entity =>
        {
            entity.HasIndex(l => new { l.InstallationId, l.RequestTime });
            entity.HasIndex(l => l.IsSuccess);
        });

        // ── WarrantyRecord ──
        builder.Entity<WarrantyRecord>(entity =>
        {
            entity.HasOne(w => w.Equipment)
                .WithMany()
                .HasForeignKey(w => w.EquipmentId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(w => new { w.InstallationId, w.WarrantyEndDate });
        });

        // ── CleaningRecord ──
        builder.Entity<CleaningRecord>(entity =>
        {
            entity.HasOne(c => c.PerformedByUser)
                .WithMany()
                .HasForeignKey(c => c.PerformedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(c => new { c.InstallationId, c.ScheduledDate });
        });

        // ── InstallationDocument ──
        builder.Entity<InstallationDocument>(entity =>
        {
            entity.HasOne(d => d.UploadedByUser)
                .WithMany()
                .HasForeignKey(d => d.UploadedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.Property(d => d.FilePath).HasMaxLength(500);
        });

        // ── NotificationLog ──
        builder.Entity<NotificationLog>(entity =>
        {
            entity.HasOne(n => n.RecipientUser)
                .WithMany()
                .HasForeignKey(n => n.RecipientUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(n => new { n.RecipientUserId, n.CreatedAt });
            entity.HasIndex(n => n.Status);
        });

        // ── AuditLog ──
        builder.Entity<AuditLog>(entity =>
        {
            entity.HasIndex(a => a.Timestamp);
            entity.HasIndex(a => new { a.EntityType, a.EntityId });
            entity.HasIndex(a => a.UserId);
        });

        // ── ElectricityTariff ──
        builder.Entity<ElectricityTariff>(entity =>
        {
            entity.Property(t => t.ImportRatePerKwh).HasPrecision(10, 4);
            entity.Property(t => t.ExportRatePerKwh).HasPrecision(10, 4);
            entity.Property(t => t.DemandChargePerKw).HasPrecision(10, 4);

            entity.HasIndex(t => new { t.InstallationId, t.EffectiveFrom });
        });

        // ── SystemSetting ──
        builder.Entity<SystemSetting>(entity =>
        {
            entity.HasIndex(s => s.Key).IsUnique();
            entity.Property(s => s.Key).HasMaxLength(200);
        });
    }
}
