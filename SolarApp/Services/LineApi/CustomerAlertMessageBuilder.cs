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
            AlertType.Offline =>
$@"🚨 ระบบโซลาร์เซลล์หยุดทำงาน

📍 ไซต์: {site}
🕐 เริ่มหยุด: {when}
🟢 ทำงานครั้งสุดท้าย: {last}

อาจเกิดจาก:
• ไฟดับ (Grid Loss)
• อินเวอร์เตอร์ขัดข้อง
• อินเทอร์เน็ตที่ไซต์มีปัญหา

ทีมงานได้รับการแจ้งเตือนและกำลังตรวจสอบ
หากท่านอยู่ที่ไซต์ ขอความกรุณาตรวจสอบไฟฟ้าที่ตู้เมน
สอบถามเพิ่มเติม กรุณาตอบกลับในแชทนี้ครับ",

            AlertType.Error =>
$@"⚠️ ระบบโซลาร์เซลล์มีปัญหา

📍 ไซต์: {site}
🕐 เวลา: {when}
รายละเอียด: {Truncate(alert.Title, 120)}

ทีมงานได้รับการแจ้งเตือนและกำลังตรวจสอบ
สอบถามเพิ่มเติม กรุณาตอบกลับในแชทนี้ครับ",

            AlertType.LowProduction =>
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
    /// Whether this alert type is configured to notify the customer for the
    /// given installation.  Acts as the single source of truth so callers
    /// don't have to remember the AlertType → NotifyOnXxx mapping.
    /// </summary>
    public static bool ShouldNotifyCustomer(Installation install, AlertType type) => type switch
    {
        AlertType.Offline           => install.NotifyOnGridLoss,
        AlertType.Error             => install.NotifyOnGridLoss,
        AlertType.LowProduction     => install.NotifyOnLowProduction,
        AlertType.MaintenanceDue    => install.NotifyOnCleaningDue,
        AlertType.WarrantyExpiring  => install.NotifyOnWarrantyExpiry,
        _                           => false,
    };

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
