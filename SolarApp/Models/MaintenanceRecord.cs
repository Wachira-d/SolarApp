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

    public string? Category { get; set; }             // e.g. "Inverter Issue", "Grid Connection", "Cleaning"
    public string? Location { get; set; }             // Where in the installation the work was done

    // Photos: comma-separated URLs OR JSON array (stored as text)
    public string? BeforePhotos { get; set; }
    public string? AfterPhotos { get; set; }
    public string? AttachmentUrls { get; set; }       // Document/PDF attachments (comma-separated)

    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Helpers - parse comma-separated photo URLs
    public IEnumerable<string> BeforePhotoList => ParseList(BeforePhotos);
    public IEnumerable<string> AfterPhotoList => ParseList(AfterPhotos);
    public IEnumerable<string> AttachmentList => ParseList(AttachmentUrls);

    private static IEnumerable<string> ParseList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Enumerable.Empty<string>();
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
