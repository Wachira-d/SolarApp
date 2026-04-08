using SolarApp.Models.Enums;

namespace SolarApp.Models;

public class ServiceTicket
{
    public int Id { get; set; }
    public int InstallationId { get; set; }
    public Installation Installation { get; set; } = null!;

    public string TicketNumber { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public TicketPriority Priority { get; set; } = TicketPriority.Normal;
    public TicketStatus Status { get; set; } = TicketStatus.Open;

    public string ReportedByUserId { get; set; } = string.Empty;
    public ApplicationUser ReportedByUser { get; set; } = null!;

    public string? AssignedToUserId { get; set; }
    public ApplicationUser? AssignedToUser { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }

    public string? Resolution { get; set; }
    public string? Notes { get; set; }
}
