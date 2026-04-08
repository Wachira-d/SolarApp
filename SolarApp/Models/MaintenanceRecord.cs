using SolarApp.Models.Enums;

namespace SolarApp.Models;

public class MaintenanceRecord
{
    public int Id { get; set; }
    public int InstallationId { get; set; }
    public Installation Installation { get; set; } = null!;

    public MaintenanceType MaintenanceType { get; set; }
    public MaintenanceStatus Status { get; set; } = MaintenanceStatus.Scheduled;

    public DateTime ScheduledDate { get; set; }
    public DateTime? CompletedDate { get; set; }

    public string? PerformedByUserId { get; set; }
    public ApplicationUser? PerformedByUser { get; set; }

    public string Description { get; set; } = string.Empty;
    public decimal? Cost { get; set; }

    public string? BeforePhotos { get; set; }
    public string? AfterPhotos { get; set; }
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
