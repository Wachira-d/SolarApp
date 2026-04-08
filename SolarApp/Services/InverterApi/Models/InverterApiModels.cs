namespace SolarApp.Services.InverterApi.Models;

public class ApiCredentials
{
    public string? ApiKey { get; set; }
    public string? ApiSecret { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? PlantId { get; set; }
}

public class PlantInfo
{
    public string PlantId { get; set; } = string.Empty;
    public string PlantName { get; set; } = string.Empty;
    public decimal CapacityKw { get; set; }
    public string? Address { get; set; }
    public DateTime? CreateDate { get; set; }
}

public class RealtimeData
{
    public DateTime Timestamp { get; set; }
    public decimal CurrentPowerKw { get; set; }
    public decimal TodayEnergyKwh { get; set; }
    public decimal TotalEnergyKwh { get; set; }
    public decimal? GridExportKw { get; set; }
    public decimal? GridImportKw { get; set; }
    public decimal? BatterySocPercent { get; set; }
    public decimal? BatteryPowerKw { get; set; }
    public bool IsOnline { get; set; }
}

public class EnergyRecord
{
    public DateTime Timestamp { get; set; }
    public decimal EnergyGenerationKwh { get; set; }
    public decimal GridExportKwh { get; set; }
    public decimal GridImportKwh { get; set; }
    public decimal SelfConsumptionKwh { get; set; }
    public decimal? BatteryChargeKwh { get; set; }
    public decimal? BatteryDischargeKwh { get; set; }
}

public class AlarmRecord
{
    public string AlarmId { get; set; } = string.Empty;
    public string AlarmName { get; set; } = string.Empty;
    public string? AlarmCode { get; set; }
    public int Severity { get; set; } // 1=Critical, 2=Major, 3=Minor, 4=Warning
    public DateTime OccurredAt { get; set; }
    public DateTime? ClearedAt { get; set; }
    public string? DeviceName { get; set; }
    public string? Description { get; set; }
}

public class DeviceInfo
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string DeviceType { get; set; } = string.Empty;
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public bool IsOnline { get; set; }
}
