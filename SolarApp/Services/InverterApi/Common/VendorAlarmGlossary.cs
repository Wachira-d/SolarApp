using System.Text.RegularExpressions;

namespace SolarApp.Services.InverterApi.Common;

/// <summary>
/// Plain-Thai meaning of the alarm names inverters report ("Low Insulation
/// Resistance", "Grid Phase UV Level2", "Bus Overvoltage"…), with what to
/// check and what usually fixes it.
///
/// The alarm name alone is the vendor's English; the LINE card fell back to
/// "Inverter แจ้ง fault/error — ตรวจ error code" for every one of them, which
/// told the reader nothing they didn't already know. The names are largely
/// shared across GoodWe, Huawei, Solis and Deye, so the table is
/// brand-neutral; brand-specific codes stay in InverterKnowledgeBase.
///
/// Matched on whole words against the alarm name only — never against free
/// text such as an offline alert's explanation, where words like "grid" or
/// "comm" appear without meaning that fault.
/// </summary>
public static class VendorAlarmGlossary
{
    public sealed record Entry(string Thai, string Meaning, string[] Checks, string[] Fixes, bool SafetyCritical = false);

    private static readonly (Regex Pattern, Entry Entry)[] Table =
    {
        (Rx(@"\barc\b|\barc fault|\barcing\b"), new(
            "ตรวจพบประกายไฟฝั่ง DC (Arc fault)",
            "มีการอาร์กที่สาย/ขั้วต่อ DC — เสี่ยงไฟไหม้",
            new[] { "ตรวจ MC4 ทุกจุดว่าหลวม ไหม้ หรือมีรอยดำ", "ตรวจสาย DC ว่ามีรอยถลอก/ขาด" },
            new[] { "ปิดระบบก่อนเข้าตรวจ", "เปลี่ยน MC4 ที่เสีย (ห้ามใช้ซ้ำ)", "ขันขั้วต่อให้แน่นตามแรงบิดที่กำหนด" },
            SafetyCritical: true)),

        (Rx(@"insulation|isolation|\biso\b|riso"), new(
            "ค่าความเป็นฉนวนฝั่ง DC ต่ำ",
            "มีกระแสรั่วจากสาย DC/แผงลงดิน มักเกิดตอนเช้าที่มีความชื้นหรือหลังฝนตก",
            new[] { "ดูว่าเกิดเฉพาะช่วงเช้า/หลังฝนหรือไม่ (ถ้าใช่ มักเป็นความชื้น)", "ตรวจ MC4 และกล่องรวมสายว่ามีน้ำเข้า", "วัดค่าฉนวนแต่ละ string ด้วย megger" },
            new[] { "เป่าแห้ง/ซีลขั้วต่อที่มีความชื้น", "เปลี่ยนสายหรือขั้วต่อที่ค่าฉนวนต่ำ", "ถ้าหายเองหลังแดดออก ให้เฝ้าดูความถี่ที่เกิด" },
            SafetyCritical: true)),

        (Rx(@"residual current|leakage|\bgfci\b|\brcd\b|\brcmu\b"), new(
            "กระแสรั่วลงดินสูง",
            "มีกระแสรั่วผิดปกติ — ประเด็นความปลอดภัย",
            new[] { "ตรวจระบบสายดิน", "ตรวจสาย DC ว่าไม่แตะโครงโลหะ", "ตรวจขั้วต่อว่ามีน้ำเข้า" },
            new[] { "แก้จุดที่สายแตะโลหะ/มีความชื้น", "ถ้าเกิดซ้ำ ส่งช่างไล่หาจุดรั่ว" },
            SafetyCritical: true)),

        (Rx(@"utility loss|grid (power )?outage|grid loss|no grid|grid lost|mains lost|grid disconnect"), new(
            "ไฟจากการไฟฟ้าดับ / อินเวอร์เตอร์ไม่เห็นไฟกริด",
            "อินเวอร์เตอร์หยุดจ่ายไฟเพื่อความปลอดภัยเมื่อไฟกริดหายไป",
            new[] { "ตรวจว่าไฟการไฟฟ้าในพื้นที่ดับหรือไม่", "ตรวจ AC breaker ฝั่งอินเวอร์เตอร์และตู้ MDB", "ตรวจสาย AC และมิเตอร์" },
            new[] { "ถ้าไฟการไฟฟ้าดับ ระบบจะกลับมาเองเมื่อไฟมา", "ถ้าไฟมีแต่ยังแจ้งอยู่ ให้เปิด breaker ที่ trip และตรวจสาย AC" })),

        (Rx(@"under ?voltage|\buv\b|grid phase uv|low grid voltage|grid voltage low"), new(
            "แรงดันไฟกริดต่ำกว่าเกณฑ์",
            "แรงดันจากการไฟฟ้าต่ำกว่าที่อินเวอร์เตอร์ยอมรับ มักเกิดตอนโหลดในพื้นที่สูง",
            new[] { "วัดแรงดันที่มิเตอร์/MDB ช่วงที่เกิด", "ดูว่าเกิดเฉพาะบางช่วงเวลาหรือไม่" },
            new[] { "ถ้าเกิดบ่อย แจ้งการไฟฟ้าตรวจแรงดันในพื้นที่", "ตรวจขนาดสาย AC ว่าไม่เล็กเกิน (แรงดันตก)" })),

        (Rx(@"over ?voltage.*grid|grid.*over ?voltage|\bov\b|grid rapid overvoltage|10 ?min overvoltage|grid voltage high"), new(
            "แรงดันไฟกริดสูงเกินเกณฑ์",
            "แรงดันที่จุดเชื่อมต่อสูงเกิน — มักเกิดตอนแดดแรงและโหลดในพื้นที่ต่ำ อินเวอร์เตอร์จึงตัดเพื่อป้องกัน",
            new[] { "วัดแรงดันที่มิเตอร์ช่วงเที่ยง (เกิน ~253V ถือว่าสูง)", "ตรวจระยะสาย AC ถึงจุดเชื่อมต่อ" },
            new[] { "ตั้งค่า Volt-Watt/Volt-Var ตามที่การไฟฟ้ากำหนด", "ขยายขนาดสาย AC ถ้าแรงดันตกในสายสูง", "แจ้งการไฟฟ้าตรวจหม้อแปลง" })),

        (Rx(@"frequency|\bfreq\b|\bfac\b"), new(
            "ความถี่ไฟกริดผิดปกติ",
            "ความถี่ไฟจากการไฟฟ้าออกนอกช่วง มักเป็นเหตุการณ์ชั่วคราวจากฝั่งกริด",
            new[] { "ดูว่าเกิดพร้อมกันหลายไซต์ในพื้นที่หรือไม่" },
            new[] { "ส่วนใหญ่หายเอง ถ้าเกิดซ้ำบ่อย ตรวจค่าตั้งการป้องกันตามมาตรฐานการไฟฟ้า" })),

        (Rx(@"pv.*over ?voltage|pv\d* input over ?voltage|dc over ?voltage|string over ?voltage"), new(
            "แรงดัน DC จากแผงเกินพิกัดอินเวอร์เตอร์",
            "แรงดัน string สูงเกินที่อินเวอร์เตอร์รับได้ มักเกิดตอนเช้าที่อากาศเย็น (Voc สูงขึ้น)",
            new[] { "ตรวจจำนวนแผงต่อ string เทียบกับ Voc สูงสุดของอินเวอร์เตอร์", "ดูว่าเกิดช่วงเช้าที่อากาศเย็นหรือไม่" },
            new[] { "ลดจำนวนแผงต่อ string ถ้าออกแบบเกินพิกัด", "ตรวจว่าไม่มีการต่อ string ผิด" })),

        (Rx(@"bus over ?voltage|n-bus|p-bus|bus voltage|bus unbalance"), new(
            "แรงดัน BUS ภายในอินเวอร์เตอร์สูง/ไม่สมดุล",
            "วงจรภายในตรวจพบแรงดันผิดปกติ มักตามหลังแรงดัน PV สูงหรือกริดกระชาก",
            new[] { "ดูว่าเกิดพร้อม PV Overvoltage หรือกริดผิดปกติหรือไม่", "ดูว่าเกิดซ้ำบ่อยแค่ไหน" },
            new[] { "ถ้าเกิดครั้งเดียวแล้วหาย ไม่ต้องทำอะไร", "ถ้าเกิดซ้ำ ติดต่อผู้ผลิตพร้อม S/N และเวลาที่เกิด" })),

        (Rx(@"over ?temp|temperature|overheat|\botp\b"), new(
            "อินเวอร์เตอร์ร้อนเกิน",
            "อุณหภูมิภายในสูง อินเวอร์เตอร์จะลดกำลังหรือหยุดเพื่อป้องกันตัวเอง",
            new[] { "ตรวจพัดลมระบายความร้อน", "ตรวจว่าติดตั้งในที่อากาศถ่ายเท ไม่โดนแดดตรง", "ตรวจฝุ่นที่ heat sink" },
            new[] { "ทำความสะอาด heat sink/พัดลม", "เพิ่มการระบายอากาศหรือทำหลังคากันแดด" })),

        (Rx(@"\bfan\b"), new(
            "พัดลมระบายความร้อนผิดปกติ",
            "พัดลมไม่หมุนหรือหมุนผิดปกติ จะทำให้ร้อนเกินตามมา",
            new[] { "ฟังเสียง/ดูว่าพัดลมหมุนหรือไม่", "ตรวจสิ่งอุดตันที่พัดลม" },
            new[] { "ทำความสะอาดหรือเปลี่ยนพัดลม" })),

        (Rx(@"comm|communication|\bcan\b|\b485\b"), new(
            "การสื่อสารภายในขัดข้อง",
            "บอร์ดภายในอินเวอร์เตอร์สื่อสารกันไม่ได้ชั่วขณะ",
            new[] { "ดูว่าอินเวอร์เตอร์ยังผลิตไฟปกติหรือไม่", "ดูว่าเกิดซ้ำบ่อยแค่ไหน" },
            new[] { "ถ้าหายเองไม่ต้องทำอะไร", "ถ้าเกิดซ้ำ ปิด-เปิดอินเวอร์เตอร์ (AC แล้ว DC) และอัปเดต firmware" })),

        (Rx(@"relay"), new(
            "รีเลย์ตรวจสอบไม่ผ่าน",
            "การทดสอบรีเลย์ฝั่ง AC ก่อนเชื่อมกริดไม่ผ่าน",
            new[] { "ตรวจแรงดันกริดและสาย N/PE", "ดูว่าเกิดซ้ำหรือไม่" },
            new[] { "ปิด-เปิดอินเวอร์เตอร์", "ถ้าเกิดซ้ำ ติดต่อผู้ผลิต (อาจต้องเปลี่ยนบอร์ด)" })),

        (Rx(@"\bdci\b|dc injection"), new(
            "DC ไหลเข้ากริดเกินเกณฑ์",
            "มีไฟ DC ปนออกฝั่ง AC มากกว่าที่กำหนด",
            new[] { "ดูว่าเกิดซ้ำหรือไม่" },
            new[] { "ปิด-เปิดอินเวอร์เตอร์", "ถ้าเกิดซ้ำ ติดต่อผู้ผลิต" })),

        (Rx(@"\bspd\b|surge"), new(
            "อุปกรณ์กันฟ้าผ่า/ไฟกระชาก (SPD) ทำงาน",
            "SPD อาจเสียหลังฟ้าผ่าหรือไฟกระชาก",
            new[] { "ตรวจหน้าต่างสถานะของ SPD (เขียว/แดง)" },
            new[] { "เปลี่ยน SPD ที่เสีย" })),

        (Rx(@"reverse|polarity"), new(
            "ต่อสาย string กลับขั้ว",
            "ขั้วบวก/ลบของ string สลับกัน",
            new[] { "วัดขั้วของ string ที่แจ้ง" },
            new[] { "ปิด DC แล้วต่อสายให้ถูกขั้ว" })),

        (Rx(@"meter|\bct\b"), new(
            "มิเตอร์/CT สื่อสารไม่ได้หรือผิดปกติ",
            "อินเวอร์เตอร์อ่านค่ามิเตอร์ไม่ได้ — ระบบกันย้อน/ค่าโหลดอาจผิด",
            new[] { "ตรวจสายสื่อสารมิเตอร์ (RS485)", "ตรวจทิศทาง CT" },
            new[] { "ต่อสายสื่อสารใหม่/ตั้ง address มิเตอร์ให้ตรง" })),
    };

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The entry for an alarm name, or null when it isn't one we know.</summary>
    public static Entry? Lookup(string? alarmName)
    {
        if (string.IsNullOrWhiteSpace(alarmName)) return null;
        foreach (var (pattern, entry) in Table)
            if (pattern.IsMatch(alarmName))
                return entry;
        return null;
    }
}
