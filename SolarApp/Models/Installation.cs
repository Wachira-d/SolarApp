using SolarApp.Models.Enums;

namespace SolarApp.Models;

public class Installation
{
    public int Id { get; set; }
    public string SiteCode { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string? City { get; set; }
    public string? Province { get; set; }
    public string? PostalCode { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    // Customer & Partner (separate entities)
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public int? PartnerId { get; set; }
    public Partner? Partner { get; set; }

    // Inverter Cloud connection
    public InverterBrand InverterBrand { get; set; }
    public string? InverterModel { get; set; }
    public int InverterCount { get; set; } = 1;
    public decimal InverterSizeKw { get; set; }       // กำลังของ inverter แต่ละตัว (kW)
    public decimal TotalInverterCapacityKw => InverterSizeKw * InverterCount;
    public string? CloudApiKey { get; set; }
    public string? CloudApiSecret { get; set; }
    public string? CloudUsername { get; set; }
    public string? CloudPassword { get; set; }
    public string? PlantId { get; set; }

    // System specifications
    public SystemType SystemType { get; set; } = SystemType.OnGrid;
    public decimal SystemCapacityKw { get; set; }
    public int NumberOfPanels { get; set; }
    public int PanelWattage { get; set; }
    public string? PanelBrand { get; set; }
    public string? PanelModel { get; set; }
    public string? BatteryBrand { get; set; }
    public string? BatteryModel { get; set; }
    public decimal? BatteryCapacityKwh { get; set; }
    public int? BatteryCount { get; set; }

    // Dates
    public DateTime InstallationDate { get; set; }
    public DateTime? CommissionDate { get; set; }

    // Warranty
    public int WarrantyYears { get; set; }
    public DateTime WarrantyExpiryDate { get; set; }
    public int InverterWarrantyYears { get; set; }
    public DateTime? InverterWarrantyExpiryDate { get; set; }

    // Panel Cleaning
    public int CleaningFrequencyMonths { get; set; } = 6;
    public int TotalCleaningCount { get; set; }
    public int CompletedCleaningCount { get; set; }
    public DateTime? LastCleaningDate { get; set; }
    public DateTime? NextCleaningDueDate { get; set; }

    public int RemainingCleanings => TotalCleaningCount - CompletedCleaningCount;
    public bool IsWarrantyExpired => DateTime.UtcNow > WarrantyExpiryDate;
    public bool IsCleaningDue => NextCleaningDueDate.HasValue && NextCleaningDueDate.Value <= DateTime.UtcNow;

    // Status
    public InstallationStatus Status { get; set; } = InstallationStatus.Active;
    public DateTime? LastOnlineAt { get; set; }
    public string? Notes { get; set; }

    // Financial
    public decimal? ContractValue { get; set; }
    public decimal ElectricityRatePerKwh { get; set; } = 4.0m;
    public decimal? FeedInTariffRate { get; set; }

    // Audit
    public string? CreatedByUserId { get; set; }
    public ApplicationUser? CreatedByUser { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public ICollection<Equipment> Equipment { get; set; } = new List<Equipment>();
    public ICollection<EnergyData> EnergyData { get; set; } = new List<EnergyData>();
    public ICollection<SystemAlert> Alerts { get; set; } = new List<SystemAlert>();
    public ICollection<MaintenanceRecord> MaintenanceRecords { get; set; } = new List<MaintenanceRecord>();
    public ICollection<ServiceTicket> ServiceTickets { get; set; } = new List<ServiceTicket>();
    public ICollection<WarrantyRecord> WarrantyRecords { get; set; } = new List<WarrantyRecord>();
    public ICollection<CleaningRecord> CleaningRecords { get; set; } = new List<CleaningRecord>();
    public ICollection<InstallationDocument> Documents { get; set; } = new List<InstallationDocument>();
    public ICollection<ElectricityTariff> Tariffs { get; set; } = new List<ElectricityTariff>();
}
