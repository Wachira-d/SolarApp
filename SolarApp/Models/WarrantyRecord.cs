using SolarApp.Models.Enums;

namespace SolarApp.Models;

public class WarrantyRecord
{
    public int Id { get; set; }
    public int InstallationId { get; set; }
    public Installation Installation { get; set; } = null!;
    public int? EquipmentId { get; set; }
    public Equipment? Equipment { get; set; }

    public WarrantyType WarrantyType { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public DateTime WarrantyStartDate { get; set; }
    public DateTime WarrantyEndDate { get; set; }
    public string? TermsDescription { get; set; }
    public string? DocumentUrl { get; set; }

    public bool ClaimFiled { get; set; }
    public DateTime? ClaimDate { get; set; }
    public ClaimStatus? ClaimStatus { get; set; }
    public string? ClaimNotes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool IsExpired => DateTime.UtcNow > WarrantyEndDate;
    public int DaysRemaining => Math.Max(0, (WarrantyEndDate - DateTime.UtcNow).Days);
}
