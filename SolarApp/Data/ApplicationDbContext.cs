using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SolarApp.Models;

namespace SolarApp.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    public DbSet<Installation> Installations => Set<Installation>();
    public DbSet<Equipment> Equipment => Set<Equipment>();
    public DbSet<EnergyData> EnergyData => Set<EnergyData>();
    public DbSet<SystemAlert> SystemAlerts => Set<SystemAlert>();
    public DbSet<MaintenanceRecord> MaintenanceRecords => Set<MaintenanceRecord>();
    public DbSet<ServiceTicket> ServiceTickets => Set<ServiceTicket>();
    public DbSet<InverterApiLog> InverterApiLogs => Set<InverterApiLog>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Installation relationships
        builder.Entity<Installation>(entity =>
        {
            entity.HasOne(i => i.Customer)
                .WithMany(u => u.OwnedInstallations)
                .HasForeignKey(i => i.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(i => i.Partner)
                .WithMany(u => u.ManagedInstallations)
                .HasForeignKey(i => i.PartnerId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(i => i.CustomerId);
            entity.HasIndex(i => i.PartnerId);
            entity.HasIndex(i => i.Status);
            entity.HasIndex(i => i.InverterBrand);

            entity.Property(i => i.SystemCapacityKw).HasPrecision(10, 2);
            entity.Property(i => i.ElectricityRatePerKwh).HasPrecision(10, 2);
        });

        // EnergyData indexes for query performance
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

        // SystemAlert indexes
        builder.Entity<SystemAlert>(entity =>
        {
            entity.HasIndex(a => new { a.InstallationId, a.OccurredAt });
            entity.HasIndex(a => a.AlertType);
            entity.HasIndex(a => a.IsNotified);
        });

        // ServiceTicket
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
        });

        // MaintenanceRecord
        builder.Entity<MaintenanceRecord>(entity =>
        {
            entity.HasOne(m => m.PerformedByUser)
                .WithMany()
                .HasForeignKey(m => m.PerformedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.Property(m => m.Cost).HasPrecision(12, 2);
        });

        // InverterApiLog indexes
        builder.Entity<InverterApiLog>(entity =>
        {
            entity.HasIndex(l => new { l.InstallationId, l.RequestTime });
            entity.HasIndex(l => l.IsSuccess);
        });
    }
}
