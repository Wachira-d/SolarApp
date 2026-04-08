using SolarApp.Models.Enums;

namespace SolarApp.Models;

public class Installation
{
    public int Id { get; set; }
    public string SiteName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    // Owner & Partner
    public string CustomerId { get; set; } = string.Empty;
    public ApplicationUser Customer { get; set; } = null!;
    public string? PartnerId { get; set; }
    public ApplicationUser? Partner { get; set; }

    // Inverter Cloud connection
    public InverterBrand InverterBrand { get; set; }
    public string? CloudApiKey { get; set; }
    public string? CloudApiSecret { get; set; }
    public string? CloudUsername { get; set; }
    public string? CloudPassword { get; set; }
    public string? PlantId { get; set; }

    // System specifications
    public decimal SystemCapacityKw { get; set; }
    public int NumberOfPanels { get; set; }
    public int PanelWattage { get; set; }

    // Dates
    public DateTime InstallationDate { get; set; }
    public DateTime? CommissionDate { get; set; }

    // Warranty
    public int WarrantyYears { get; set; }
    public DateTime WarrantyExpiryDate { get; set; }

    // Panel Cleaning
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

    // Electricity rate for savings calculation (THB per kWh)
    public decimal ElectricityRatePerKwh { get; set; } = 4.0m;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public ICollection<Equipment> Equipment { get; set; } = new List<Equipment>();
    public ICollection<EnergyData> EnergyData { get; set; } = new List<EnergyData>();
    public ICollection<SystemAlert> Alerts { get; set; } = new List<SystemAlert>();
    public ICollection<MaintenanceRecord> MaintenanceRecords { get; set; } = new List<MaintenanceRecord>();
    public ICollection<ServiceTicket> ServiceTickets { get; set; } = new List<ServiceTicket>();
}
