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
        var installations = await _context.Installations
            .AsNoTracking()
            .Where(i => i.Status != InstallationStatus.Inactive)
            .ToListAsync();

        if (installations.Count == 0) return alerts;

        // Pre-compute all aggregations in a fixed number of queries (was 5–11
        // queries × N installations). For 100 installations: 800+ queries → ~10.
        var installationIds = installations.Select(i => i.Id).ToList();
        var metrics = await BuildBatchMetricsAsync(installationIds);

        foreach (var inst in installations)
        {
            try
            {
                alerts.AddRange(AnalyzeEnergyDecline(inst, metrics));
                alerts.AddRange(AnalyzeIntermittentOffline(inst, metrics));
                alerts.AddRange(AnalyzeApiErrorTrend(inst, metrics));
                alerts.AddRange(AnalyzeAgeBasedRisks(inst));
                alerts.AddRange(AnalyzeEnvironmentalRisks(inst));
                alerts.AddRange(AnalyzeRepeatingFaults(inst, metrics));
                alerts.AddRange(AnalyzeSafetyRecalls(inst));
                alerts.AddRange(AnalyzeFaultCodePatterns(inst, metrics));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Predictive analysis failed for {Id}", inst.Id);
            }
        }

        return alerts;
    }

    // ══════════════════════════════════════════════════════════════
    //  Batch metrics loader — single set of queries for all installations
    // ══════════════════════════════════════════════════════════════

    private async Task<DiagnosticsBatchMetrics> BuildBatchMetricsAsync(List<int> installationIds)
    {
        var now = DateTime.UtcNow;
        var weekAgo = now.AddDays(-7);
        var monthAgo = now.AddDays(-30);
        var recentStart = now.AddDays(-7);
        var previousStart = now.AddDays(-37);
        var previousEnd = now.AddDays(-30);
        var recentWindow = now.AddDays(-3);
        var previousWindow = now.AddDays(-10);

        // 1) Energy decline windows (Pattern 1) — 2 queries
        var recentEnergyByInst = await _context.EnergyData.AsNoTracking()
            .Where(e => installationIds.Contains(e.InstallationId)
                && e.Timestamp >= recentStart && e.EnergyGenerationKwh > 0)
            .GroupBy(e => e.InstallationId)
            .Select(g => new { InstallationId = g.Key, Avg = g.Average(e => (decimal?)e.EnergyGenerationKwh) ?? 0m })
            .ToDictionaryAsync(x => x.InstallationId, x => x.Avg);

        var previousEnergyByInst = await _context.EnergyData.AsNoTracking()
            .Where(e => installationIds.Contains(e.InstallationId)
                && e.Timestamp >= previousStart && e.Timestamp <= previousEnd
                && e.EnergyGenerationKwh > 0)
            .GroupBy(e => e.InstallationId)
            .Select(g => new { InstallationId = g.Key, Avg = g.Average(e => (decimal?)e.EnergyGenerationKwh) ?? 0m })
            .ToDictionaryAsync(x => x.InstallationId, x => x.Avg);

        // 2) Intermittent offline (Pattern 2) — 2 queries
        var offlineCountByInst = await _context.InverterApiLogs.AsNoTracking()
            .Where(l => installationIds.Contains(l.InstallationId) && l.RequestTime >= weekAgo
                && l.IsSuccess && l.ErrorMessage != null && l.ErrorMessage.Contains("[DEVICE OFFLINE]"))
            .GroupBy(l => l.InstallationId)
            .Select(g => new { InstallationId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.InstallationId, x => x.Count);

        var successCountByInst = await _context.InverterApiLogs.AsNoTracking()
            .Where(l => installationIds.Contains(l.InstallationId) && l.RequestTime >= weekAgo && l.IsSuccess)
            .GroupBy(l => l.InstallationId)
            .Select(g => new { InstallationId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.InstallationId, x => x.Count);

        // 3) API error trend (Pattern 3) — 4 count queries + 1 top-errors query
        var recentErrorByInst = await _context.InverterApiLogs.AsNoTracking()
            .Where(l => installationIds.Contains(l.InstallationId)
                && l.RequestTime >= recentWindow && !l.IsSuccess)
            .GroupBy(l => l.InstallationId)
            .Select(g => new { InstallationId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.InstallationId, x => x.Count);

        var recentTotalByInst = await _context.InverterApiLogs.AsNoTracking()
            .Where(l => installationIds.Contains(l.InstallationId) && l.RequestTime >= recentWindow)
            .GroupBy(l => l.InstallationId)
            .Select(g => new { InstallationId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.InstallationId, x => x.Count);

        var prevErrorByInst = await _context.InverterApiLogs.AsNoTracking()
            .Where(l => installationIds.Contains(l.InstallationId)
                && l.RequestTime >= previousWindow && l.RequestTime < recentWindow && !l.IsSuccess)
            .GroupBy(l => l.InstallationId)
            .Select(g => new { InstallationId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.InstallationId, x => x.Count);

        var prevTotalByInst = await _context.InverterApiLogs.AsNoTracking()
            .Where(l => installationIds.Contains(l.InstallationId)
                && l.RequestTime >= previousWindow && l.RequestTime < recentWindow)
            .GroupBy(l => l.InstallationId)
            .Select(g => new { InstallationId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.InstallationId, x => x.Count);

        // Top errors per installation (last 3d) — load grouped error message + counts;
        // engine takes top 3 per installation in-memory.
        var topErrorsRaw = await _context.InverterApiLogs.AsNoTracking()
            .Where(l => installationIds.Contains(l.InstallationId)
                && l.RequestTime >= recentWindow && !l.IsSuccess && l.ErrorMessage != null)
            .GroupBy(l => new
            {
                l.InstallationId,
                Snippet = l.ErrorMessage!.Substring(0, Math.Min(l.ErrorMessage!.Length, 50))
            })
            .Select(g => new { g.Key.InstallationId, g.Key.Snippet, Count = g.Count() })
            .ToListAsync();

        var topErrorsByInst = topErrorsRaw
            .GroupBy(x => x.InstallationId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.Count).Take(3).Select(x => (x.Snippet, x.Count)).ToList());

        // 4) Repeating fault titles (Pattern 6) — 1 query
        var recurringRaw = await _context.SystemAlerts.AsNoTracking()
            .Where(a => installationIds.Contains(a.InstallationId) && a.OccurredAt >= monthAgo)
            .GroupBy(a => new { a.InstallationId, a.Title })
            .Select(g => new
            {
                g.Key.InstallationId,
                g.Key.Title,
                Count = g.Count(),
                Latest = g.Max(a => a.OccurredAt)
            })
            .Where(x => x.Count >= 3)
            .ToListAsync();

        var recurringByInst = recurringRaw
            .GroupBy(x => x.InstallationId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.Count).Take(3)
                      .Select(x => (x.Title, x.Count, x.Latest)).ToList());

        // 5) Recent distinct error messages (Pattern 8) — 1 query
        var distinctErrorsRaw = await _context.InverterApiLogs.AsNoTracking()
            .Where(l => installationIds.Contains(l.InstallationId) && l.RequestTime >= weekAgo
                && !l.IsSuccess && l.ErrorMessage != null)
            .Select(l => new { l.InstallationId, l.ErrorMessage })
            .Distinct()
            .ToListAsync();

        var distinctErrorsByInst = distinctErrorsRaw
            .GroupBy(x => x.InstallationId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ErrorMessage!).Take(20).ToList());

        // 6) Recent alarm strings (Pattern 8) — 1 query
        var alarmStringsRaw = await _context.SystemAlerts.AsNoTracking()
            .Where(a => installationIds.Contains(a.InstallationId) && a.OccurredAt >= weekAgo
                && (a.AlertType == AlertType.Error || a.AlertType == AlertType.Warning))
            .Select(a => new { a.InstallationId, Combined = a.Title + " " + a.Message })
            .ToListAsync();

        var alarmStringsByInst = alarmStringsRaw
            .GroupBy(x => x.InstallationId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Combined).Take(10).ToList());

        return new DiagnosticsBatchMetrics
        {
            RecentEnergyAvg = recentEnergyByInst,
            PreviousEnergyAvg = previousEnergyByInst,
            OfflineMarkedCount = offlineCountByInst,
            SuccessfulSyncCount = successCountByInst,
            RecentErrorCount = recentErrorByInst,
            RecentTotalCount = recentTotalByInst,
            PreviousErrorCount = prevErrorByInst,
            PreviousTotalCount = prevTotalByInst,
            TopRecentErrors = topErrorsByInst,
            RecurringAlerts = recurringByInst,
            RecentDistinctErrors = distinctErrorsByInst,
            RecentAlarmStrings = alarmStringsByInst,
        };
    }

    // ══════════════════════════════════════════════════════════════
    //  Pattern 1: Energy Production Decline
    // ══════════════════════════════════════════════════════════════

    private static List<PredictiveAlert> AnalyzeEnergyDecline(Installation inst, DiagnosticsBatchMetrics m)
    {
        var alerts = new List<PredictiveAlert>();
        var recentEnergy = m.RecentEnergyAvg.TryGetValue(inst.Id, out var r) ? r : 0m;
        var previousEnergy = m.PreviousEnergyAvg.TryGetValue(inst.Id, out var p) ? p : 0m;

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

    private static List<PredictiveAlert> AnalyzeIntermittentOffline(Installation inst, DiagnosticsBatchMetrics m)
    {
        var alerts = new List<PredictiveAlert>();
        var offlineCount = m.OfflineMarkedCount.TryGetValue(inst.Id, out var o) ? o : 0;
        var totalCount = m.SuccessfulSyncCount.TryGetValue(inst.Id, out var t) ? t : 0;

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

    private static List<PredictiveAlert> AnalyzeApiErrorTrend(Installation inst, DiagnosticsBatchMetrics m)
    {
        var alerts = new List<PredictiveAlert>();
        var recentErrors = m.RecentErrorCount.TryGetValue(inst.Id, out var re) ? re : 0;
        var recentTotal = m.RecentTotalCount.TryGetValue(inst.Id, out var rt) ? rt : 0;
        var prevErrors = m.PreviousErrorCount.TryGetValue(inst.Id, out var pe) ? pe : 0;
        var prevTotal = m.PreviousTotalCount.TryGetValue(inst.Id, out var pt) ? pt : 0;

        if (recentTotal < 5 || prevTotal < 5) return alerts;

        var recentRate = (double)recentErrors / recentTotal * 100;
        var prevRate = (double)prevErrors / prevTotal * 100;

        if (recentRate >= 20 && recentRate >= prevRate * 1.8)
        {
            var topErrors = m.TopRecentErrors.TryGetValue(inst.Id, out var te) ? te : new();
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
        var ageYears = (DateTime.UtcNow - inst.HardwareInServiceDate).TotalDays / 365.25;

        // Inverter fan typically fails at 5-7 years
        if (ageYears is >= 4.5 and < 6)
        {
            var kb = InverterKnowledgeBase.GetAgingCase(inst.InverterBrand, "fan", ageYears);
            alerts.Add(new PredictiveAlert
            {
                InstallationId = inst.Id,
                SiteName = inst.SiteName,
                Prediction = $"Inverter อายุ {ageYears:0.0} ปี — ใกล้ถึงจุดเสี่ยงพัดลมเสีย (5-7 ปี)",
                Evidence = $"เริ่มใช้งานวันที่ {inst.HardwareInServiceDate:dd/MM/yyyy} ({ageYears:0.1} ปี)\n" +
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
                Evidence = $"เริ่มใช้งานวันที่ {inst.HardwareInServiceDate:dd/MM/yyyy} ({ageYears:0.1} ปี)\n" +
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
            // Whole site-local days, like every other warranty countdown.
            // Subtracting UtcNow gave a fractional day that rounded the
            // displayed "เหลืออีก N วัน" down by one for most of the day.
            var daysLeft = (inst.InverterWarrantyExpiryDate.Value.Date
                            - SolarApp.Services.SiteTime.Today).TotalDays;
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
        var daysSinceInstall = (DateTime.UtcNow - inst.HardwareInServiceDate).TotalDays;
        // Only for sites we clean — this one ends in "ทำความสะอาดแผง", and
        // recommending a crew visit we're not contracted for is noise at best.
        if (thMonth is >= 5 and <= 6 && daysSinceInstall >= 90 && inst.HasCleaningInScope)
        {
            // Use install date as fallback if never cleaned (not a 999 sentinel)
            var referenceDate = inst.LastCleaningDate ?? inst.HardwareInServiceDate;
            var daysSinceLastCleaning = (DateTime.UtcNow - referenceDate).TotalDays;
            var refLabel = inst.LastCleaningDate.HasValue ? "ล้างแผงครั้งสุดท้าย" : "ตั้งแต่เริ่มใช้งาน (ยังไม่เคยล้าง)";

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
            var ageYears = (DateTime.UtcNow - inst.HardwareInServiceDate).TotalDays / 365.25;
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

    private static List<PredictiveAlert> AnalyzeRepeatingFaults(Installation inst, DiagnosticsBatchMetrics m)
    {
        var alerts = new List<PredictiveAlert>();
        if (!m.RecurringAlerts.TryGetValue(inst.Id, out var recurringAlerts))
            return alerts;

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
        var recall = InverterKnowledgeBase.GetSafetyRecallCase(inst.InverterBrand, inst.InverterModel, inst.HardwareInServiceDate);
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

    private static List<PredictiveAlert> AnalyzeFaultCodePatterns(Installation inst, DiagnosticsBatchMetrics m)
    {
        var alerts = new List<PredictiveAlert>();
        var recentErrors = m.RecentDistinctErrors.TryGetValue(inst.Id, out var re) ? re : new List<string>();
        var recentAlarms = m.RecentAlarmStrings.TryGetValue(inst.Id, out var ra) ? ra : new List<string>();

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
