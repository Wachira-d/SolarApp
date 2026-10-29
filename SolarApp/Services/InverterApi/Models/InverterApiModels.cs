namespace SolarApp.Services.InverterApi.Models;

public class ApiCredentials
{
    public string? ApiKey { get; set; }
    public string? ApiSecret { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? PlantId { get; set; }

    /// <summary>
    /// Optional override of the inverter cloud API base URL. When set, the
    /// adapter should try this endpoint FIRST and skip region auto-detection.
    /// See Installation.CloudApiBaseUrl for the user-facing documentation.
    /// </summary>
    public string? ApiBaseUrl { get; set; }

    /// <summary>
    /// Operator-selected connection method (Official HMAC API vs.
    /// Username/Password web login).  When set, adapters that support
    /// multiple modes (e.g. Solis) MUST honor this instead of auto-
    /// detecting from which fields are populated — auto-detect produces
    /// "API Key required" errors when a previously-saved ApiKey lingers
    /// in storage but the operator now wants to use web-login.
    /// Null = adapter falls back to its legacy auto-detect.
    /// </summary>
    public SolarApp.Models.Enums.ConnectionMethod? ConnectionMethod { get; set; }
}

public class PlantInfo
{
    public string PlantId { get; set; } = string.Empty;
    public string PlantName { get; set; } = string.Empty;
    public decimal CapacityKw { get; set; }
    public string? Address { get; set; }
    public DateTime? CreateDate { get; set; }
    public DateTime? InstallDate { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
}

public enum HealthState
{
    Unknown = 0,
    Healthy = 1,
    Faulty = 2,        // operating but reporting a fault (Grid Loss, OverTemp, etc.)
    Disconnected = 3,  // not communicating with cloud
    OfflineNight = 4   // expected offline (sundown / sleep mode)
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

    /// <summary>True when the station is producing or in a normal standby (not faulty / disconnected).</summary>
    public bool IsOnline { get; set; }

    /// <summary>Brand-normalised station health. Faulty stations are still "online" comms-wise but require attention.</summary>
    public HealthState Health { get; set; } = HealthState.Unknown;

    /// <summary>Free-text reason when Health = Faulty (e.g. "Grid Loss"). Populated from station-level KPI or alarms.</summary>
    public string? FaultReason { get; set; }
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

    /// <summary>
    /// Never-empty label for this alarm. Every adapter falls back to "" when the
    /// vendor JSON doesn't carry the name field, and storing that straight
    /// through produced dashboard rows that were a severity badge and nothing
    /// else — the operator could see something was wrong but not what, and
    /// couldn't look it up in the vendor portal either. Falls back through the
    /// other identifying fields so the row always says something actionable.
    /// </summary>
    public string DisplayTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(AlarmName)) return AlarmName.Trim();
            if (!string.IsNullOrWhiteSpace(Description)) return Description.Trim();

            var code = !string.IsNullOrWhiteSpace(AlarmCode) ? AlarmCode!.Trim()
                     : !string.IsNullOrWhiteSpace(AlarmId) ? AlarmId.Trim()
                     : null;
            var device = !string.IsNullOrWhiteSpace(DeviceName) ? DeviceName!.Trim() : null;

            return (code, device) switch
            {
                (not null, not null) => $"แจ้งเตือนจากอินเวอร์เตอร์ {device} (รหัส {code})",
                (not null, null)     => $"แจ้งเตือนจากอินเวอร์เตอร์ (รหัส {code})",
                (null, not null)     => $"แจ้งเตือนจากอินเวอร์เตอร์ {device} — ผู้ผลิตไม่ได้ส่งชื่อ alarm มา",
                _                    => "แจ้งเตือนจากผู้ผลิต — ไม่ได้ส่งรายละเอียดมา"
            };
        }
    }

    /// <summary>
    /// Body text for the stored alert. The old format interpolated the raw
    /// fields, so an unnamed alarm with no description rendered as the literal
    /// string " (Device: N/A)" — blank title, blank body, nothing to act on.
    /// </summary>
    public string DisplayMessage
    {
        get
        {
            var parts = new List<string>();

            // What the vendor's English name means, in Thai — the part the
            // reader actually needed and the vendor never sends.
            if (SolarApp.Services.InverterApi.Common.VendorAlarmGlossary.Lookup(DisplayTitle) is { } meaning)
                parts.Add($"{(meaning.SafetyCritical ? "⚠ " : "")}{meaning.Thai} — {meaning.Meaning}");

            if (!string.IsNullOrWhiteSpace(Description) && Description!.Trim() != DisplayTitle)
                parts.Add(Description.Trim());

            if (!string.IsNullOrWhiteSpace(DeviceName))
                parts.Add($"อุปกรณ์: {DeviceName!.Trim()}");

            var code = !string.IsNullOrWhiteSpace(AlarmCode) ? AlarmCode!.Trim()
                     : !string.IsNullOrWhiteSpace(AlarmId) ? AlarmId.Trim()
                     : null;
            if (code != null)
                parts.Add($"รหัส alarm: {code}");

            // Say so explicitly rather than leaving a blank body — "the vendor
            // sent nothing" is itself the thing the operator needs to know,
            // because it means the details have to come from the vendor portal.
            if (parts.Count == 0)
                return "ผู้ผลิตส่งการแจ้งเตือนมาโดยไม่มีรายละเอียด — ตรวจสอบ alarm list ในพอร์ทัลของผู้ผลิตโดยตรง";

            return string.Join(" · ", parts);
        }
    }
}

public class DeviceInfo
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string DeviceType { get; set; } = string.Empty;
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public bool IsOnline { get; set; }
    public string? FirmwareVersion { get; set; }
    public DateTime? InstallDate { get; set; }
    public decimal? RatedPowerKw { get; set; }
}
