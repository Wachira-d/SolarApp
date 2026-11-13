using SolarApp.Models.Enums;

namespace SolarApp.Services.Telemetry;

/// <summary>
/// Brand tables for StatusCodeCatalog. Extend here; nothing else needs to change.
/// Only entries we are confident of are listed — an unknown code decodes to
/// "รหัส N (ไม่ทราบความหมาย)", which is better than a wrong translation.
/// </summary>
public static partial class StatusCodeCatalog
{
    static partial void RegisterBrandTables()
    {
        Register(InverterBrand.FoxESS, "status", FoxEssRunningState);
        Register(InverterBrand.Huawei, "status", HuaweiDeviceStatus);
        Register(InverterBrand.Huawei, "alarm-bits", HuaweiAlarm1Bits);
        Register(InverterBrand.Deye, "status", DeyeRunningState);
    }

    /// <summary>FoxESS H1 / H3 "inverter state" register (as read by the common HA foxess_modbus integration).</summary>
    private static readonly Dictionary<long, (string, AlertSeverity)> FoxEssRunningState = new()
    {
        [0] = ("ตรวจสอบตัวเอง (Self-test)", AlertSeverity.Low),
        [1] = ("รอเชื่อมต่อ (Waiting)", AlertSeverity.Low),
        [2] = ("กำลังตรวจสอบก่อนเชื่อมต่อ (Checking)", AlertSeverity.Low),
        [3] = ("ทำงานปกติ เชื่อมต่อกริด (On-grid)", AlertSeverity.Low),
        [4] = ("ทำงานแบบไม่มีกริด / EPS (Off-grid)", AlertSeverity.Medium),
        [5] = ("ขัดข้อง (Fault)", AlertSeverity.High),
        [6] = ("ขัดข้องถาวร ต้องแจ้งช่าง (Permanent fault)", AlertSeverity.Critical),
        [7] = ("สแตนด์บาย (Standby)", AlertSeverity.Low),
        [8] = ("กำลังอัปเกรดเฟิร์มแวร์ (Upgrading)", AlertSeverity.Low),
        [9] = ("โหมดทดสอบโรงงาน (FCT)", AlertSeverity.Medium),
        [10] = ("สถานะไม่ถูกต้อง (Illegal)", AlertSeverity.Medium),
    };

    /// <summary>Huawei SUN2000 register 32089 "Device status".</summary>
    private static readonly Dictionary<long, (string, AlertSeverity)> HuaweiDeviceStatus = new()
    {
        [0x0000] = ("สแตนด์บาย: กำลังเริ่มระบบ (Standby: initializing)", AlertSeverity.Low),
        [0x0001] = ("สแตนด์บาย: ตรวจวัดความต้านทานฉนวน (Standby: insulation resistance)", AlertSeverity.Low),
        [0x0002] = ("สแตนด์บาย: ตรวจวัดแสงแดด (Standby: irradiation)", AlertSeverity.Low),
        [0x0003] = ("สแตนด์บาย: ตรวจสอบกริด (Standby: grid detecting)", AlertSeverity.Low),
        [0x0100] = ("กำลังเริ่มทำงาน (Starting)", AlertSeverity.Low),
        [0x0200] = ("ทำงานปกติ เชื่อมต่อกริด (On-grid)", AlertSeverity.Low),
        [0x0201] = ("เชื่อมต่อกริด: ถูกจำกัดกำลัง (Power limited)", AlertSeverity.Low),
        [0x0202] = ("เชื่อมต่อกริด: ลดกำลังเอง (Self-derating)", AlertSeverity.Medium),
        [0x0300] = ("หยุดทำงาน: ขัดข้อง (Shutdown: fault)", AlertSeverity.High),
        [0x0301] = ("หยุดทำงาน: ตามคำสั่ง (Shutdown: command)", AlertSeverity.Medium),
        [0x0302] = ("หยุดทำงาน: OVGR", AlertSeverity.High),
        [0x0303] = ("หยุดทำงาน: การสื่อสารขาดหาย (Communication disconnected)", AlertSeverity.High),
        [0x0304] = ("หยุดทำงาน: ถูกจำกัดกำลัง (Power limited)", AlertSeverity.Medium),
        [0x0305] = ("หยุดทำงาน: ต้องสั่งเริ่มด้วยมือ (Manual startup required)", AlertSeverity.High),
        [0x0306] = ("หยุดทำงาน: สวิตช์ DC ถูกปลด (DC switches disconnected)", AlertSeverity.High),
        [0x0307] = ("หยุดทำงาน: ตัดฉุกเฉิน (Rapid cutoff)", AlertSeverity.High),
        [0x0308] = ("หยุดทำงาน: กำลังไฟขาเข้าไม่พอ (Input underpower)", AlertSeverity.Low),
        [0x0401] = ("ควบคุมโดยกริด: เส้นโค้ง cosφ-P", AlertSeverity.Low),
        [0x0402] = ("ควบคุมโดยกริด: เส้นโค้ง Q-U", AlertSeverity.Low),
        [0x0403] = ("ควบคุมโดยกริด: เส้นโค้ง PF-U", AlertSeverity.Low),
        [0x0404] = ("ควบคุมโดยกริด: dry contact", AlertSeverity.Low),
        [0x0405] = ("ควบคุมโดยกริด: เส้นโค้ง Q-P", AlertSeverity.Low),
        [0x0500] = ("พร้อมตรวจสอบ (Spot-check ready)", AlertSeverity.Low),
        [0x0501] = ("กำลังตรวจสอบ (Spot-checking)", AlertSeverity.Low),
        [0x0600] = ("กำลังตรวจตรา (Inspecting)", AlertSeverity.Low),
        [0x0700] = ("ทดสอบ AFCI ตัวเอง (AFCI self check)", AlertSeverity.Low),
        [0x0800] = ("สแกนเส้นโค้ง I-V (I-V scanning)", AlertSeverity.Low),
        [0x0900] = ("ตรวจสอบอินพุต DC (DC input detection)", AlertSeverity.Low),
        [0x0A00] = ("ทำงานแบบไม่มีกริด: ชาร์จ (Off-grid charging)", AlertSeverity.Medium),
        [0xA000] = ("สแตนด์บาย: ไม่มีแสงแดด (No irradiation)", AlertSeverity.Low),
    };

    /// <summary>Huawei SUN2000 register 32008 "Alarm 1" bits (alarm IDs 2001–2040).</summary>
    private static readonly Dictionary<long, (string, AlertSeverity)> HuaweiAlarm1Bits = new()
    {
        [0] = ("แรงดันสตริงสูงเกิน (2001 High string input voltage)", AlertSeverity.Critical),
        [1] = ("เกิดอาร์กฝั่ง DC (2002 DC arc fault)", AlertSeverity.Critical),
        [2] = ("ต่อสตริงกลับขั้ว (2011 String reverse connection)", AlertSeverity.Critical),
        [3] = ("กระแสสตริงไหลย้อน (2012 String current backfeed)", AlertSeverity.Medium),
        [4] = ("กำลังสตริงผิดปกติ (2013 Abnormal string power)", AlertSeverity.Medium),
        [5] = ("AFCI ทดสอบตัวเองไม่ผ่าน (2021 AFCI self-check fail)", AlertSeverity.Critical),
        [6] = ("สายเฟสลัดวงจรลง PE (2031 Phase wire short-circuited to PE)", AlertSeverity.Critical),
        [7] = ("ไฟฟ้าดับ / ไม่พบกริด (2032 Grid loss)", AlertSeverity.High),
        [8] = ("แรงดันกริดต่ำ (2033 Grid undervoltage)", AlertSeverity.High),
        [9] = ("แรงดันกริดสูง (2034 Grid overvoltage)", AlertSeverity.High),
        [10] = ("แรงดันกริดไม่สมดุล (2035 Grid voltage imbalance)", AlertSeverity.High),
        [11] = ("ความถี่กริดสูง (2036 Grid overfrequency)", AlertSeverity.High),
        [12] = ("ความถี่กริดต่ำ (2037 Grid underfrequency)", AlertSeverity.High),
        [13] = ("ความถี่กริดไม่เสถียร (2038 Unstable grid frequency)", AlertSeverity.High),
        [14] = ("กระแสขาออกเกิน (2039 Output overcurrent)", AlertSeverity.Critical),
        [15] = ("DC component ขาออกสูง (2040 Output DC component overhigh)", AlertSeverity.Critical),
    };

    /// <summary>Deye hybrid (SG0x) running-state register.</summary>
    private static readonly Dictionary<long, (string, AlertSeverity)> DeyeRunningState = new()
    {
        [0] = ("สแตนด์บาย (Standby)", AlertSeverity.Low),
        [1] = ("ตรวจสอบตัวเอง (Self-check)", AlertSeverity.Low),
        [2] = ("ทำงานปกติ (Normal)", AlertSeverity.Low),
        [3] = ("มีการแจ้งเตือน (Alarm)", AlertSeverity.Medium),
        [4] = ("ขัดข้อง (Fault)", AlertSeverity.High),
    };
}
