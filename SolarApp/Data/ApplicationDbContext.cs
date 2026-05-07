using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SolarApp.Models;

namespace SolarApp.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Partner> Partners => Set<Partner>();
    public DbSet<Installation> Installations => Set<Installation>();
    public DbSet<Equipment> Equipment => Set<Equipment>();
    public DbSet<EnergyData> EnergyData => Set<EnergyData>();
    public DbSet<SystemAlert> SystemAlerts => Set<SystemAlert>();
    public DbSet<MaintenanceRecord> MaintenanceRecords => Set<MaintenanceRecord>();
    public DbSet<ServiceTicket> ServiceTickets => Set<ServiceTicket>();
    public DbSet<InverterApiLog> InverterApiLogs => Set<InverterApiLog>();
    public DbSet<WarrantyRecord> WarrantyRecords => Set<WarrantyRecord>();
    public DbSet<CleaningRecord> CleaningRecords => Set<CleaningRecord>();
    public DbSet<InstallationDocument> InstallationDocuments => Set<InstallationDocument>();
    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ElectricityTariff> ElectricityTariffs => Set<ElectricityTariff>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<AlertRule> AlertRules => Set<AlertRule>();
    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();
    public DbSet<WorkOrderItem> WorkOrderItems => Set<WorkOrderItem>();
    public DbSet<WorkOrderPhoto> WorkOrderPhotos => Set<WorkOrderPhoto>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // ══════════════════════════════════════════════════════════
        // SQL Server does NOT allow multiple cascade paths.
        // All FK relationships use Restrict (no cascade) to avoid
        // "may cause cycles or multiple cascade paths" errors.
        // Application code handles deletions explicitly.
        // ══════════════════════════════════════════════════════════

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
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(i => i.CreatedByUser)
                .WithMany()
                .HasForeignKey(i => i.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(i => i.SiteCode).IsUnique();
            entity.HasIndex(i => i.CustomerId);
            entity.HasIndex(i => i.PartnerId);
            entity.HasIndex(i => i.Status);

            entity.Property(i => i.SystemCapacityKw).HasPrecision(10, 3);
            entity.Property(i => i.InverterSizeKw).HasPrecision(10, 3);
            entity.Ignore(i => i.TotalInverterCapacityKw);
            entity.Property(i => i.ElectricityRatePerKwh).HasPrecision(10, 4);
            entity.Property(i => i.FeedInTariffRate).HasPrecision(10, 4);
            entity.Property(i => i.ContractValue).HasPrecision(18, 2);
            entity.Property(i => i.BatteryCapacityKwh).HasPrecision(10, 3);
            entity.Property(i => i.SiteCode).HasMaxLength(50);
            entity.Property(i => i.SiteName).HasMaxLength(200);
            entity.Property(i => i.Address).HasMaxLength(500);
        });

        // ── Equipment ──
        builder.Entity<Equipment>(entity =>
        {
            entity.HasOne(e => e.Installation)
                .WithMany(i => i.Equipment)
                .HasForeignKey(e => e.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.Property(e => e.RatingKw).HasPrecision(10, 3);
        });

        // ── EnergyData ──
        builder.Entity<EnergyData>(entity =>
        {
            entity.HasOne(e => e.Installation)
                .WithMany(i => i.EnergyData)
                .HasForeignKey(e => e.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);

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
            entity.HasOne(a => a.Installation)
                .WithMany(i => i.Alerts)
                .HasForeignKey(a => a.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(a => a.AcknowledgedByUser)
                .WithMany()
                .HasForeignKey(a => a.AcknowledgedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(a => new { a.InstallationId, a.OccurredAt });
            entity.HasIndex(a => a.AlertType);
            entity.HasIndex(a => a.IsNotified);
            entity.HasIndex(a => a.AcknowledgedByUserId);
            entity.HasIndex(a => a.Severity);
        });

        // ── ServiceTicket ──
        builder.Entity<ServiceTicket>(entity =>
        {
            entity.HasOne(t => t.Installation)
                .WithMany(i => i.ServiceTickets)
                .HasForeignKey(t => t.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(t => t.ReportedByUser)
                .WithMany()
                .HasForeignKey(t => t.ReportedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(t => t.AssignedToUser)
                .WithMany()
                .HasForeignKey(t => t.AssignedToUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(t => t.TicketNumber).IsUnique();
            entity.HasIndex(t => t.Status);
            entity.HasIndex(t => t.ReportedByUserId);
            entity.HasIndex(t => t.AssignedToUserId);
            entity.Property(t => t.TicketNumber).HasMaxLength(50);
        });

        // ── MaintenanceRecord ──
        builder.Entity<MaintenanceRecord>(entity =>
        {
            entity.HasOne(m => m.Installation)
                .WithMany(i => i.MaintenanceRecords)
                .HasForeignKey(m => m.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(m => m.PerformedByUser)
                .WithMany()
                .HasForeignKey(m => m.PerformedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.Property(m => m.Cost).HasPrecision(12, 2);
            entity.HasIndex(m => new { m.InstallationId, m.ScheduledDate });
            entity.HasIndex(m => m.PerformedByUserId);
            entity.HasIndex(m => m.Status);
        });

        // ── InverterApiLog ──
        builder.Entity<InverterApiLog>(entity =>
        {
            entity.HasOne(l => l.Installation)
                .WithMany()
                .HasForeignKey(l => l.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(l => new { l.InstallationId, l.RequestTime });
            entity.HasIndex(l => l.IsSuccess);
        });

        // ── WarrantyRecord ──
        builder.Entity<WarrantyRecord>(entity =>
        {
            entity.HasOne(w => w.Installation)
                .WithMany(i => i.WarrantyRecords)
                .HasForeignKey(w => w.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(w => w.Equipment)
                .WithMany()
                .HasForeignKey(w => w.EquipmentId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(w => new { w.InstallationId, w.WarrantyEndDate });
            entity.HasIndex(w => w.EquipmentId);
            entity.HasIndex(w => w.WarrantyType);
        });

        // ── CleaningRecord ──
        builder.Entity<CleaningRecord>(entity =>
        {
            entity.HasOne(c => c.Installation)
                .WithMany(i => i.CleaningRecords)
                .HasForeignKey(c => c.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(c => c.PerformedByUser)
                .WithMany()
                .HasForeignKey(c => c.PerformedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(c => new { c.InstallationId, c.ScheduledDate });
            entity.HasIndex(c => c.PerformedByUserId);
            entity.HasIndex(c => c.Status);
        });

        // ── InstallationDocument ──
        builder.Entity<InstallationDocument>(entity =>
        {
            entity.HasOne(d => d.Installation)
                .WithMany(i => i.Documents)
                .HasForeignKey(d => d.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(d => d.UploadedByUser)
                .WithMany()
                .HasForeignKey(d => d.UploadedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.Property(d => d.FilePath).HasMaxLength(500);
        });

        // ── ElectricityTariff ──
        builder.Entity<ElectricityTariff>(entity =>
        {
            entity.HasOne(t => t.Installation)
                .WithMany(i => i.Tariffs)
                .HasForeignKey(t => t.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.Property(t => t.ImportRatePerKwh).HasPrecision(10, 4);
            entity.Property(t => t.ExportRatePerKwh).HasPrecision(10, 4);
            entity.Property(t => t.DemandChargePerKw).HasPrecision(10, 4);
            entity.HasIndex(t => new { t.InstallationId, t.EffectiveFrom });
        });

        // ── NotificationLog ──
        builder.Entity<NotificationLog>(entity =>
        {
            entity.HasOne(n => n.RecipientUser)
                .WithMany()
                .HasForeignKey(n => n.RecipientUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(n => new { n.RecipientUserId, n.CreatedAt });
            entity.HasIndex(n => n.Status);
        });

        // ── AuditLog (no FK) ──
        builder.Entity<AuditLog>(entity =>
        {
            entity.HasIndex(a => a.Timestamp);
            entity.HasIndex(a => new { a.EntityType, a.EntityId });
            entity.HasIndex(a => a.UserId);
        });

        // ── SystemSetting ──
        builder.Entity<SystemSetting>(entity =>
        {
            entity.HasIndex(s => s.Key).IsUnique();
            entity.Property(s => s.Key).HasMaxLength(200);
        });

        // ── AlertRule ──
        builder.Entity<AlertRule>(entity =>
        {
            entity.HasOne(r => r.Installation)
                .WithMany()
                .HasForeignKey(r => r.InstallationId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(r => r.CreatedByUser)
                .WithMany()
                .HasForeignKey(r => r.CreatedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.Property(r => r.Threshold).HasPrecision(14, 4);
            entity.HasIndex(r => r.IsEnabled);
            entity.HasIndex(r => r.InstallationId);
            entity.HasIndex(r => r.Metric);
        });

        // ── WorkOrder ──
        builder.Entity<WorkOrder>(entity =>
        {
            entity.HasOne(w => w.Installation)
                .WithMany()
                .HasForeignKey(w => w.InstallationId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(w => w.Customer)
                .WithMany()
                .HasForeignKey(w => w.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(w => w.AssignedToUser)
                .WithMany()
                .HasForeignKey(w => w.AssignedToUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(w => w.CreatedByUser)
                .WithMany()
                .HasForeignKey(w => w.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(w => w.ParentWorkOrder)
                .WithMany()
                .HasForeignKey(w => w.ParentWorkOrderId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(w => w.ServiceTicket)
                .WithMany()
                .HasForeignKey(w => w.ServiceTicketId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(w => w.WorkOrderNumber).IsUnique();
            entity.HasIndex(w => w.Status);
            entity.HasIndex(w => w.ScheduledDate);
            entity.HasIndex(w => w.AssignedToUserId);
            entity.HasIndex(w => w.Type);
            entity.HasIndex(w => w.InstallationId);
            entity.HasIndex(w => w.CustomerId);

            entity.Property(w => w.WorkOrderNumber).HasMaxLength(20);
            entity.Property(w => w.LaborCost).HasPrecision(12, 2);
            entity.Property(w => w.MaterialCost).HasPrecision(12, 2);
            entity.Property(w => w.TravelCost).HasPrecision(12, 2);
            entity.Property(w => w.OtherCost).HasPrecision(12, 2);
        });

        // ── WorkOrderItem (checklist) ──
        builder.Entity<WorkOrderItem>(entity =>
        {
            entity.HasOne(i => i.WorkOrder)
                .WithMany(w => w.ChecklistItems)
                .HasForeignKey(i => i.WorkOrderId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(i => i.CompletedByUser)
                .WithMany()
                .HasForeignKey(i => i.CompletedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(i => new { i.WorkOrderId, i.SortOrder });
        });

        // ── WorkOrderPhoto ──
        builder.Entity<WorkOrderPhoto>(entity =>
        {
            entity.HasOne(p => p.WorkOrder)
                .WithMany(w => w.Photos)
                .HasForeignKey(p => p.WorkOrderId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(p => p.UploadedByUser)
                .WithMany()
                .HasForeignKey(p => p.UploadedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(p => new { p.WorkOrderId, p.Category, p.Timing });
        });
    }
}
