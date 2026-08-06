using SolarApp.Models.Enums;

namespace SolarApp.Services.InverterApi.Common;

/// <summary>
/// Per-brand metadata: which connection methods are supported, what the
/// vendor's published rate limit is, and the resulting minimum safe sync
/// interval.  Single source of truth so UI dropdowns, server-side
/// validation and the sync scheduler all agree.
///
/// Sources for the rate-limit figures are documented next to each entry;
/// vendor docs / observed behaviour as of 2026-05.  When a vendor changes
/// their published limits, edit here only.
/// </summary>
public static class BrandCapabilities
{
    /// <summary>Sync-interval presets exposed in the UI dropdown (minutes).</summary>
    public static readonly int[] AllowedIntervalMinutes =
        { 5, 15, 30, 60, 120, 360, 720, 1440 };

    private static readonly Dictionary<InverterBrand, BrandCapability> _map = new()
    {
        // Hoymiles S-Miles Cloud — inverter reports every 5 min, web UI polls
        // every 5 min, no documented per-key rate limit but 5-min sync covers
        // every fresh data point.
        [InverterBrand.Hoymiles] = new BrandCapability(
            SupportedMethods: new[] { ConnectionMethod.UserPassword },
            MinIntervalMinutes: 5,
            DefaultIntervalMinutes: 15,
            Notes: "Username = S-Miles email, Password = S-Miles password. No API-key option from the vendor."),

        // Huawei FusionSolar Northbound — 1 req/minute HARD limit per kkid;
        // a single sync round needs 3-5 endpoints so 5 min is the
        // tightest safe interval.  10-15 min is what we recommend.
        [InverterBrand.Huawei] = new BrandCapability(
            SupportedMethods: new[] { ConnectionMethod.OfficialApi },
            MinIntervalMinutes: 5,
            DefaultIntervalMinutes: 15,
            Notes: "Northbound API key only (no user/password). Hard limit: 1 req/min per kkid."),

        // Solis SolisCloud — HMAC API documented 3 req/5 sec, 100 req/min.
        // Web-login (no key) supported but breaks whenever Solis updates
        // the portal — see SolisAdapter for details.
        [InverterBrand.Solis] = new BrandCapability(
            SupportedMethods: new[] { ConnectionMethod.OfficialApi, ConnectionMethod.UserPassword },
            MinIntervalMinutes: 5,
            DefaultIntervalMinutes: 15,
            Notes: "Prefer HMAC API (Key+Secret from SolisCloud → API Management). Web-login mode supported but fragile."),

        // GoodWe SEMS portal — no public API, reverse-portal login only.
        // Anecdotal rate limit ~1 req/30 sec; 5 min is comfortable.
        [InverterBrand.GoodWe] = new BrandCapability(
            SupportedMethods: new[] { ConnectionMethod.UserPassword },
            MinIntervalMinutes: 5,
            DefaultIntervalMinutes: 15,
            Notes: "GoodWe has no public API. Username/Password = SEMS portal credentials."),

        // Deye / Solarman OpenAPI — docs say 10 req/min.  5 min interval
        // never exceeds budget for typical 5-call sync round.
        [InverterBrand.Deye] = new BrandCapability(
            SupportedMethods: new[] { ConnectionMethod.OfficialApi },
            MinIntervalMinutes: 5,
            DefaultIntervalMinutes: 15,
            Notes: "Solarman OpenAPI (AppId+AppSecret). 10 req/min documented."),

        // FoxESS OpenAPI — 10 req/min per key (vendor docs).
        [InverterBrand.FoxESS] = new BrandCapability(
            SupportedMethods: new[] { ConnectionMethod.OfficialApi },
            MinIntervalMinutes: 5,
            DefaultIntervalMinutes: 15,
            Notes: "FoxESS OpenAPI key from the customer portal. 10 req/min."),

        // Sigenergy — rate limit not formally documented; treat as
        // 10 req/min like other Chinese-vendor OpenAPIs for safety.
        [InverterBrand.Sigenergy] = new BrandCapability(
            SupportedMethods: new[] { ConnectionMethod.OfficialApi },
            MinIntervalMinutes: 5,
            DefaultIntervalMinutes: 15,
            Notes: "Sigenergy Cloud API. Rate limit unofficial — 5 min is comfortable."),
    };

    public static BrandCapability Get(InverterBrand brand) =>
        _map.TryGetValue(brand, out var cap) ? cap : Default;

    private static readonly BrandCapability Default = new(
        SupportedMethods: new[] { ConnectionMethod.OfficialApi, ConnectionMethod.UserPassword },
        MinIntervalMinutes: 5,
        DefaultIntervalMinutes: 15,
        Notes: "");

    /// <summary>
    /// Clamp a requested interval to the brand's allowed presets and minimum.
    /// Returns the closest valid value at or above both the brand's minimum
    /// and the next preset >= the requested value.
    /// </summary>
    public static int ClampInterval(InverterBrand brand, int requestedMinutes)
    {
        var cap = Get(brand);
        var floor = cap.MinIntervalMinutes;
        // Snap to the nearest allowed preset that's >= the brand floor AND
        // >= the requested value.  Operators picking from the dropdown will
        // pass a valid preset already; this is the safety net.
        foreach (var preset in AllowedIntervalMinutes)
            if (preset >= floor && preset >= requestedMinutes)
                return preset;
        return AllowedIntervalMinutes[^1];
    }
}

public sealed record BrandCapability(
    ConnectionMethod[] SupportedMethods,
    int MinIntervalMinutes,
    int DefaultIntervalMinutes,
    string Notes)
{
    /// <summary>What this brand calls the Plant/Station ID field.</summary>
    public string PlantIdLabel { get; init; } = "Plant/Station ID";

    /// <summary>What value to type in — the single most-asked question per brand.</summary>
    public string PlantIdWhat { get; init; } = "";

    /// <summary>Where in the vendor's portal to find it.</summary>
    public string PlantIdWhere { get; init; } = "";

    /// <summary>Example value, so the expected shape is obvious at a glance.</summary>
    public string PlantIdExample { get; init; } = "";

    /// <summary>What the credential fields mean for this brand.</summary>
    public string CredentialHint { get; init; } = "";
}

/// <summary>
/// Per-brand field guidance shown next to the inputs.  Kept beside the
/// capability table so "what does this brand call things / where do I get
/// them" lives in exactly one place instead of being folklore spread
/// across support chats.
/// </summary>
public static class BrandFieldGuide
{

    public static BrandCapability Get(InverterBrand brand)
    {
        var cap = BrandCapabilities.Get(brand);
        return brand switch
        {
            InverterBrand.Hoymiles => cap with
            {
                PlantIdLabel = "Station ID (sid)",
                PlantIdWhat = "ตัวเลข Station ID ของไซต์",
                PlantIdWhere = "เข้า global.hoymiles.com → เลือกไซต์ → ดูตัวเลขท้าย URL (…/station/<sid>)",
                PlantIdExample = "1298491919450550810",
                CredentialHint = "Username/Password = อีเมลกับรหัสผ่านของ S-Miles Cloud (ไม่มี API key ให้ใช้)",
            },
            InverterBrand.Huawei => cap with
            {
                PlantIdLabel = "Station Code",
                PlantIdWhat = "รหัสสถานี ขึ้นต้นด้วย NE= (ระบบเติม NE= ให้อัตโนมัติถ้าลืม)",
                PlantIdWhere = "FusionSolar → Plant Management → คอลัมน์ Plant Code",
                PlantIdExample = "NE=56946102",
                CredentialHint = "ต้องใช้บัญชี Northbound API (ไม่ใช่บัญชี login ปกติ) — ขอจาก Huawei/ผู้ติดตั้ง",
            },
            InverterBrand.FoxESS => cap with
            {
                PlantIdLabel = "Plant ID หรือ Inverter SN",
                PlantIdWhat = "ใส่ Plant/Station ID เพื่อรวมผลผลิตของ inverter ทุกตัวในไซต์ (แนะนำ) " +
                              "หรือใส่ SN ของ inverter ตัวเดียวถ้าต้องการเจาะจง — เว้นว่าง = ใช้ทุกอุปกรณ์ในบัญชี",
                PlantIdWhere = "กดปุ่ม \"ดึงรายการจาก Cloud\" แล้วเลือกได้เลย " +
                               "(หรือดูที่ foxesscloud.com → Device → คอลัมน์ SN)",
                PlantIdExample = "60P1103061FM046",
                CredentialHint = "API Key = Private Token จาก foxesscloud.com → User Profile → API Management " +
                                 "(กด Generate แล้ว copy ทันที เห็นครั้งเดียว) — ไม่ต้องใส่ Secret/Username",
            },
            InverterBrand.Solis => cap with
            {
                PlantIdLabel = "Station ID",
                PlantIdWhat = "ตัวเลข Station ID ของไซต์",
                PlantIdWhere = "SolisCloud → เลือกไซต์ → ดูตัวเลขใน URL",
                PlantIdExample = "1298491919450550810",
                CredentialHint = "แนะนำ API Key + Secret (SolisCloud → API Management). " +
                                 "โหมด user/password ใช้ได้แต่พังบ่อยเมื่อ Solis อัปเดตเว็บ",
            },
            InverterBrand.GoodWe => cap with
            {
                PlantIdLabel = "Power Station ID",
                PlantIdWhat = "GUID ของไซต์ (มีขีดคั่น)",
                PlantIdWhere = "SEMS Portal → เลือกไซต์ → คัดลอกค่า powerStationId จาก URL",
                PlantIdExample = "04acf5fb-3843-4b9f-aff3-c67796bd91c2",
                CredentialHint = "Username/Password = บัญชี SEMS Portal (GoodWe ไม่มี public API key)",
            },
            InverterBrand.Deye => cap with
            {
                PlantIdLabel = "Station ID",
                PlantIdWhat = "ตัวเลข Station ID",
                PlantIdWhere = "Solarman/Deye Cloud → เลือกไซต์ → ดูใน URL",
                PlantIdExample = "1234567",
                CredentialHint = "ต้องใช้ครบ 4 ค่า: AppId (API Key), AppSecret, อีเมล, รหัสผ่าน " +
                                 "— ขอ AppId/Secret จาก Solarman Business",
            },
            InverterBrand.Sigenergy => cap with
            {
                PlantIdLabel = "System ID",
                PlantIdWhat = "รหัสระบบของไซต์",
                PlantIdWhere = "Sigen Cloud → System → ดูรหัสในหน้า System info",
                PlantIdExample = "123456",
                CredentialHint = "API Key ใช้ระบุ region (eu/apac/us/cn) — ค่าเริ่มต้นคือ apac",
            },
            _ => cap,
        };
    }
}
