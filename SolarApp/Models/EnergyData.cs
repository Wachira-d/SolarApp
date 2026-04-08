using SolarApp.Models.Enums;

namespace SolarApp.Models;

public class EnergyData
{
    public long Id { get; set; }
    public int InstallationId { get; set; }
    public Installation Installation { get; set; } = null!;

    public DateTime Timestamp { get; set; }
    public DataGranularity Granularity { get; set; }

    // Generation
    public decimal PowerGenerationKw { get; set; }
    public decimal EnergyGenerationKwh { get; set; }

    // Grid
    public decimal GridExportKwh { get; set; }
    public decimal GridImportKwh { get; set; }

    // Self consumption
    public decimal SelfConsumptionKwh { get; set; }

    // Battery
    public decimal? BatterySocPercent { get; set; }
    public decimal? BatteryChargeKwh { get; set; }
    public decimal? BatteryDischargeKwh { get; set; }
}
