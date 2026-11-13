namespace SolarApp.Models.Enums;

/// <summary>
/// Where a site's live data comes from. Independent of ConnectionMethod,
/// which only describes how we authenticate to a vendor cloud.
/// </summary>
public enum TelemetrySource
{
    /// <summary>Poll the vendor cloud (the original behaviour).</summary>
    Cloud = 0,

    /// <summary>
    /// The site pushes to us — Home Assistant reading the equipment over
    /// Modbus TCP and posting to /api/ingest. No cloud polling.
    /// </summary>
    LocalGateway = 1,

    /// <summary>
    /// Both: the gateway is the live source; the cloud is still polled for
    /// vendor alarms and as a fallback when the gateway goes quiet.
    /// </summary>
    Hybrid = 2,
}

/// <summary>Which piece of equipment a channel belongs to. Parsed from the channel key.</summary>
public enum TelemetryDeviceKind
{
    Other = 0,
    Pv = 1,
    PvString = 2,
    Inverter = 3,
    Grid = 4,
    Load = 5,
    Battery = 6,
    BatteryPack = 7,
    BatteryCell = 8,
    Environment = 9,
}

/// <summary>What a channel measures. Parsed from the channel key.</summary>
public enum TelemetryQuantity
{
    Other = 0,
    Power = 1,
    Energy = 2,
    Voltage = 3,
    Current = 4,
    Temperature = 5,
    Soc = 6,
    Soh = 7,
    Frequency = 8,
    Status = 9,
    AlarmCode = 10,
    Illuminance = 11,
    Irradiance = 12,
    CellVoltageMax = 13,
    CellVoltageMin = 14,
    CellTempMax = 15,
    CellTempMin = 16,
    GridPresent = 17,
}

/// <summary>Rollup resolution of a stored aggregate.</summary>
public enum AggregateResolution : byte
{
    FiveMinutes = 1,
    Hour = 2,
    Day = 3,
}

/// <summary>How an electricity plan prices imported energy.</summary>
public enum TariffKind
{
    /// <summary>One rate for every kWh.</summary>
    Flat = 0,
    /// <summary>Tiered by monthly consumption (Thai residential 1.1.x).</summary>
    Progressive = 1,
    /// <summary>Time of use: peak / off-peak by hour and day type.</summary>
    TimeOfUse = 2,
}
