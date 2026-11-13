using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Models.Telemetry;

namespace SolarApp.Services.Telemetry;

/// <summary>
/// What a site actually has, so each report shows only what applies
/// (DESIGN.md §6). Telemetry channels are the evidence for gateway sites;
/// the installation's own fields stand in for cloud-only sites.
/// </summary>
public sealed record SiteCapabilities(
    bool HasPv, int StringCount, int InverterCount,
    bool HasBattery, int BatteryPackCount, bool HasCellData, bool HasBatteryTemps,
    bool HasGridMeter, bool HasGridVoltage, bool HasGridFrequency, bool HasGridStatus,
    bool HasLoad, bool HasInverterTemps, bool HasStatusCodes,
    bool HasLux, bool HasIrradiance, bool HasTelemetry)
{
    public static SiteCapabilities From(Installation inst, IReadOnlyCollection<TelemetryChannel> channels)
    {
        bool Any(Func<TelemetryChannel, bool> p) => channels.Any(p);

        var strings = channels.Where(c => c.Kind == TelemetryDeviceKind.PvString && c.DeviceIndex > 0)
            .Select(c => c.DeviceIndex).Distinct().Count();
        var inverters = channels.Where(c => c.Kind == TelemetryDeviceKind.Inverter && c.DeviceIndex > 0)
            .Select(c => c.DeviceIndex).Distinct().Count();
        var packs = channels.Where(c => (c.Kind == TelemetryDeviceKind.BatteryPack || c.Kind == TelemetryDeviceKind.BatteryCell)
                                        && c.DeviceIndex > 0)
            .Select(c => c.DeviceIndex).Distinct().Count();

        var hasBatteryChannels = Any(c => c.Kind is TelemetryDeviceKind.Battery or TelemetryDeviceKind.BatteryPack
                                          && c.Quantity is TelemetryQuantity.Soc or TelemetryQuantity.Power);
        // The installation's flag still gates: a hybrid inverter can report a
        // phantom 0 % SOC with no battery attached (see Installation.HasBattery).
        var hasBattery = inst.HasBattery && (hasBatteryChannels || channels.Count == 0);

        return new SiteCapabilities(
            HasPv: Any(c => c.Kind is TelemetryDeviceKind.Pv or TelemetryDeviceKind.PvString) || channels.Count == 0,
            StringCount: strings,
            InverterCount: inverters > 0 ? inverters : inst.InverterCount,
            HasBattery: hasBattery,
            BatteryPackCount: hasBattery ? (packs > 0 ? packs : Math.Max(1, inst.BatteryCount ?? 1)) : 0,
            HasCellData: hasBattery && Any(c => c.Quantity is TelemetryQuantity.CellVoltageMax or TelemetryQuantity.CellVoltageMin
                                                || c.Kind == TelemetryDeviceKind.BatteryCell),
            HasBatteryTemps: hasBattery && Any(c => c.Quantity is TelemetryQuantity.CellTempMax or TelemetryQuantity.CellTempMin
                                                   || (c.Kind is TelemetryDeviceKind.Battery or TelemetryDeviceKind.BatteryPack
                                                       && c.Quantity == TelemetryQuantity.Temperature)),
            HasGridMeter: Any(c => c.Kind == TelemetryDeviceKind.Grid && c.Quantity is TelemetryQuantity.Power or TelemetryQuantity.Energy),
            HasGridVoltage: Any(c => c.Kind == TelemetryDeviceKind.Grid && c.Quantity == TelemetryQuantity.Voltage),
            HasGridFrequency: Any(c => c.Kind == TelemetryDeviceKind.Grid && c.Quantity == TelemetryQuantity.Frequency),
            HasGridStatus: Any(c => c.Quantity == TelemetryQuantity.GridPresent),
            HasLoad: Any(c => c.Kind == TelemetryDeviceKind.Load),
            HasInverterTemps: Any(c => c.Kind == TelemetryDeviceKind.Inverter && c.Quantity == TelemetryQuantity.Temperature),
            HasStatusCodes: Any(c => c.Quantity is TelemetryQuantity.Status or TelemetryQuantity.AlarmCode),
            HasLux: Any(c => c.Quantity == TelemetryQuantity.Illuminance),
            HasIrradiance: Any(c => c.Quantity == TelemetryQuantity.Irradiance),
            HasTelemetry: channels.Count > 0);
    }
}
