namespace SolarApp.Models;

public class ElectricityTariff
{
    public int Id { get; set; }
    public int InstallationId { get; set; }
    public Installation Installation { get; set; } = null!;

    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }

    public decimal ImportRatePerKwh { get; set; }
    public decimal? ExportRatePerKwh { get; set; }
    public decimal? DemandChargePerKw { get; set; }

    public string Currency { get; set; } = "THB";
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public bool IsCurrent => EffectiveFrom <= DateTime.UtcNow && (EffectiveTo == null || EffectiveTo > DateTime.UtcNow);
}
