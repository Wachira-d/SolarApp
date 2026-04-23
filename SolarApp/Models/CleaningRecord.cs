using SolarApp.Models.Enums;

namespace SolarApp.Models;

public class CleaningRecord
{
    public int Id { get; set; }
    public int InstallationId { get; set; }
    public Installation Installation { get; set; } = null!;

    public DateTime ScheduledDate { get; set; }
    public DateTime? ActualDate { get; set; }
    public CleaningStatus Status { get; set; } = CleaningStatus.Scheduled;

    public string? PerformedByUserId { get; set; }
    public ApplicationUser? PerformedByUser { get; set; }
    public string? PerformedByExternal { get; set; }

    // Single URLs (legacy) + multi-photo support (comma-separated)
    public string? BeforePhotoUrl { get; set; }
    public string? AfterPhotoUrl { get; set; }
    public string? BeforePhotos { get; set; }
    public string? AfterPhotos { get; set; }
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public IEnumerable<string> BeforePhotoList
    {
        get
        {
            var list = new List<string>();
            if (!string.IsNullOrWhiteSpace(BeforePhotoUrl)) list.Add(BeforePhotoUrl);
            if (!string.IsNullOrWhiteSpace(BeforePhotos))
                list.AddRange(BeforePhotos.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return list.Distinct();
        }
    }

    public IEnumerable<string> AfterPhotoList
    {
        get
        {
            var list = new List<string>();
            if (!string.IsNullOrWhiteSpace(AfterPhotoUrl)) list.Add(AfterPhotoUrl);
            if (!string.IsNullOrWhiteSpace(AfterPhotos))
                list.AddRange(AfterPhotos.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return list.Distinct();
        }
    }
}
