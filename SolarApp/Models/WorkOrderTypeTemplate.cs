using System.ComponentModel.DataAnnotations;
using SolarApp.Models.Enums;

namespace SolarApp.Models;

/// <summary>
/// Per-WorkOrderType default Title and Checklist that the WorkOrders/Create page
/// pre-populates when a tech picks that type. Admin-editable so each company can
/// tailor their checklist without code changes.
/// </summary>
public class WorkOrderTypeTemplate
{
    public int Id { get; set; }

    /// <summary>The WorkOrderType this template applies to. One row per type.</summary>
    public WorkOrderType Type { get; set; }

    [StringLength(200)]
    public string Title { get; set; } = string.Empty;

    /// <summary>One checklist item per line.</summary>
    public string ChecklistItems { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? UpdatedByUserId { get; set; }
}
