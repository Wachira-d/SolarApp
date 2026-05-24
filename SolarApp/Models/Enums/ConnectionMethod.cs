namespace SolarApp.Models.Enums;

/// <summary>
/// How the SolarApp adapter authenticates against the inverter brand's cloud.
/// Most brands support more than one; the operator picks which to use per
/// installation depending on which credentials the customer can provide.
/// </summary>
public enum ConnectionMethod
{
    /// <summary>
    /// Brand's official rate-stable API: API Key / Secret pair issued by the
    /// vendor (Solis HMAC, FoxESS OpenAPI, Huawei Northbound, Deye Solarman).
    /// Preferred wherever available — durable, documented, less likely to
    /// break when the brand updates their web portal.
    /// </summary>
    OfficialApi = 0,

    /// <summary>
    /// Reverse web-portal login using the customer's account Username/Password
    /// (Hoymiles, Solis web-login, GoodWe SEMS).  Easiest to set up (no API
    /// key application needed) but can break when the vendor updates their
    /// web portal.  Use when the customer can't / won't apply for an API key.
    /// </summary>
    UserPassword = 1,

    /// <summary>
    /// Brand-specific hybrid: e.g. Huawei IAM with shared "account+password"
    /// + region-routing instead of plain NB API key.  Reserved — actual
    /// brand list is enumerated by BrandCapabilities.
    /// </summary>
    Hybrid = 2,
}
