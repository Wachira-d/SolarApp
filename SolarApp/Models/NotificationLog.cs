using SolarApp.Models.Enums;

namespace SolarApp.Models;

public class NotificationLog
{
    public long Id { get; set; }
    public string? RecipientUserId { get; set; }
    public ApplicationUser? RecipientUser { get; set; }
    public string? RecipientLineId { get; set; }

    public NotificationChannel Channel { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;

    public string? RelatedEntityType { get; set; }
    public string? RelatedEntityId { get; set; }

    public NotificationStatus Status { get; set; } = NotificationStatus.Pending;
    public string? ErrorMessage { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
