namespace SolarApp.Services;

/// <summary>
/// Centralised "what day is it at the site" helper.  All daily-aggregate
/// keys, query windows, and "today's production" lookups must use this —
/// not DateTime.UtcNow.Date — because the inverter resets its
/// TodayEnergyKwh counter at LOCAL midnight, not UTC midnight.  For a
/// Thailand deployment (UTC+7), keying by UtcNow.Date during the 00:00-
/// 07:00 local window stores fresh-day readings under yesterday's date
/// and clobbers the real end-of-day value.
///
/// Site timezone is configurable via appsettings "Site:TimeZone".
/// Defaults to Asia/Bangkok (the home market).  Tries both the IANA id
/// (Linux) and Windows id; falls back to UTC if neither resolves (the
/// app then has the previous behaviour, no crash).
/// </summary>
public static class SiteTime
{
    private static readonly TimeZoneInfo _tz = Resolve();

    /// <summary>Site local date for "today" — used as the upsert key.</summary>
    public static DateTime Today => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _tz).Date;

    /// <summary>Site local "now" (date + time of day).</summary>
    public static DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _tz);

    /// <summary>
    /// The UTC instant at which today began at the site. Use for "today"
    /// windows over UTC timestamps (OccurredAt, ResolvedAt, UpdatedAt):
    /// UtcNow.Date starts at 07:00 Thailand time and drops the first seven
    /// hours of every day.
    /// </summary>
    public static DateTime TodayStartUtc => ToUtc(Today);

    /// <summary>Convert a site-local time (e.g. a date picked in a form) to UTC.</summary>
    public static DateTime ToUtc(DateTime siteLocal) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(siteLocal, DateTimeKind.Unspecified), _tz);

    /// <summary>Convert an arbitrary UTC instant to site-local time.</summary>
    public static DateTime ToSiteTime(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _tz);

    /// <summary>Nullable overload, for "last seen" style fields that may be empty.</summary>
    public static DateTime? ToSiteTime(DateTime? utc) => utc.HasValue ? ToSiteTime(utc.Value) : null;

    public static TimeZoneInfo TimeZone => _tz;

    private static TimeZoneInfo Resolve()
    {
        foreach (var id in new[] { "Asia/Bangkok", "SE Asia Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { /* try next */ }
            catch (InvalidTimeZoneException)  { /* try next */ }
        }
        return TimeZoneInfo.Utc;
    }
}
