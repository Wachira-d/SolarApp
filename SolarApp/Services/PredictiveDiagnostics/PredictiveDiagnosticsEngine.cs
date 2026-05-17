using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Services.PredictiveDiagnostics;

public class PredictiveDiagnosticsEngine
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<PredictiveDiagnosticsEngine> _logger;

    public PredictiveDiagnosticsEngine(ApplicationDbContext context, ILogger<PredictiveDiagnosticsEngine> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<List<PredictiveAlert>> AnalyzeAllInstallationsAsync()
    {
        var alerts = new List<PredictiveAlert>();
        // Engine is pure read — no entity mutations. AsNoTracking eliminates
        // EF change-tracker overhead which was costing ~30% on each query.
        var installations = await _context.Installations
            .AsNoTracking()
            .Where(i => i.Status != InstallationStatus.Inactive)
            .ToListAsync();

        foreach (var inst in installations)
        {
            try
            {
                alerts.AddRange(await AnalyzeEnergyDeclineAsync(inst));
                alerts.AddRange(await AnalyzeIntermittentOfflineAsync(inst));
                alerts.AddRange(await AnalyzeApiErrorTrendAsync(inst));
                alerts.AddRange(AnalyzeAgeBasedRisks(inst));
                alerts.AddRange(AnalyzeEnvironmentalRisks(inst));
                alerts.AddRange(await AnalyzeRepeatingFaultsAsync(inst));
                alerts.AddRange(AnalyzeSafetyRecalls(inst));
                alerts.AddRange(await AnalyzeFaultCodePatternsAsync(inst));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Predictive analysis failed for {Id}", inst.Id);
            }
        }

        return alerts;
    }

    // ══════════════════════════════════════════════════════════════
    //  Pattern 1: Energy Production Decline
    // ══════════════════════════════════════════════════════════════

    private async Task<List<PredictiveAlert>> AnalyzeEnergyDeclineAsync(Installation inst)
    {
        var alerts = new List<PredictiveAlert>();
        var now = DateTime.UtcNow;

        // Compare last 7 days vs same 7 days previous month
        var recentStart = now.AddDays(-7);
        var previousStart = now.AddDays(-37);
        var previousEnd = now.AddDays(-30);

        var recentEnergy = await _context.EnergyData.AsNoTracking()
            .Where(e => e.InstallationId == inst.Id && e.Timestamp >= recentStart && e.EnergyGenerationKwh > 0)
            .AverageAsync(e => (decimal?)e.EnergyGenerationKwh) ?? 0;

        var previousEnergy = await _context.EnergyData.AsNoTracking()
            .Where(e => e.InstallationId == inst.Id && e.Timestamp >= previousStart && e.Timestamp <= previousEnd && e.EnergyGenerationKwh > 0)
            .AverageAsync(e => (decimal?)e.EnergyGenerationKwh) ?? 0;

        if (previousEnergy <= 0 || recentEnergy <= 0) return alerts;

        var declinePercent = (1 - recentEnergy / previousEnergy) * 100;

        // Normal degradation: 0.5-0.7% per year = ~0.06% per month
        // >15% decline in a month = abnormal
        if (declinePercent >= 30)
        {
            var kb = InverterKnowledgeBase.GetEnergyDeclineCase(inst.InverterBrand, declinePercent);
            alerts.Add(new PredictiveAlert
            {
                InstallationId = inst.Id,
                SiteName = inst.SiteName,
                Prediction = $"ผลิตไฟลดลง {declinePercent:0}% ใน 30 วัน — อาจมีปัญหาที่แผง/inverter",
                Evidence = $"ค่าเฉลี่ย 7 วันล่าสุด: {recentEnergy:0.0} kWh/day vs " +
                           $"30 วันก่อน: {previousEnergy:0.0} kWh/day (ลดลง {declinePercent:0.0}%)",
                CheckSteps = kb.CheckSteps,
                FixSteps = kb.FixSteps,
                RiskLevel = "High",
                KnownCaseRef = kb.CaseRef,
            });
        }
        else if (declinePercent >= 15)
        {
            var kb = InverterKnowledgeBase.GetEnergyDeclineCase(inst.InverterBrand, declinePercent);
            alerts.Add(new PredictiveAlert
            {
                InstallationId = inst.Id,
                SiteName = inst.SiteName,
                Prediction = $"ผลิตไฟลดลง {declinePercent:0}% — เริ่มต่ำกว่าปกติ",
                Evidence = $"ค่าเฉลี่ย 7 วันล่าสุด: {recentEnergy:0.0} kWh/day vs " +
                           $"30 วันก่อน: {previousEnergy:0.0} kWh/day",
                CheckSteps = kb.CheckSteps,
                FixSteps = kb.FixSteps,
                RiskLevel = "Medium",
                KnownCaseRef = kb.CaseRef,
            });
        }

        return alerts;
    }

    // ══════════════════════════════════════════════════════════════
    //  Pattern 2: Intermittent Offline (precursor to total failure)
    // ══════════════════════════════════════════════════════════════

    private async Task<List<PredictiveAlert>> AnalyzeIntermittentOfflineAsync(Installation inst)
    {
        var alerts = new List<PredictiveAlert>();
        var weekAgo = DateTime.UtcNow.AddDays(-7);

        var offlineCount = await _context.InverterApiLogs.AsNoTracking()
            .Where(l => l.InstallationId == inst.Id && l.RequestTime >= weekAgo
                && l.IsSuccess && l.ErrorMessage != null && l.ErrorMessage.Contains("[DEVICE OFFLINE]"))
            .CountAsync();

        var totalCount = await _context.InverterApiLogs.AsNoTracking()
            .Where(l => l.InstallationId == inst.Id && l.RequestTime >= weekAgo && l.IsSuccess)
            .CountAsync();

        if (totalCount < 10) return alerts;

        var offlinePercent = (double)offlineCount / totalCount * 100;

        // If 30-70% of syncs show offline during daytime = intermittent problem
        if (offlinePercent is >= 30 and < 80)
        {
            var kb = InverterKnowledgeBase.GetIntermittentOfflineCase(inst.InverterBrand);
            alerts.Add(new PredictiveAlert
            {
                InstallationId = inst.Id,
                SiteName = inst.SiteName,
                Prediction = $"WiFi/สัญญาณขาดๆ หายๆ — อาจ offline ถาวรเร็วๆ นี้",
                Evidence = $"7 วันที่ผ่านมา: {offlineCount}/{totalCount} ครั้ง ({offlinePercent:0}%) " +
                           "อ่านค่าได้เป็น [DEVICE OFFLINE] " +
                           "→ สัญญาณไม่เสถียร มักจะ offline ถาวรภายใน 1-2 สัปดาห์",
                CheckSteps = kb.CheckSteps,
                FixSteps = kb.FixSteps,
                RiskLevel = offlinePercent >= 50 ? "High" : "Medium",
                KnownCaseRef = kb.CaseRef,
            });
        }

        return alerts;
    }

    // ══════════════════════════════════════════════════════════════
    //  Pattern 3: API Error Rate Trending Up
    // ══════════════════════════════════════════════════════════════

    private async Task<List<PredictiveAlert>> AnalyzeApiErrorTrendAsync(Installation inst)
    {
        var alerts = new List<PredictiveAlert>();
        var now = DateTime.UtcNow;
        var recentWindow = now.AddDays(-3);
        var previousWindow = now.AddDays(-10);

        var recentErrors = await _context.InverterApiLogs.AsNoTracking()
            .Where(l => l.InstallationId == inst.Id && l.RequestTime >= recentWindow && !l.IsSuccess)
            .CountAsync();
        var recentTotal = await _context.InverterApiLogs.AsNoTracking()
            .Where(l => l.InstallationId == inst.Id && l.RequestTime >= recentWindow)
            .CountAsync();

        var prevErrors = await _context.InverterApiLogs.AsNoTracking()
            .Where(l => l.InstallationId == inst.Id && l.RequestTime >= previousWindow && l.RequestTime < recentWindow && !l.IsSuccess)
            .CountAsync();
        var prevTotal = await _context.InverterApiLogs.AsNoTracking()
            .Where(l => l.InstallationId == inst.Id && l.RequestTime >= previousWindow && l.RequestTime < recentWindow)
            .CountAsync();

        if (recentTotal < 5 || prevTotal < 5) return alerts;

        var recentRate = (double)recentErrors / recentTotal * 100;
        var prevRate = (double)prevErrors / prevTotal * 100;

        // Error rate doubled and now above 20%
        if (recentRate >= 20 && recentRate >= prevRate * 1.8)
        {
            // Check what type of errors
            var topErrors = await _context.InverterApiLogs.AsNoTracking()
                .Where(l => l.InstallationId == inst.Id && l.RequestTime >= recentWindow && !l.IsSuccess && l.ErrorMessage != null)
                .GroupBy(l => l.ErrorMessage!.Substring(0, Math.Min(l.ErrorMessage!.Length, 50)))
                .Select(g => new { Error = g.Key, Count = g.Count() })
                .OrderByDescending(g => g.Count)
                .Take(3)
                .ToListAsync();

            var errorSummary = string.Join(", ", topErrors.Select(e => $"{e.Error} ({e.Count}x)"));
            var kb = InverterKnowledgeBase.GetApiErrorCase(inst.InverterBrand, errorSummary);

            alerts.Add(new PredictiveAlert
            {
                InstallationId = inst.Id,
                SiteName = inst.SiteName,
                Prediction = $"API error rate เพิ่มขึ้นผิดปกติ ({recentRate:0}%) — อาจ credential หมดอายุ หรือ cloud มีปัญหา",
                Evidence = $"3 วันล่าสุด: {recentErrors}/{recentTotal} errors ({recentRate:0}%) " +
                           $"vs 7 วันก่อนหน้า: {prevErrors}/{prevTotal} ({prevRate:0}%)\n" +
                           $"Error หลัก: {errorSummary}",
                CheckSteps = kb.CheckSteps,
                FixSteps = kb.FixSteps,
                RiskLevel = recentRate >= 50 ? "High" : "Medium",
                KnownCaseRef = kb.CaseRef,
            });
        }

        return alerts;
    }

    // ══════════════════════════════════════════════════════════════
    //  Pattern 4: Age-Based Component Risk
    // ══════════════════════════════════════════════════════════════

    private List<PredictiveAlert> AnalyzeAgeBasedRisks(Installation inst)
    {
        var alerts = new List<PredictiveAlert>();
        var ageYears = (DateTime.UtcNow - inst.InstallationDate).TotalDays / 365.25;

        // Inverter fan typically fails at 5-7 years
        if (ageYears is >= 4.5 and < 6)
        {
            var kb = InverterKnowledgeBase.GetAgingCase(inst.InverterBrand, "fan", ageYears);
            alerts.Add(new PredictiveAlert
            {
                InstallationId = inst.Id,
                SiteName = inst.SiteName,
                Prediction = $"Inverter อายุ {ageYears:0.0} ปี — ใกล้ถึงจุดเสี่ยงพัดลมเสีย (5-7 ปี)",
                Evidence = $"ติดตั้งวันที่ {inst.InstallationDate:dd/MM/yyyy} ({ageYears:0.1} ปี)\n" +
                           "สถิติจากอุตสาหกรรม: พัดลม inverter มักเริ่มมีปัญหาที่ 5-7 ปี " +
                           "ถ้าพังจะทำให้ inverter ร้อนเกิน → shutdown → ผลิตไฟลดลง",
                CheckSteps = kb.CheckSteps,
                FixSteps = kb.FixSteps,
                RiskLevel = ageYears >= 5.5 ? "High" : "Medium",
                KnownCaseRef = kb.CaseRef,
            });
        }

        // Electrolytic capacitors: 7-10 years
        if (ageYears >= 7 && ageYears < 10)
        {
            var kb = InverterKnowledgeBase.GetAgingCase(inst.InverterBrand, "capacitor", ageYears);
            alerts.Add(new PredictiveAlert
            {
                InstallationId = inst.Id,
                SiteName = inst.SiteName,
                Prediction = $"Inverter อายุ {ageYears:0.0} ปี — ใกล้ถึงจุดเสี่ยง capacitor เสื่อม",
                Evidence = $"ติดตั้งวันที่ {inst.InstallationDate:dd/MM/yyyy} ({ageYears:0.1} ปี)\n" +
                           "Electrolytic capacitor ใน inverter มีอายุใช้งาน 7-12 ปี " +
                           "โดยเฉพาะในสภาพอากาศร้อน อาการเริ่มแรก: output power ลดลง, ripple เพิ่ม",
                CheckSteps = kb.CheckSteps,
                FixSteps = kb.FixSteps,
                RiskLevel = ageYears >= 8.5 ? "High" : "Medium",
                KnownCaseRef = kb.CaseRef,
            });
        }

        // Inverter warranty expiring + high age
        if (inst.InverterWarrantyExpiryDate.HasValue)
        {
            var daysLeft = (inst.InverterWarrantyExpiryDate.Value - DateTime.UtcNow).TotalDays;
            if (daysLeft is > 0 and <= 180 && ageYears >= 4)
            {
                alerts.Add(new PredictiveAlert
                {
                    InstallationId = inst.Id,
                    SiteName = inst.SiteName,
                    Prediction = $"รับประกัน inverter เหลืออีก {daysLeft:0} วัน — ควร inspect ก่อนหมดประกัน",
                    Evidence = $"Warranty หมด: {inst.InverterWarrantyExpiryDate:dd/MM/yyyy}\n" +
                               $"อายุ inverter: {ageYears:0.1} ปี\n" +
                               "ถ้ามีปัญหาควรเคลมก่อนหมดประกัน รวมถึงพัดลม, board, หน้าจอ",
                    CheckSteps = new[]
                    {
                        "ตรวจสอบเสียงพัดลม (ดังผิดปกติ?)",
                        "ตรวจสอบหน้าจอ LCD (ใช้งานได้ปกติ?)",
                        "ตรวจ error log ใน cloud ย้อนหลัง 6 เดือน",
                        "วัดค่า output power เทียบกับ rated capacity",
                        "ถ่ายรูปป้าย serial number ไว้สำหรับเคลม"
                    },
                    FixSteps = new[]
                    {
                        "ติดต่อ vendor เพื่อ claim warranty inspection",
                        "เตรียมเอกสาร: ใบเสร็จ, S/N, log ย้อนหลัง",
                        "ถ้าพัดลมเสียงดัง → ขอเปลี่ยนพัดลมฟรีตามประกัน",
                        "ถ้ามี error code → แจ้ง vendor ให้ตรวจ board"
                    },
                    RiskLevel = daysLeft <= 60 ? "High" : "Medium",
                    KnownCaseRef = $"Inverter warranty expires {inst.InverterWarrantyExpiryDate:dd/MM/yyyy}",
                });
            }
        }

        return alerts;
    }

    // ══════════════════════════════════════════════════════════════
    //  Pattern 5: Environmental / Seasonal Risks
    // ══════════════════════════════════════════════════════════════

    private static List<PredictiveAlert> AnalyzeEnvironmentalRisks(Installation inst)
    {
        var alerts = new List<PredictiveAlert>();
        var nowUtc = DateTime.UtcNow;
        var thMonth = (nowUtc.AddHours(7)).Month; // Thailand local month

        // Monsoon season: May-October → lightning + water damage risk
        // Skip sites < 90 days old — nothing to inspect yet on a fresh install
        var daysSinceInstall = (DateTime.UtcNow - inst.InstallationDate).TotalDays;
        if (thMonth is >= 5 and <= 6 && daysSinceInstall >= 90)
        {
            // Use install date as fallback if never cleaned (not a 999 sentinel)
            var referenceDate = inst.LastCleaningDate ?? inst.InstallationDate;
            var daysSinceLastCleaning = (DateTime.UtcNow - referenceDate).TotalDays;
            var refLabel = inst.LastCleaningDate.HasValue ? "ล้างแผงครั้งสุดท้าย" : "ตั้งแต่ติดตั้ง (ยังไม่เคยล้าง)";

            if (daysSinceLastCleaning > 120)
            {
                alerts.Add(new PredictiveAlert
                {
                    InstallationId = inst.Id,
                    SiteName = inst.SiteName,
                    Prediction = "เข้าฤดูฝน — ควรตรวจสอบก่อนมรสุม",
                    Evidence = $"เดือน {thMonth} (ต้นฤดูฝน), {refLabel} {daysSinceLastCleaning:0} วันก่อน\n" +
                               "ก่อนมรสุมควรตรวจ: สายดิน, surge protection, ระบบ drainage หลังคา, " +
                               "แผงโซลาร์ยึดแน่น (ลมแรง), และ conduit ป้องกันน้ำเข้า",
                    CheckSteps = new[]
                    {
                        "ตรวจสายดิน (grounding) ว่ายังต่ออยู่ครบ",
                        "ตรวจ surge protection device (SPD) — ไฟ LED สถานะปกติ?",
                        "ตรวจท่อร้อยสาย (conduit) ว่าไม่มีรอยแตก",
                        "ตรวจ mounting/clamp ว่ายึดแน่น (เตรียมรับลมแรง)",
                        "ตรวจ DC connector ว่าไม่มีสนิม/ความชื้น"
                    },
                    FixSteps = new[]
                    {
                        "ขันน็อต mounting ให้แน่น, เปลี่ยน clamp ที่ผุ",
                        "เปลี่ยน SPD ถ้าไฟแสดงเป็นสีแดง",
                        "ซีลรอยแตกที่ conduit ด้วย silicone",
                        "ทำความสะอาดแผงก่อนฤดูฝน (ฝุ่น+คราบ)",
                        "ล้าง gutter/drainage หลังคาไม่ให้อุดตัน"
                    },
                    RiskLevel = "Medium",
                    KnownCaseRef = "Pre-monsoon inspection checklist (Thailand May-Oct)",
                });
            }
        }

        // Extreme heat: March-April → inverter thermal throttling risk
        if (thMonth is >= 3 and <= 4)
        {
            var ageYears = (DateTime.UtcNow - inst.InstallationDate).TotalDays / 365.25;
            if (ageYears >= 3)
            {
                alerts.Add(new PredictiveAlert
                {
                    InstallationId = inst.Id,
                    SiteName = inst.SiteName,
                    Prediction = "เข้าหน้าร้อน — inverter อาจ throttle เพราะความร้อน",
                    Evidence = $"เดือน {thMonth} (ร้อนที่สุดในปี), อายุ inverter {ageYears:0.1} ปี\n" +
                               "Inverter อายุ 3+ ปี พัดลมเริ่มสะสมฝุ่น → ระบายความร้อนได้แย่ลง → " +
                               "thermal derating (ลด output 10-30% เมื่อร้อนเกิน)",
                    CheckSteps = new[]
                    {
                        "ตรวจอุณหภูมิ inverter ผ่าน cloud (>65°C = มีปัญหา)",
                        "ตรวจพัดลม inverter ว่ายังหมุนอยู่",
                        "ตรวจช่องระบายอากาศ ว่าไม่ถูกบัง",
                        "เปรียบเทียบ output power ช่วงเที่ยง vs rated capacity"
                    },
                    FixSteps = new[]
                    {
                        "ทำความสะอาดฝุ่นที่ช่องระบายอากาศ inverter",
                        "ติดตั้งหลังคาบังแดด (shade) ให้ inverter (ถ้ายังไม่มี)",
                        "เปลี่ยนพัดลม inverter ถ้าหมุนช้าหรือเสียงดัง",
                        "ติดตั้ง fan เสริมถ้าอยู่ในพื้นที่อากาศไม่ถ่ายเท"
                    },
                    RiskLevel = "Medium",
                    KnownCaseRef = "Thermal derating prevention (Thailand hot season Mar-Apr)",
                });
            }
        }

        return alerts;
    }

    // ══════════════════════════════════════════════════════════════
    //  Pattern 6: Repeating Fault Codes (same error recurring)
    // ══════════════════════════════════════════════════════════════

    private async Task<List<PredictiveAlert>> AnalyzeRepeatingFaultsAsync(Installation inst)
    {
        var alerts = new List<PredictiveAlert>();
        var monthAgo = DateTime.UtcNow.AddDays(-30);

        // Look for the same alert title recurring multiple times in 30 days
        var recurringAlerts = await _context.SystemAlerts.AsNoTracking()
            .Where(a => a.InstallationId == inst.Id && a.OccurredAt >= monthAgo)
            .GroupBy(a => a.Title)
            .Select(g => new { Title = g.Key, Count = g.Count(), Latest = g.Max(a => a.OccurredAt) })
            .Where(g => g.Count >= 3)
            .OrderByDescending(g => g.Count)
            .Take(3)
            .ToListAsync();

        foreach (var recurring in recurringAlerts)
        {
            var kb = InverterKnowledgeBase.GetRecurringFaultCase(inst.InverterBrand, recurring.Title);
            alerts.Add(new PredictiveAlert
            {
                InstallationId = inst.Id,
                SiteName = inst.SiteName,
                Prediction = $"ปัญหาซ้ำ {recurring.Count} ครั้งใน 30 วัน — มีปัญหาที่ต้นเหตุ",
                Evidence = $"Alert \"{recurring.Title}\" เกิดซ้ำ {recurring.Count} ครั้ง\n" +
                           $"ล่าสุด: {recurring.Latest:dd/MM/yyyy HH:mm}\n" +
                           "การเกิดซ้ำหลายครั้งบ่งชี้ว่ามีปัญหาที่ต้นเหตุยังไม่ได้แก้ " +
                           "ไม่ใช่แค่ error ชั่วคราว",
                CheckSteps = kb.CheckSteps,
                FixSteps = kb.FixSteps,
                RiskLevel = recurring.Count >= 5 ? "High" : "Medium",
                KnownCaseRef = kb.CaseRef,
            });
        }

        return alerts;
    }

    // ══════════════════════════════════════════════════════════════
    //  Pattern 7: Safety Recalls (brand-specific)
    // ══════════════════════════════════════════════════════════════

    private static List<PredictiveAlert> AnalyzeSafetyRecalls(Installation inst)
    {
        var alerts = new List<PredictiveAlert>();
        var recall = InverterKnowledgeBase.GetSafetyRecallCase(inst.InverterBrand, inst.InverterModel, inst.InstallationDate);
        if (recall != null)
        {
            alerts.Add(new PredictiveAlert
            {
                InstallationId = inst.Id,
                SiteName = inst.SiteName,
                Prediction = $"⚠ SAFETY RECALL สำหรับ {inst.InverterBrand} {inst.InverterModel ?? ""}",
                Evidence = recall.CaseRef,
                CheckSteps = recall.CheckSteps,
                FixSteps = recall.FixSteps,
                RiskLevel = "High",
                KnownCaseRef = recall.CaseRef,
            });
        }
        return alerts;
    }

    // ══════════════════════════════════════════════════════════════
    //  Pattern 8: Known Fault Code Matching (from error logs)
    // ══════════════════════════════════════════════════════════════

    private async Task<List<PredictiveAlert>> AnalyzeFaultCodePatternsAsync(Installation inst)
    {
        var alerts = new List<PredictiveAlert>();
        var weekAgo = DateTime.UtcNow.AddDays(-7);

        // Get recent error messages and try to match known fault codes
        var recentErrors = await _context.InverterApiLogs.AsNoTracking()
            .Where(l => l.InstallationId == inst.Id && l.RequestTime >= weekAgo
                && !l.IsSuccess && l.ErrorMessage != null)
            .Select(l => l.ErrorMessage!)
            .Distinct()
            .Take(20)
            .ToListAsync();

        // Also check recent alarm-type alerts
        var recentAlarms = await _context.SystemAlerts.AsNoTracking()
            .Where(a => a.InstallationId == inst.Id && a.OccurredAt >= weekAgo
                && (a.AlertType == AlertType.Error || a.AlertType == AlertType.Warning))
            .Select(a => a.Title + " " + a.Message)
            .Take(10)
            .ToListAsync();

        var allErrors = recentErrors.Concat(recentAlarms).Distinct();
        var matchedCodes = new HashSet<string>();

        foreach (var errorText in allErrors)
        {
            var match = InverterKnowledgeBase.MatchFaultCode(inst.InverterBrand, errorText);
            if (match == null || matchedCodes.Contains(match.CaseRef)) continue;
            matchedCodes.Add(match.CaseRef);

            alerts.Add(new PredictiveAlert
            {
                InstallationId = inst.Id,
                SiteName = inst.SiteName,
                Prediction = $"ตรวจพบ fault code ที่รู้จัก: {match.CaseRef.Split('—')[0].Trim()}",
                Evidence = $"พบใน error log 7 วันล่าสุด\nอ้างอิง: {match.CaseRef}",
                CheckSteps = match.CheckSteps,
                FixSteps = match.FixSteps,
                RiskLevel = match.CaseRef.Contains("FIRE") || match.CaseRef.Contains("safety") ? "High" : "Medium",
                KnownCaseRef = match.CaseRef,
            });
        }

        return alerts;
    }
}

public class PredictiveAlert
{
    public int InstallationId { get; set; }
    public string SiteName { get; set; } = "";
    public string Prediction { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string[] CheckSteps { get; set; } = Array.Empty<string>();
    public string[] FixSteps { get; set; } = Array.Empty<string>();
    public string RiskLevel { get; set; } = "Medium";
    public string? KnownCaseRef { get; set; }
}
