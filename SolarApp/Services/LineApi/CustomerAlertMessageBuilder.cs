using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Services.LineApi;

/// <summary>
/// Renders SystemAlert objects as plain-Thai customer-facing LINE messages.
///
/// The default alert title/message are written for technicians ("API
/// connection lost", "device offline", brand names, last-online timestamps).
/// Customers don't speak that vocabulary and don't care about the distinction
/// between "API down" and "inverter down" — what they want to know is
///   * is my solar working right now? (no)
///   * what's likely wrong? (in laypeople terms)
///   * who's handling it? (we are)
///   * how do I follow up? (phone / line)
///
/// This builder produces one short message per AlertType that follows those
/// four beats.  Use SystemHealthMonitorJob for the technical version sent to
/// the operator's alert group + partner staff; use this one for the LINE
/// recipient on the installation.LineGroupId / Customer.LineUserId.
/// </summary>
public static class CustomerAlertMessageBuilder
{
    public static string Build(SystemAlert alert, Installation installation)
    {
        var site = installation.SiteName;
        var when = (alert.OccurredAt == default ? DateTime.UtcNow : alert.OccurredAt)
            .ToLocalTime().ToString("dd/MM/yyyy เวลา HH:mm");
        var last = installation.LastOnlineAt?.ToLocalTime().ToString("dd/MM/yyyy เวลา HH:mm")
                   ?? "ยังไม่เคยเชื่อมต่อ";

        return alert.AlertType switch
        {
            // Offline = lost cloud contact / inverter not reporting.  We do
            // NOT actually know the cause yet (usually just internet at the
            // site; sometimes a real fault).  Frame it cautiously so the
            // customer doesn't panic — many "outages" turn out to be a
            // 10-minute ISP blip that resolves itself.  We'll send a
            // follow-up via BuildRecovery once it's back, with the actual
            // cause if we found one.
            AlertType.Offline =>
$@"ℹ️ ระบบโซลาร์ขาดการเชื่อมต่อชั่วคราว

📍 ไซต์: {site}
🕐 ตรวจไม่พบสัญญาณตั้งแต่: {when}
🟢 ทำงานครั้งสุดท้าย: {last}

ส่วนใหญ่อาจเกิดจาก:
• อินเทอร์เน็ตที่ไซต์ขัดข้องชั่วคราว (พบบ่อยที่สุด)
• ไฟดับ (Grid Loss)
• อุปกรณ์รับ-ส่งข้อมูล (data logger) มีปัญหา

ระบบโซลาร์เซลล์อาจยังทำงานปกติอยู่ — เรายังไม่ทราบสาเหตุที่แน่ชัด
ทีมงานกำลังตรวจสอบและจะแจ้งให้ทราบทันทีเมื่อทราบข้อมูลที่แท้จริง",

            AlertType.Error =>
$@"⚠️ ระบบโซลาร์เซลล์มีปัญหา

📍 ไซต์: {site}
🕐 เวลา: {when}
รายละเอียด: {Truncate(alert.Title, 120)}

ทีมงานได้รับการแจ้งเตือนและกำลังตรวจสอบ
สอบถามเพิ่มเติม กรุณาตอบกลับในแชทนี้ครับ",

            AlertType.LowPerformance =>
$@"⚡ ผลิตไฟฟ้าได้ต่ำกว่าปกติ

📍 ไซต์: {site}
🕐 เวลา: {when}

วันนี้ผลิตไฟได้น้อยกว่าค่าเฉลี่ยปกติของไซต์นี้
อาจเป็นเพราะ:
• สภาพอากาศ (ฝน เมฆมาก)
• แผงสกปรก/มีฝุ่น
• มีเงาบังแผงบางส่วน

ทีมงานกำลังตรวจสอบ และจะแจ้งกลับเมื่อมีข้อมูลเพิ่มเติม",

            AlertType.MaintenanceDue =>
$@"💧 ครบกำหนดล้างแผงโซลาร์เซลล์

📍 ไซต์: {site}

ตามตารางบำรุงรักษา ไซต์นี้ครบกำหนดล้างแผงแล้ว
ทีมงานจะติดต่อเพื่อนัดวันเข้าให้บริการครับ",

            AlertType.WarrantyExpiring =>
$@"📋 ใกล้หมดประกัน

📍 ไซต์: {site}
ประกันระบบจะสิ้นสุดภายใน 90 วัน

หากต้องการต่ออายุประกัน หรือสอบถามรายละเอียด
กรุณาตอบกลับในแชทนี้ครับ",

            _ => $"แจ้งเตือนระบบโซลาร์ ไซต์ {site}: {Truncate(alert.Title, 100)}",
        };
    }

    /// <summary>
    /// Customer-facing recovery message for a previously-notified alert.
    /// Includes the actual cause when we discovered one during the outage
    /// (e.g. the operator found a specific Huawei alarm in FusionSolar
    /// or the next sync round ingested per-alarm records).  When we never
    /// found a specific cause — common for short network blips — the
    /// message simply confirms the connection is back and reassures the
    /// customer that the panels were likely working all along.
    /// </summary>
    /// <param name="alert">The original alert that just resolved.</param>
    /// <param name="installation">Site context.</param>
    /// <param name="discoveredCauses">
    /// Alarm titles from sibling alerts that occurred during the outage
    /// window, e.g. ["Communication interrupted", "Grid voltage abnormal"].
    /// Pass an empty array when no specific cause was found.
    /// </param>
    public static string BuildRecovery(
        SystemAlert alert,
        Installation installation,
        IReadOnlyList<string> discoveredCauses)
    {
        var site = installation.SiteName;
        var downtime = alert.ResolvedAt.HasValue
            ? alert.ResolvedAt.Value - alert.OccurredAt
            : TimeSpan.Zero;
        var downtimeStr = downtime.TotalHours >= 1
            ? $"{downtime.TotalHours:0.0} ชั่วโมง"
            : $"{Math.Max(1, downtime.TotalMinutes):0} นาที";

        // When we have a real cause from per-alarm records: name it.
        // Otherwise frame as "connection blip, probably internet".
        var causeBlock = discoveredCauses.Count > 0
            ? "\n\nสาเหตุที่ตรวจพบในช่วงนี้:\n• " + string.Join("\n• ", discoveredCauses)
            : "\n\nไม่พบ alarm หรือ fault code เฉพาะระหว่างเหตุการณ์ — " +
              "ส่วนใหญ่หมายความว่าเป็นปัญหาการเชื่อมต่อชั่วคราว (อินเทอร์เน็ต/data logger) " +
              "ไม่ใช่ความผิดปกติของอุปกรณ์";

        return alert.AlertType switch
        {
            AlertType.Offline =>
$@"✅ ระบบโซลาร์เซลล์กลับมาเชื่อมต่อแล้ว

📍 ไซต์: {site}
⏱ ขาดการเชื่อมต่อนาน: {downtimeStr}

ขณะนี้ระบบกลับมาส่งข้อมูลและทำงานได้ปกติแล้ว{causeBlock}",

            AlertType.Error =>
$@"✅ ระบบโซลาร์เซลล์กลับมาทำงานปกติ

📍 ไซต์: {site}
⏱ ระยะเวลาที่หยุด: {downtimeStr}{causeBlock}",

            _ =>
$@"✅ {site} กลับมาทำงานปกติแล้ว (หยุดไป {downtimeStr}){causeBlock}",
        };
    }

    /// <summary>
    /// Whether this alert type is configured to notify the customer for the
    /// given installation.  Acts as the single source of truth so callers
    /// don't have to remember the AlertType → NotifyOnXxx mapping.
    /// </summary>
    public static bool ShouldNotifyCustomer(Installation install, AlertType type) => type switch
    {
        AlertType.Offline           => install.NotifyOnGridLoss,
        AlertType.Error             => install.NotifyOnGridLoss,
        AlertType.LowPerformance    => install.NotifyOnLowProduction,
        AlertType.MaintenanceDue    => install.NotifyOnCleaningDue,
        AlertType.WarrantyExpiring  => install.NotifyOnWarrantyExpiry,
        _                           => false,
    };

    /// <summary>
    /// Whether the customer is currently in a "do-not-disturb" window
    /// (e.g. 22:00 → 07:00 local).  Critical-severity alerts ignore this
    /// — a Critical fault is worth waking someone up for, but a
    /// Medium/High Offline blip at 3 AM is not.  Operator alert group
    /// and partner channels never honour quiet hours; this gates the
    /// customer LINE channel only.
    /// </summary>
    public static bool IsInCustomerQuietHours(Installation install, DateTime utcNow, AlertSeverity severity)
    {
        if (severity == AlertSeverity.Critical) return false;
        if (!install.QuietHoursStart.HasValue || !install.QuietHoursEnd.HasValue) return false;
        // Use the SITE-local time, not the server OS time.  On a UTC-configured
        // host, utcNow.ToLocalTime() == UTC, so a 22:00–07:00 quiet window
        // silenced 05:00–14:00 Thai (prime daytime) and let 3 AM alerts through.
        // SiteTime centralises the Bangkok offset used everywhere else.
        var localNow = TimeOnly.FromDateTime(SolarApp.Services.SiteTime.ToSiteTime(utcNow));
        var start = install.QuietHoursStart.Value;
        var end = install.QuietHoursEnd.Value;
        // Window can wrap midnight: 22:00→07:00 means "22:00-23:59 OR 00:00-07:00".
        return start <= end
            ? localNow >= start && localNow < end
            : localNow >= start || localNow < end;
    }

    /// <summary>
    /// LINE recipient for customer-facing alerts.  Prefers the per-installation
    /// LineGroupId (so partners with a shared group for several sites can route
    /// per site) and falls back to the Customer.LineUserId when no override is
    /// set.  Returns null when neither is configured.
    /// </summary>
    public static string? PickRecipient(Installation install) =>
        !string.IsNullOrWhiteSpace(install.LineGroupId)
            ? install.LineGroupId
            : install.Customer?.LineUserId;

    private static string Truncate(string? s, int n) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n] + "…";
}
