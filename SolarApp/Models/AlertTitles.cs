namespace SolarApp.Models;

/// <summary>
/// Title prefixes that identify which producer raised an alert.
///
/// Several producers share an AlertType — warranty and contract reminders are
/// both WarrantyExpiring, API-failure and vendor alarms are both Error — and
/// each must dedupe and resolve only its own. When one of them didn't, it
/// silently closed or blocked another's alerts: the warranty sweep closed every
/// contract alert, and a contract alert blocked the warranty reminder. The
/// customer message builder also needs them, so a contract renewal isn't sent
/// to a customer as "ใกล้หมดประกัน". One place, so producer and reader can't
/// drift apart.
/// </summary>
public static class AlertTitles
{
    public const string ApiFailure = "เชื่อมต่อคลาวด์ไม่สำเร็จ: ";
    public const string ContractExpiring = "สัญญาบริการใกล้หมดอายุ: ";
    public const string WarrantyExpiring = "ประกันใกล้หมดอายุ: ";
    public const string CleaningDue = "ครบกำหนดล้างแผง: ";
    /// <summary>Cleaning reminders raised before the titles were in Thai.</summary>
    public const string LegacyCleaningDue = "Panel Cleaning Due: ";
}
