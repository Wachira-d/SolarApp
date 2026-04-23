using SolarApp.Models.Enums;

namespace SolarApp.Models;

public class Equipment
{
    public int Id { get; set; }
    public int InstallationId { get; set; }
    public Installation Installation { get; set; } = null!;

    public EquipmentType EquipmentType { get; set; }
    public string Brand { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string? SerialNumber { get; set; }
    public int Quantity { get; set; } = 1;

    // Power/Energy rating per unit:
    // - Inverter: rated power in kW (e.g. 5, 10, 15 kW)
    // - Panel: wattage per panel in watts (e.g. 400, 450, 550 W)
    // - Battery: capacity in kWh (e.g. 5.12, 10.24 kWh)
    public decimal? RatingKw { get; set; }
    public int? RatingWatts { get; set; }

    public int WarrantyYears { get; set; }
    public DateTime? WarrantyExpiryDate { get; set; }
    public DateTime InstalledDate { get; set; }

    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }

    public bool IsWarrantyExpired => WarrantyExpiryDate.HasValue && DateTime.UtcNow > WarrantyExpiryDate.Value;
}
