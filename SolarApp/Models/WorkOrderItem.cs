using System.ComponentModel.DataAnnotations;

namespace SolarApp.Models;

/// <summary>
/// Checklist item within a WorkOrder. Technicians check items off as they
/// complete them. Supports photos per item and ordering.
/// </summary>
public class WorkOrderItem
{
    public int Id { get; set; }

    public int WorkOrderId { get; set; }
    public WorkOrder WorkOrder { get; set; } = null!;

    public int SortOrder { get; set; }

    [Required, StringLength(300)]
    public string Description { get; set; } = string.Empty;

    public bool IsCompleted { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? CompletedByUserId { get; set; }
    public ApplicationUser? CompletedByUser { get; set; }

    public string? Notes { get; set; }
    public string? Photos { get; set; }

    public List<string> PhotoList => string.IsNullOrWhiteSpace(Photos) ? new() :
        Photos.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
