namespace SolarApp.Models.Enums;

/// <summary>
/// Predefined photo categories for solar installation work orders.
/// Each category can have Before + After shots.
/// </summary>
public enum PhotoCategory
{
    // ─── Site overview ───
    SiteOverview = 1,           // ภาพรวมไซต์
    RoofOverview = 2,           // ภาพรวมหลังคา/พื้นที่ติดตั้ง

    // ─── Panels ───
    PanelArrayOverall = 10,     // ภาพรวมแผงโซลาร์ทั้งหมด
    PanelCloseup = 11,          // แผงระยะใกล้ (สภาพ/ความสกปรก)
    PanelDamage = 12,           // แผงเสียหาย/แตก/เปลี่ยนสี
    PanelStringLabel = 13,      // ป้ายสตริง/แถว

    // ─── Inverter ───
    InverterZone = 20,          // โซน Inverter ภาพรวม
    InverterDisplay = 21,       // หน้าจอ Inverter (อ่านค่า)
    InverterWiring = 22,        // สายไฟ Inverter
    InverterNameplate = 23,     // ป้ายรุ่น/Serial Inverter

    // ─── Electrical ───
    ACBreaker = 30,             // ตู้ AC Breaker
    DCIsolator = 31,            // DC Isolator
    ElectricalPanel = 32,       // ตู้ไฟหลัก / MDB
    Meter = 33,                 // มิเตอร์ไฟฟ้า (อ่านค่า)
    WiringConduit = 34,         // ท่อ/รางสาย

    // ─── Thermal scan ───
    ThermalPanelOverall = 40,   // สแกนความร้อน แผงภาพรวม
    ThermalHotspot = 41,        // จุดร้อนที่พบ
    ThermalInverter = 42,       // สแกนความร้อน Inverter
    ThermalConnection = 43,     // สแกนความร้อน จุดเชื่อมต่อ/ขั้วต่อ

    // ─── Battery / ESS ───
    BatteryZone = 50,           // โซน Battery
    BatteryDisplay = 51,        // หน้าจอ BMS

    // ─── Structure / Mounting ───
    MountingStructure = 60,     // โครงยึดแผง
    RoofPenetration = 61,       // จุดเจาะหลังคา/กันซึม

    // ─── Cleaning specific ───
    PanelBeforeCleaning = 70,   // แผงก่อนล้าง
    PanelAfterCleaning = 71,    // แผงหลังล้าง
    DrainageGutter = 72,        // รางน้ำ/ระบายน้ำ

    // ─── Documentation ───
    WorkPermit = 80,            // ใบอนุญาตทำงาน
    SafetySign = 81,            // ป้ายเตือนความปลอดภัย
    MeterReading = 82,          // อ่านค่ามิเตอร์
    CustomerSignoff = 83,       // ลูกค้าเซ็นรับงาน

    // ─── Other ───
    Other = 99                  // อื่นๆ
}

/// <summary>
/// Whether the photo is a "before" (pre-work) or "after" (post-work) shot,
/// or just a general reference during work.
/// </summary>
public enum PhotoTiming
{
    Before = 1,
    During = 2,
    After = 3
}
