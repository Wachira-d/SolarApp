using SolarApp.Models.Enums;

namespace SolarApp.Models.Telemetry;

/// <summary>
/// A device at a site that pushes data to us — normally one Home Assistant
/// instance. The token is shown once when created; only its SHA-256 is kept.
/// </summary>
public class TelemetryGateway
{
    public int Id { get; set; }
    public int InstallationId { get; set; }
    public Installation Installation { get; set; } = null!;

    public string Name { get; set; } = "Home Assistant";

    /// <summary>Hex SHA-256 of the full token.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>First characters of the token, for recognising it in lists ("sag_Ab12…").</summary>
    public string TokenPrefix { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedByUserId { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public string? LastSeenIp { get; set; }
    /// <summary>Samples accepted in total, for the operator's "is it working" glance.</summary>
    public long SamplesAccepted { get; set; }
    public DateTime? RevokedAt { get; set; }

    public bool IsActive => RevokedAt == null;
}

/// <summary>
/// One measured quantity at a site — "battery.pack2.cell_temp_max_c". Raw
/// rows reference the channel's int id, so the device, quantity and site are
/// stored once here instead of on every sample.
/// </summary>
public class TelemetryChannel
{
    public int Id { get; set; }
    public int InstallationId { get; set; }
    public Installation Installation { get; set; } = null!;

    /// <summary>Canonical key (see DESIGN.md §3) or "ha.&lt;entity&gt;" for unmapped entities.</summary>
    public string Key { get; set; } = string.Empty;

    public TelemetryDeviceKind Kind { get; set; }
    /// <summary>Device number from the key: 2 for "pv.string2.power_w", 0 when not numbered.</summary>
    public int DeviceIndex { get; set; }
    /// <summary>Second-level number: the cell in "battery.pack1.cell7.voltage_v".</summary>
    public int SubIndex { get; set; }
    public TelemetryQuantity Quantity { get; set; }
    public string? Unit { get; set; }
    /// <summary>Monotonic counter (energy totals). Rollups use last − first, bridging resets.</summary>
    public bool IsCumulative { get; set; }

    /// <summary>HA entity this channel was first seen from, if any.</summary>
    public string? SourceEntityId { get; set; }

    public DateTime FirstTs { get; set; }
    public DateTime LastTs { get; set; }
    public double LastValue { get; set; }
}

/// <summary>Maps a Home Assistant entity id to a canonical key, with a linear fix-up.</summary>
public class TelemetryEntityMap
{
    public int Id { get; set; }
    public int InstallationId { get; set; }
    public Installation Installation { get; set; } = null!;

    public string EntityId { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    /// <summary>value' = (value × Scale + Offset) × (Invert ? −1 : 1). Scale 0.001 turns W into kW, etc.</summary>
    public double Scale { get; set; } = 1;
    public double Offset { get; set; }
    public bool Invert { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A span of time whose rollups must be recomputed because data arrived late.</summary>
public class TelemetryDirtyRange
{
    public long Id { get; set; }
    public int InstallationId { get; set; }
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Per-site rollup progress: everything before WatermarkUtc is rolled up to 5 minutes.</summary>
public class TelemetryRollupState
{
    public int InstallationId { get; set; }
    public DateTime WatermarkUtc { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A status change or alarm reported by equipment, decoded to text.</summary>
public class DeviceEvent
{
    public long Id { get; set; }
    public int InstallationId { get; set; }
    public Installation Installation { get; set; } = null!;

    public DateTime TsUtc { get; set; }
    /// <summary>"inverter1", "battery.pack2", "bms"…</summary>
    public string Source { get; set; } = string.Empty;
    /// <summary>"status" or "alarm".</summary>
    public string EventKind { get; set; } = "status";
    public long Code { get; set; }
    public string Decoded { get; set; } = string.Empty;
    public AlertSeverity Severity { get; set; } = AlertSeverity.Low;
    public string? Message { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Computed analytics for one site and day, as JSON. One row per
/// (site, date, kind): "battery", "pv", "economics", "quality", "pv-model".
/// JSON keeps the table stable while reports grow.
/// </summary>
public class SiteAnalyticsDaily
{
    public long Id { get; set; }
    public int InstallationId { get; set; }
    public DateTime Date { get; set; }   // site-local date
    public string Kind { get; set; } = string.Empty;
    public string Json { get; set; } = "{}";
    public DateTime ComputedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// An electricity plan for a site — the one on the meter now, or an
/// alternative to compare against. Rates live in ConfigJson so Flat,
/// Progressive and TOU share one table (see TariffConfig).
/// </summary>
public class TariffPlan
{
    public int Id { get; set; }
    public int InstallationId { get; set; }
    public Installation Installation { get; set; } = null!;

    public string Name { get; set; } = string.Empty;
    public TariffKind Kind { get; set; }
    /// <summary>The plan the meter is actually on. Exactly one per site should be current.</summary>
    public bool IsCurrent { get; set; }
    public string ConfigJson { get; set; } = "{}";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
