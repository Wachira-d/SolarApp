using SolarApp.Models.Enums;

namespace SolarApp.Services.PredictiveDiagnostics;

/// <summary>
/// Brand-specific knowledge base for solar inverter diagnostics.
/// Built from field experience, manufacturer documentation, and community reports.
/// Each entry maps a failure pattern → check steps + fix steps.
/// </summary>
public static class InverterKnowledgeBase
{
    public record KnownCase(string[] CheckSteps, string[] FixSteps, string CaseRef);

    // ══════════════════════════════════════════════════════════════
    //  Energy Decline Cases
    // ══════════════════════════════════════════════════════════════

    public static KnownCase GetEnergyDeclineCase(InverterBrand brand, decimal declinePercent)
    {
        var baseChecks = new List<string>
        {
            "ตรวจสอบแผงโซลาร์ ว่ามีฝุ่น/นกขี้/ใบไม้ปิดทับ",
            "ตรวจ string voltage ผ่าน inverter display หรือ cloud",
            "เปรียบเทียบ output แต่ละ string (ถ้า string ไหนต่ำ = มีแผงมีปัญหา)",
            "ตรวจ shading ใหม่ (ต้นไม้โต, อาคารใหม่, สายไฟข้าม)",
        };

        var baseFixes = new List<string>
        {
            "ล้างแผงโซลาร์",
            "ตรวจ hotspot ด้วย thermal camera (ถ้ามี)",
            "ตรวจสาย DC connector ว่าแน่นดีไม่มีสนิม",
        };

        switch (brand)
        {
            case InverterBrand.Huawei:
                baseChecks.Add("ตรวจ Smart PV Optimizer (ถ้ามี) — optimizer ที่เสียจะทำให้ string นั้นผลิตลด");
                baseChecks.Add("ดู Huawei FusionSolar → Smart I-V Curve Diagnosis (ถ้ามีฟีเจอร์)");
                baseFixes.Add("เปลี่ยน Optimizer ตัวที่เสีย (ถ้ามี)");
                baseFixes.Add("Reset inverter: ปิด AC breaker 30 วินาที → เปิด");
                return new(baseChecks.ToArray(), baseFixes.ToArray(),
                    "Huawei: Smart I-V Curve + Optimizer failure common after 3yr");

            case InverterBrand.GoodWe:
                baseChecks.Add("ตรวจ GoodWe SEMS Portal → PV Input → เปรียบเทียบ MPPT แต่ละ channel");
                baseChecks.Add("ตรวจ firmware version — GoodWe ออก FW update แก้ bug MPPT tracking");
                baseFixes.Add("Update firmware ผ่าน SEMS Portal (ถ้ามี version ใหม่)");
                baseFixes.Add("ตรวจ anti-PID module (ถ้าใช้ thin-film panel)");
                return new(baseChecks.ToArray(), baseFixes.ToArray(),
                    "GoodWe: MPPT tracking bug fixed in FW ≥ 5.xx, check SEMS Portal");

            case InverterBrand.Solis:
                baseChecks.Add("ตรวจ SolisCloud → Datalog → ดู PV Input Voltage ว่า match");
                baseChecks.Add("Solis RHI series: ตรวจ built-in RCD trip (error code 509/510)");
                baseFixes.Add("Solis: restart ผ่าน DC switch → รอ 5 นาที → เปิดใหม่");
                return new(baseChecks.ToArray(), baseFixes.ToArray(),
                    "Solis: RCD nuisance trip (509/510) common in humid climate");

            case InverterBrand.Deye:
                baseChecks.Add("ตรวจ Deye Cloud → Device Info → PV String status");
                baseChecks.Add("Deye SUN series: ตรวจ grid-side voltage — voltage สูงเกิน = derating");
                baseFixes.Add("Deye: ถ้า grid voltage >253V → แจ้ง กฟภ./กฟน. ให้ตรวจหม้อแปลง");
                baseFixes.Add("Deye: update firmware — early batches มี bug grid frequency detection");
                return new(baseChecks.ToArray(), baseFixes.ToArray(),
                    "Deye: Grid overvoltage derating common in rural Thailand");

            case InverterBrand.FoxESS:
                baseChecks.Add("ตรวจ FoxESS Cloud → Device Detail → Fault History");
                baseChecks.Add("FoxESS T/H series: ตรวจ DC isolation fault (error F37)");
                baseFixes.Add("FoxESS F37: ตรวจ DC cable insulation, อาจมีน้ำเข้า connector");
                return new(baseChecks.ToArray(), baseFixes.ToArray(),
                    "FoxESS: DC isolation fault (F37) common in monsoon season");

            case InverterBrand.Hoymiles:
                baseChecks.Add("ตรวจ S-Miles Cloud → แต่ละ microinverter output vs rated");
                baseChecks.Add("Hoymiles: ถ้า 1 ตัวผลิตต่ำกว่าตัวอื่น = microinverter หรือแผงตัวนั้นมีปัญหา");
                baseFixes.Add("เปลี่ยน microinverter ตัวที่ output ต่ำ");
                baseFixes.Add("ตรวจ trunk cable connection ระหว่าง microinverters");
                return new(baseChecks.ToArray(), baseFixes.ToArray(),
                    "Hoymiles: Microinverter per-unit comparison in S-Miles Cloud");

            case InverterBrand.Sigenergy:
                baseChecks.Add("ตรวจ SigenCloud → System Energy Flow → per-inverter data");
                baseChecks.Add("Sigenergy: ตรวจ battery SoC pattern — ถ้า battery ไม่ charge เต็ม = PV ลด");
                baseFixes.Add("Sigenergy: check operational mode (AI mode vs manual)");
                return new(baseChecks.ToArray(), baseFixes.ToArray(),
                    "Sigenergy: Check AI mode + battery SoC correlation");

            default:
                return new(baseChecks.ToArray(), baseFixes.ToArray(),
                    $"Generic solar production decline analysis ({declinePercent:0}% drop)");
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  Intermittent Offline Cases
    // ══════════════════════════════════════════════════════════════

    public static KnownCase GetIntermittentOfflineCase(InverterBrand brand)
    {
        var baseChecks = new List<string>
        {
            "ตรวจ WiFi signal strength ที่ตัว data logger / inverter",
            "ตรวจ router ว่ายังทำงานปกติ, ไม่ restart บ่อย",
            "ตรวจ data logger/WiFi stick ว่า LED กระพริบปกติ",
            "ตรวจระยะห่าง router ↔ inverter (ต้อง <15m ถ้ามีกำแพง)",
        };

        var baseFixes = new List<string>
        {
            "ย้าย router ให้ใกล้ inverter ขึ้น หรือติดตั้ง WiFi extender",
            "เปลี่ยน WiFi channel ที่ router (หลีกเลี่ยง channel แออัด)",
            "เปลี่ยนเป็น LAN/Ethernet ถ้า inverter รองรับ (เสถียรกว่า WiFi)",
        };

        switch (brand)
        {
            case InverterBrand.Huawei:
                baseChecks.Add("Huawei: ตรวจ WLAN-FE SmartDongle — LED ต้องเป็นสีเขียว");
                baseChecks.Add("Huawei: ตรวจ SUN2000 WiFi module บน app → WiFi signal quality");
                baseFixes.Add("Huawei: เปลี่ยน SmartDongle (WLAN-FE) ตัวใหม่ — ตัวเก่ามักเสียหลัง 2-3 ปี");
                baseFixes.Add("Huawei: upgrade SmartDongle จาก WLAN-FE → WLAN-FE2 (มี LAN port)");
                return new(baseChecks.ToArray(), baseFixes.ToArray(),
                    "Huawei: SmartDongle WLAN-FE WiFi module known to fail after 2-3yr (firmware loop/LED orange)");

            case InverterBrand.GoodWe:
                baseChecks.Add("GoodWe: ตรวจ WiFi module (EzLink/EzLogger) — LED ต้องเป็นสีน้ำเงิน");
                baseChecks.Add("GoodWe: ตรวจ firmware EzLogger — version เก่ามีปัญหา DHCP timeout");
                baseFixes.Add("GoodWe: Reset EzLogger: ถอดออก → รอ 10 วิ → ใส่กลับ");
                baseFixes.Add("GoodWe: Flash EzLogger firmware ผ่าน PV Master app");
                return new(baseChecks.ToArray(), baseFixes.ToArray(),
                    "GoodWe: EzLink DHCP timeout in FW <2.1, update via PV Master app");

            case InverterBrand.Deye:
                baseChecks.Add("Deye: ตรวจ WiFi logger (ปลั๊กด้านข้าง) — LED กระพริบปกติ?");
                baseChecks.Add("Deye: micro-USB logger บางรุ่นไม่แน่น → หลุดง่าย → offline");
                baseFixes.Add("Deye: ยึด WiFi logger ด้วยสกรู/เทป (ไม่ให้หลุด)");
                baseFixes.Add("Deye: เปลี่ยนจาก WiFi logger → 4G logger (WNET-S2 4G)");
                return new(baseChecks.ToArray(), baseFixes.ToArray(),
                    "Deye: micro-USB WiFi logger loose connection common issue");

            case InverterBrand.Solis:
                baseChecks.Add("Solis: ตรวจ S3-WiFi-ST data logger — LED pattern");
                baseFixes.Add("Solis: re-config WiFi ผ่าน SolisCloud app (Bluetooth)");
                return new(baseChecks.ToArray(), baseFixes.ToArray(),
                    "Solis: S3-WiFi-ST factory reset via long-press 10s");

            case InverterBrand.FoxESS:
                baseChecks.Add("FoxESS: ตรวจ WiFi module → LED สีเขียว = online, สีแดง = no WiFi");
                baseFixes.Add("FoxESS: re-pair WiFi module ผ่าน FoxESS app → Settings → WiFi");
                return new(baseChecks.ToArray(), baseFixes.ToArray(),
                    "FoxESS: WiFi module re-pair required after router change");

            case InverterBrand.Hoymiles:
                baseChecks.Add("Hoymiles: ตรวจ DTU (Data Transfer Unit) ว่ามีไฟเลี้ยง");
                baseChecks.Add("Hoymiles: DTU-Pro → ดูจำนวน microinverters ที่ connect ได้ vs ทั้งหมด");
                baseFixes.Add("Hoymiles: เปลี่ยน DTU ถ้า firmware เก่า (DTU-Pro > DTU-WLite → ดีกว่า)");
                baseFixes.Add("Hoymiles: ตรวจ PLC signal — microinverter ที่อยู่ไกล DTU มักขาดสัญญาณ");
                return new(baseChecks.ToArray(), baseFixes.ToArray(),
                    "Hoymiles: DTU PLC signal degrades over long cable runs (>30m)");

            default:
                return new(baseChecks.ToArray(), baseFixes.ToArray(),
                    "Generic: Intermittent WiFi/communication failure pattern");
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  API Error Pattern Cases
    // ══════════════════════════════════════════════════════════════

    public static KnownCase GetApiErrorCase(InverterBrand brand, string errorSummary)
    {
        var checks = new List<string>
        {
            "ตรวจ credentials ว่ายังใช้งานได้ — ลอง login ผ่าน cloud portal",
            "ตรวจสถานะ cloud vendor (status page / community)",
            "ตรวจ API key/token ว่าหมดอายุหรือยัง",
        };
        var fixes = new List<string>
        {
            "Re-enter API credentials ใน Installation settings",
            "กด Test Connection เพื่อ verify",
        };

        if (errorSummary.Contains("401") || errorSummary.Contains("403") || errorSummary.Contains("[AUTH"))
        {
            checks.Insert(0, "⚠ AUTH ERROR — มีโอกาสสูงที่ token/password หมดอายุ");
            fixes.Insert(0, "เปลี่ยน password ที่ cloud portal → update ใน SolarApp ทันที");

            if (brand == InverterBrand.Huawei)
            {
                checks.Add("Huawei NBI: API key expire ทุก 6 เดือน — ต้อง renew ที่ developer portal");
                fixes.Add("Huawei: ไป iMaster NetEco → API Management → Renew key");
            }
        }

        if (errorSummary.Contains("TIMEOUT") || errorSummary.Contains("timeout"))
        {
            checks.Add("ตรวจ internet connection ของ SolarApp server");
            checks.Add("ตรวจว่า cloud vendor ไม่ได้ maintenance");
            fixes.Add("ถ้า timeout บ่อย → เพิ่ม sync interval (15 → 30 นาที)");
        }

        if (errorSummary.Contains("429") || errorSummary.Contains("RATE"))
        {
            checks.Add("ถูก rate limit — ส่ง request ถี่เกินไป");
            fixes.Add("เพิ่ม sync interval ใน Admin → Sync Schedule");
            if (brand == InverterBrand.Huawei)
                fixes.Add("Huawei: ต้อง ≥1 request/minute — ระบบจัดการอัตโนมัติแล้ว");
        }

        return new(checks.ToArray(), fixes.ToArray(),
            $"{brand} API error trend — errors: {(errorSummary.Length > 80 ? errorSummary[..80] : errorSummary)}");
    }

    // ══════════════════════════════════════════════════════════════
    //  Age-Based Component Cases
    // ══════════════════════════════════════════════════════════════

    public static KnownCase GetAgingCase(InverterBrand brand, string component, double ageYears)
    {
        if (component == "fan")
        {
            var checks = new List<string>
            {
                $"ตรวจเสียงพัดลม inverter (อายุ {ageYears:0.1} ปี)",
                "พัดลมที่เริ่มเสีย: เสียงดังกว่าปกติ / สั่น / หมุนช้า",
                "ตรวจอุณหภูมิ inverter ผ่าน cloud — ถ้า >70°C = พัดลมอาจมีปัญหา",
                "ดูว่า inverter มี thermal derating (output ลดช่วงเที่ยง) หรือไม่",
            };
            var fixes = new List<string>
            {
                "เปลี่ยนพัดลม inverter (ชิ้นส่วนราคา 500-2,000 บาท)",
                "ทำความสะอาดฝุ่นที่ heat sink ด้วยแปรงนุ่มหรือลมเป่า",
                "ตรวจ air filter (ถ้ามี) — เปลี่ยนทุก 1-2 ปี",
            };

            if (brand == InverterBrand.Huawei)
            {
                checks.Add("Huawei SUN2000: พัดลมอยู่ด้านล่าง — ฝุ่นสะสมง่าย");
                fixes.Add("Huawei: สั่งพัดลมผ่าน Huawei service center");
            }

            return new(checks.ToArray(), fixes.ToArray(),
                $"Inverter fan aging ({ageYears:0.1}yr) — expected life 5-8yr in tropical climate");
        }

        // Capacitor aging
        var capChecks = new[]
        {
            $"ตรวจ output power vs rated — ถ้า <80% อาจเป็นสัญญาณ capacitor เสื่อม",
            "ตรวจ AC output waveform (ถ้ามีเครื่องมือ) — ripple สูง = capacitor เสื่อม",
            "ตรวจ error log — error เกี่ยวกับ DC bus voltage fluctuation",
            "Capacitor เสื่อม → อาการ: power output ลดลงเรื่อยๆ โดยเฉพาะช่วงบ่าย",
        };
        var capFixes = new[]
        {
            "⚠ การเปลี่ยน capacitor ต้องทำโดยช่างที่ได้รับการฝึกจาก vendor",
            "ติดต่อ vendor service center เพื่อประเมินอายุ capacitor",
            "พิจารณาเปลี่ยน inverter ตัวใหม่ ถ้าอายุเกิน 10 ปี (คุ้มกว่าซ่อม)",
            "ใน warranty → ให้ vendor เปลี่ยนฟรี",
        };

        return new(capChecks, capFixes,
            $"Capacitor aging ({ageYears:0.1}yr) — electrolytic cap life 7-12yr, shorter in hot climate");
    }

    // ══════════════════════════════════════════════════════════════
    //  Brand-Specific Safety Recalls & Known Issues
    // ══════════════════════════════════════════════════════════════

    public static KnownCase? GetSafetyRecallCase(InverterBrand brand, string? model, DateTime installDate)
    {
        switch (brand)
        {
            case InverterBrand.GoodWe:
                // RECALL Sept 2025: EHB + GEH hybrid inverters sold May 2021 - Sept 2025
                if (installDate >= new DateTime(2021, 5, 1) && installDate <= new DateTime(2025, 9, 30)
                    && (model == null || model.Contains("EHB", StringComparison.OrdinalIgnoreCase)
                        || model.Contains("GEH", StringComparison.OrdinalIgnoreCase)
                        || model.Contains("hybrid", StringComparison.OrdinalIgnoreCase)))
                {
                    return new(
                        new[]
                        {
                            "⚠ SAFETY RECALL: GoodWe EHB/GEH hybrid inverters (May 2021 - Sept 2025)",
                            "ปัญหา: Bypass switch อาจ export ไฟขณะ islanding → เสี่ยงไฟดูด",
                            "ตรวจ firmware version — ต้องเป็น v050531 ขึ้นไป",
                            "ห้ามใช้ bypass switch จนกว่าจะ patch firmware",
                        },
                        new[]
                        {
                            "Update firmware เป็น v050531+ (ทำได้ remote ผ่าน SEMS Portal)",
                            "ถ้า update remote ไม่ได้ → ส่งช่างไป flash ที่หน้างาน",
                            "ห้ามใช้ bypass switch จนกว่าจะ update",
                            "แจ้งลูกค้าเรื่อง recall อย่างเป็นทางการ",
                        },
                        "ACCC Safety Recall: GoodWe EHB/GEH hybrid inverters — bypass switch electrocution risk"
                    );
                }
                break;

            case InverterBrand.Sigenergy:
                // RECALL 2025: Single-phase 8/10/12kW controllers — AC plug overheat
                if (model == null || model.Contains("8kW") || model.Contains("10kW") || model.Contains("12kW")
                    || model.Contains("single", StringComparison.OrdinalIgnoreCase))
                {
                    return new(
                        new[]
                        {
                            "⚠ SAFETY RECALL: Sigenergy single-phase 8/10/12kW controllers",
                            "ปัญหา: AC quick-connect plug อาจร้อนจัดจนละลาย → เสี่ยงไฟไหม้",
                            "ตรวจ AC connection point ว่ามีรอยไหม้/เปลี่ยนสี",
                            "ตรวจ firmware — version ใหม่ throttle output เพื่อป้องกัน",
                        },
                        new[]
                        {
                            "Update firmware ทันที (throttle protection)",
                            "ติดต่อ Sigenergy ขอเปลี่ยน AC plug ฟรี",
                            "ตรวจ ferrule crimping ที่ AC terminal",
                            "ถ้าเห็นรอยไหม้ → ปิดระบบทันที แจ้ง Sigenergy",
                        },
                        "ACCC Recall: Sigenergy single-phase controllers — AC plug overheat/fire risk (2025)"
                    );
                }
                break;

            case InverterBrand.Huawei:
                // Known: SmartDongle WLAN-FE failure after 2-3 years (not a recall, but widespread)
                var age = (DateTime.UtcNow - installDate).TotalDays / 365.25;
                if (age >= 2 && age <= 4)
                {
                    return new(
                        new[]
                        {
                            "Huawei SmartDongle WLAN-FE: มีรายงานเสียบ่อยหลัง 2-3 ปี",
                            "อาการ: LED สีส้ม/กระพริบ, offline บ่อย, firmware loop",
                            "ตรวจ SmartDongle LED: เขียว = OK, ส้ม = มีปัญหา, แดง = fail",
                        },
                        new[]
                        {
                            "เปลี่ยน SmartDongle WLAN-FE → WLAN-FE2 (มี Ethernet port ด้วย)",
                            "ใช้ Ethernet แทน WiFi (เสถียรกว่ามาก)",
                            "ติดต่อ Huawei service center ขอเปลี่ยน",
                        },
                        "Huawei SmartDongle WLAN-FE: widespread failure at 2-3yr — recommend upgrade to WLAN-FE2"
                    );
                }
                break;
        }
        return null;
    }

    // ══════════════════════════════════════════════════════════════
    //  Brand-Specific Common Fault Codes
    // ══════════════════════════════════════════════════════════════

    public static KnownCase? MatchFaultCode(InverterBrand brand, string errorText)
    {
        var upper = errorText.ToUpperInvariant();

        if (brand == InverterBrand.Huawei)
        {
            if (upper.Contains("2063") || upper.Contains("INSULATION"))
                return new(
                    new[] { "Huawei 2063: Low insulation resistance", "ตรวจสาย DC ว่ามีน้ำเข้า connector", "ตรวจแผงว่ามี hotspot/crack" },
                    new[] { "ตรวจ MC4 connector ทุกจุด — ถอดเช็คความชื้น", "วัด insulation resistance ด้วย megger", "เปลี่ยนสาย DC ที่สงสัย" },
                    "Huawei fault 2063: DC insulation resistance low — check wiring and connector moisture");

            if (upper.Contains("2064") || upper.Contains("RESIDUAL CURRENT"))
                return new(
                    new[] { "Huawei 2064: High residual current — earth leakage ⚠ safety critical", "ตรวจสายดิน", "ตรวจ ELCB/RCD ว่าไม่ trip" },
                    new[] { "ตรวจ grounding ทั้งระบบ", "ตรวจ DC cable ว่าไม่สัมผัสโลหะ", "ถ้าเกิดซ้ำ → ส่งช่างตรวจ earth leakage path" },
                    "Huawei fault 2064: Residual current — safety-critical earth leakage");

            if (upper.Contains("2031") || upper.Contains("ARC"))
                return new(
                    new[] { "Huawei 2031: DC Arc Fault ⚠ FIRE RISK", "ตรวจ MC4 connector ว่าแน่นดี", "ตรวจสาย DC ว่าไม่มีรอยขาด/ถลอก" },
                    new[] { "ปิดระบบทันที ถ้ามี arc จริง", "ตรวจ MC4 ทุกจุด — ขันให้แน่น", "เปลี่ยน MC4 ที่สงสัย (ไม่ควรใช้ซ้ำ)" },
                    "Huawei fault 2031: DC arc fault — FIRE RISK, inspect all MC4 connectors immediately");
        }

        if (brand == InverterBrand.GoodWe)
        {
            if (upper.Contains("E010") || upper.Contains("RESIDUAL"))
                return new(
                    new[] { "GoodWe E010: Residual current fault", "ตรวจ MC4 ว่ามีน้ำเข้า", "มักเกิดหลังฝนตก" },
                    new[] { "ทำให้ MC4 แห้ง → ใช้ silicone seal", "ตรวจ DC cable route ว่าน้ำไม่ไหลผ่าน" },
                    "GoodWe E010: Residual current — usually MC4 water ingress in monsoon");

            if (upper.Contains("CODE 16") || upper.Contains("FAN"))
                return new(
                    new[] { "GoodWe Code 16: External fan failure", "ตรวจเสียงพัดลม", "ตรวจว่าพัดลมหมุนหรือไม่" },
                    new[] { "เปลี่ยนพัดลม", "ทำความสะอาดฝุ่นที่ heat sink" },
                    "GoodWe Code 16: Fan failure — replace fan module");
        }

        if (brand == InverterBrand.Deye)
        {
            if (upper.Contains("F06") || upper.Contains("OVERVOLTAGE") || upper.Contains("GRID.*VOLT"))
                return new(
                    new[] { "Deye F06: Grid overvoltage", "ตรวจ voltage ที่หน้า meter (>253V = ผิดปกติ)", "มักเกิดในพื้นที่ชนบท/หม้อแปลงไกล" },
                    new[] { "แจ้ง กฟภ./กฟน. ให้ตรวจ tap หม้อแปลง", "ปรับ grid voltage upper limit ใน inverter (ถ้ากฎหมายอนุญาต)" },
                    "Deye F06: Grid overvoltage — common in rural Thailand, report to utility for transformer tap adjustment");

            if (upper.Contains("F07") || upper.Contains("OVER.*TEMP") || upper.Contains("TEMPERATURE"))
                return new(
                    new[] { "Deye F07: Over-temperature", "ตรวจอุณหภูมิ inverter", "ตรวจช่องระบายอากาศ" },
                    new[] { "รอ 30-60 นาทีให้เย็นลง", "ติดพัดลมเสริม/หลังคาบังแดด", "ทำความสะอาดช่องระบายอากาศ" },
                    "Deye F07: Over-temperature — common in Thai climate, improve ventilation");

            if (upper.Contains("W03") || upper.Contains("CAN") || upper.Contains("BMS"))
                return new(
                    new[] { "Deye W03: CAN communication failure with battery BMS", "ตรวจสาย CAN cable", "ตรวจ battery BMS ว่ายังทำงาน" },
                    new[] { "ตรวจ CAN cable connector ทั้งสองฝั่ง", "เปลี่ยนสาย CAN ถ้าสงสัย", "Restart battery + inverter" },
                    "Deye W03: CAN comm failure — check cable connections to battery BMS");
        }

        if (brand == InverterBrand.FoxESS)
        {
            if (upper.Contains("F37") || upper.Contains("ISOLATION") || upper.Contains("INSULATION"))
                return new(
                    new[] { "FoxESS F37: DC isolation fault", "ตรวจ DC cable ว่ามีน้ำเข้า", "มักเกิดช่วงฤดูฝน" },
                    new[] { "ตรวจ MC4 ทุกจุด", "วัด insulation resistance", "ซีล conduit ด้วย silicone" },
                    "FoxESS F37: DC isolation fault — monsoon season common issue");
        }

        return null;
    }

    // ══════════════════════════════════════════════════════════════
    //  Recurring Fault Cases
    // ══════════════════════════════════════════════════════════════

    public static KnownCase GetRecurringFaultCase(InverterBrand brand, string alertTitle)
    {
        var checks = new List<string>
        {
            $"Alert \"{alertTitle}\" เกิดซ้ำหลายครั้ง — ต้นเหตุยังไม่ได้แก้",
            "ตรวจ timeline ว่า เกิดซ้ำทุกวัน? ทุกสัปดาห์? เฉพาะบ่าย?",
            "ถ้าเกิดเฉพาะช่วงเที่ยง-บ่าย → อาจเป็นปัญหา thermal / overvoltage",
            "ถ้าเกิดเฉพาะช่วงเช้า/เย็น → อาจเป็น low-voltage / grid issue",
        };
        var fixes = new List<string>();

        if (alertTitle.Contains("Offline") || alertTitle.Contains("offline"))
        {
            checks.Add("Offline ซ้ำ → WiFi/data logger มีปัญหา OR inverter restart เอง");
            fixes.Add("เปลี่ยน data logger / WiFi module");
            fixes.Add("ตรวจ AC breaker ว่าไม่ trip (MCB ที่เก่ามักจะ nuisance trip)");
            fixes.Add("ติดตั้ง UPS ให้ router (ถ้าไฟดับบ่อย → WiFi ตาม)");
        }

        if (alertTitle.Contains("Faulty") || alertTitle.Contains("Error"))
        {
            checks.Add("Error ซ้ำ → อ่าน error code ให้ชัดเจน แล้ว search ใน brand manual");
            fixes.Add("ถ่ายรูป error code → ส่งให้ vendor service center");
            fixes.Add("ตรวจ DC string voltage — ถ้า string ไหนต่ำ = มีแผงเสียหรือสายหลวม");

            if (brand == InverterBrand.Huawei)
            {
                fixes.Add("Huawei: error ซ้ำ → ตรวจ COM port → SUN2000 app → Fault Record");
                fixes.Add("Huawei: fault code 2001/2002 → DC arc fault detection (AFCI) — ตรวจ connector MC4");
            }
            if (brand == InverterBrand.GoodWe)
            {
                fixes.Add("GoodWe: error ซ้ำ → PV Master app → Error Log → filter by code");
                fixes.Add("GoodWe: error E010 (residual current) → ตรวจ MC4 water ingress");
            }
            if (brand == InverterBrand.Deye)
            {
                fixes.Add("Deye: F06 (grid overvoltage) ซ้ำ → แจ้ง กฟภ. ตรวจหม้อแปลง");
                fixes.Add("Deye: F16 (DC insulation) ซ้ำ → ตรวจสาย DC ว่ามีน้ำเข้า connector");
            }
        }

        if (alertTitle.Contains("MaintenanceDue") || alertTitle.Contains("Cleaning"))
        {
            checks.Add("การแจ้งเตือนล้างแผงซ้ำ → อาจยังไม่ได้ทำจริง หรือยังไม่ได้ update สถานะ");
            fixes.Add("ทำ work order ให้ช่างไปล้างแผง → mark complete ใน SolarApp");
        }

        if (fixes.Count == 0)
        {
            fixes.Add("วิเคราะห์ pattern เพิ่ม: เกิดเวลาเดียวกันทุกวัน?");
            fixes.Add("สร้าง work order เพื่อส่งช่างไปตรวจสอบที่หน้างาน");
        }

        return new(checks.ToArray(), fixes.ToArray(),
            $"{brand}: Recurring alert \"{(alertTitle.Length > 50 ? alertTitle[..50] : alertTitle)}\"");
    }
}
