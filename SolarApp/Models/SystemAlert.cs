using SolarApp.Models.Enums;

namespace SolarApp.Models;

public class SystemAlert
{
    public int Id { get; set; }
    public int InstallationId { get; set; }
    public Installation Installation { get; set; } = null!;

    public AlertType AlertType { get; set; }
    public AlertSeverity Severity { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? RootCauseAnalysis { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
    public DateTime? AcknowledgedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }

    public string? AcknowledgedByUserId { get; set; }
    public ApplicationUser? AcknowledgedByUser { get; set; }

    public bool IsNotified { get; set; }
    public bool IsResolved => ResolvedAt.HasValue;
}
