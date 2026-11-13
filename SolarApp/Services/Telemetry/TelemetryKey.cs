using System.Text.RegularExpressions;
using SolarApp.Models.Enums;

namespace SolarApp.Services.Telemetry;

/// <summary>What a channel key says about the value: device, numbering, quantity, unit.</summary>
public readonly record struct ParsedKey(
    TelemetryDeviceKind Kind, int DeviceIndex, int SubIndex,
    TelemetryQuantity Quantity, string? Unit, bool IsCumulative);

/// <summary>
/// The single parser for channel keys (DESIGN.md §3). Analytics find their
/// inputs by (Kind, Quantity, index), never by string matching on keys, so
/// this is the only place the naming convention lives.
/// </summary>
public static partial class TelemetryKey
{
    [GeneratedRegex(@"^[a-z0-9_]+(\.[a-z0-9_]+){0,5}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"^([a-z_]+?)(\d+)$")]
    private static partial Regex NumberedSegment();

    public const int MaxLength = 100;

    public static bool IsValid(string? key) =>
        !string.IsNullOrEmpty(key) && key.Length <= MaxLength && KeyPattern().IsMatch(key);

    /// <summary>
    /// "sensor.Battery SOC" → "ha.sensor_battery_soc": where unmapped Home
    /// Assistant entities are stored so nothing is lost before mapping.
    /// </summary>
    public static string ForUnmappedEntity(string entityId)
    {
        var s = Regex.Replace(entityId.Trim().ToLowerInvariant(), "[^a-z0-9_]+", "_").Trim('_');
        if (s.Length == 0) s = "unknown";
        var key = "ha." + s;
        return key.Length > MaxLength ? key[..MaxLength] : key;
    }

    /// <summary>
    /// Looks like an HA entity id rather than a canonical key: "sensor.x",
    /// "binary_sensor.y", "number.z" — the HA domain is the first segment.
    /// </summary>
    public static bool LooksLikeEntityId(string name)
    {
        var dot = name.IndexOf('.');
        if (dot <= 0) return false;
        var domain = name[..dot];
        return domain is "sensor" or "binary_sensor" or "number" or "input_number" or "switch"
            or "select" or "climate" or "weather" or "sun" or "counter" or "input_boolean" or "utility_meter";
    }

    public static ParsedKey Parse(string key)
    {
        var parts = key.Split('.');
        var root = parts[0];
        var last = parts[^1];
        var (quantity, unit, cumulative) = QuantityFromSuffix(last);

        switch (root)
        {
            case "pv":
            {
                if (parts.Length >= 3 && TrySplit(parts[1], out var name, out var n) && name == "string")
                    return new(TelemetryDeviceKind.PvString, n, 0, quantity, unit, cumulative);
                return new(TelemetryDeviceKind.Pv, 0, 0, quantity, unit, cumulative);
            }
            case "grid":
            {
                if (last == "status") return new(TelemetryDeviceKind.Grid, 0, 0, TelemetryQuantity.GridPresent, null, false);
                // grid.l1.voltage_v → phase number as the device index
                if (parts.Length >= 3 && TrySplit(parts[1], out var ph, out var pn) && ph == "l")
                    return new(TelemetryDeviceKind.Grid, pn, 0, quantity, unit, cumulative);
                return new(TelemetryDeviceKind.Grid, 0, 0, quantity, unit, cumulative);
            }
            case "load":
                return new(TelemetryDeviceKind.Load, 0, 0, quantity, unit, cumulative);
            case "env":
                return new(TelemetryDeviceKind.Environment, 0, 0, quantity, unit, cumulative);
            case "battery":
            {
                if (parts.Length >= 3 && TrySplit(parts[1], out var pk, out var packNo) && pk == "pack")
                {
                    if (parts.Length >= 4 && TrySplit(parts[2], out var ck, out var cellNo) && ck == "cell")
                        return new(TelemetryDeviceKind.BatteryCell, packNo, cellNo, quantity, unit, cumulative);
                    return new(TelemetryDeviceKind.BatteryPack, packNo, 0, quantity, unit, cumulative);
                }
                return new(TelemetryDeviceKind.Battery, 0, 0, quantity, unit, cumulative);
            }
        }

        // inverter1.power_w
        if (TrySplit(root, out var dev, out var idx) && dev == "inverter")
        {
            if (last == "status_code") return new(TelemetryDeviceKind.Inverter, idx, 0, TelemetryQuantity.Status, null, false);
            if (last == "alarm_code") return new(TelemetryDeviceKind.Inverter, idx, 0, TelemetryQuantity.AlarmCode, null, false);
            return new(TelemetryDeviceKind.Inverter, idx, 0, quantity, unit, cumulative);
        }
        if (root == "inverter")
            return new(TelemetryDeviceKind.Inverter, 1, 0, quantity, unit, cumulative);

        return new(TelemetryDeviceKind.Other, 0, 0, quantity, unit, cumulative);
    }

    private static bool TrySplit(string segment, out string name, out int number)
    {
        var m = NumberedSegment().Match(segment);
        if (m.Success && int.TryParse(m.Groups[2].Value, out number) && number is > 0 and < 1000)
        {
            name = m.Groups[1].Value;
            return true;
        }
        name = segment;
        number = 0;
        return false;
    }

    private static (TelemetryQuantity, string?, bool) QuantityFromSuffix(string last) => last switch
    {
        "cell_v_max" => (TelemetryQuantity.CellVoltageMax, "V", false),
        "cell_v_min" => (TelemetryQuantity.CellVoltageMin, "V", false),
        "cell_temp_max_c" => (TelemetryQuantity.CellTempMax, "°C", false),
        "cell_temp_min_c" => (TelemetryQuantity.CellTempMin, "°C", false),
        "soc_pct" => (TelemetryQuantity.Soc, "%", false),
        "soh_pct" => (TelemetryQuantity.Soh, "%", false),
        "status_code" => (TelemetryQuantity.Status, null, false),
        "alarm_code" => (TelemetryQuantity.AlarmCode, null, false),
        "lux" => (TelemetryQuantity.Illuminance, "lx", false),
        "irradiance_wm2" => (TelemetryQuantity.Irradiance, "W/m²", false),
        _ when last.EndsWith("_total_kwh") || last == "energy_total_kwh" => (TelemetryQuantity.Energy, "kWh", true),
        _ when last.EndsWith("_kwh") => (TelemetryQuantity.Energy, "kWh", false),
        _ when last.EndsWith("_w") => (TelemetryQuantity.Power, "W", false),
        _ when last.EndsWith("_v") => (TelemetryQuantity.Voltage, "V", false),
        _ when last.EndsWith("_a") => (TelemetryQuantity.Current, "A", false),
        _ when last.EndsWith("_c") => (TelemetryQuantity.Temperature, "°C", false),
        _ when last.EndsWith("_hz") => (TelemetryQuantity.Frequency, "Hz", false),
        _ => (TelemetryQuantity.Other, null, false),
    };
}
