using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Services.PredictiveDiagnostics;

/// <summary>
/// Enriches any SystemAlert with brand-specific diagnostics:
///   - Predicted cause (จากข้อมูล log + knowledge base)
///   - Check steps (สิ่งที่ต้องตรวจ)
///   - Fix steps (วิธีแก้ไข)
///   - Known case reference
///
/// Called when creating alerts AND when sending LINE notifications.
/// </summary>
public class AlertDiagnosticsEnricher
{
    private readonly ApplicationDbContext _context;

    public AlertDiagnosticsEnricher(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<AlertDiagnostics> EnrichAsync(SystemAlert alert, Installation installation)
    {
        var diag = new AlertDiagnostics();
        var brand = installation.InverterBrand;

        // ── 1. Match error logs to known fault codes ──
        var weekAgo = DateTime.UtcNow.AddDays(-7);
        var recentErrors = await _context.InverterApiLogs
            .Where(l => l.InstallationId == installation.Id
                && l.RequestTime >= weekAgo
                && !l.IsSuccess
                && l.ErrorMessage != null)
            .OrderByDescending(l => l.RequestTime)
            .Take(10)
            .Select(l => l.ErrorMessage!)
            .ToListAsync();

        // Try matching known fault codes from error logs
        foreach (var err in recentErrors)
        {
            var faultMatch = InverterKnowledgeBase.MatchFaultCode(brand, err);
            if (faultMatch != null)
            {
                diag.PredictedCause = $"ตรวจพบ: {faultMatch.CaseRef.Split('—')[0].Trim()}";
                diag.CheckSteps.AddRange(faultMatch.CheckSteps);
                diag.FixSteps.AddRange(faultMatch.FixSteps);
                diag.KnownCaseRef = faultMatch.CaseRef;
                break;
            }
        }

        // ── 2. Match from alert title/message ──
        if (diag.PredictedCause == null)
        {
            var titleAndMsg = $"{alert.Title} {alert.Message} {alert.RootCauseAnalysis}";
            var alertMatch = InverterKnowledgeBase.MatchFaultCode(brand, titleAndMsg);
            if (alertMatch != null)
            {
                diag.PredictedCause = $"ตรวจพบ: {alertMatch.CaseRef.Split('—')[0].Trim()}";
                diag.CheckSteps.AddRange(alertMatch.CheckSteps);
                diag.FixSteps.AddRange(alertMatch.FixSteps);
                diag.KnownCaseRef = alertMatch.CaseRef;
            }
        }

        // ── 3. Analyze by alert type ──
        switch (alert.AlertType)
        {
            case AlertType.Offline:
                await EnrichOfflineAlertAsync(diag, alert, installation, recentErrors);
                break;
            case AlertType.Error:
                EnrichErrorAlert(diag, alert, installation);
                break;
            case AlertType.LowPerformance:
                await EnrichPerformanceAlertAsync(diag, installation);
                break;
        }

        // ── 4. Check for safety recalls ──
        var recall = InverterKnowledgeBase.GetSafetyRecallCase(brand, installation.InverterModel, installation.InstallationDate);
        if (recall != null)
        {
            diag.SafetyWarning = recall.CaseRef;
        }

        // ── 5. Add age-based context ──
        var ageYears = (DateTime.UtcNow - installation.InstallationDate).TotalDays / 365.25;
        if (ageYears >= 5)
        {
            diag.AddContextNote($"อายุ inverter: {ageYears:0.1} ปี — อาจมีชิ้นส่วนเสื่อม (พัดลม, capacitor)");
        }

        // ── 6. Check recent alert history (recurring?) ──
        var monthAgo = DateTime.UtcNow.AddDays(-30);
        var similarCount = await _context.SystemAlerts
            .CountAsync(a => a.InstallationId == installation.Id
                && a.Title == alert.Title
                && a.OccurredAt >= monthAgo);
        if (similarCount >= 3)
        {
            diag.AddContextNote($"⚠ ปัญหาเดิมเกิดซ้ำ {similarCount} ครั้งใน 30 วัน — ต้นเหตุยังไม่ได้แก้");
            var recurringKb = InverterKnowledgeBase.GetRecurringFaultCase(brand, alert.Title);
            diag.FixSteps.AddRange(recurringKb.FixSteps.Where(f => !diag.FixSteps.Contains(f)));
        }

        // De-dup steps
        diag.CheckSteps = diag.CheckSteps.Distinct().ToList();
        diag.FixSteps = diag.FixSteps.Distinct().ToList();

        return diag;
    }

    private async Task EnrichOfflineAlertAsync(AlertDiagnostics diag, SystemAlert alert,
        Installation inst, List<string> recentErrors)
    {
        var isApiIssue = alert.Title.Contains("API Connection") || alert.RootCauseAnalysis?.Contains("API") == true;

        if (isApiIssue)
        {
            // API connection issue — look at error patterns
            diag.PredictedCause ??= "API/Cloud เชื่อมต่อไม่ได้ — inverter อาจยังทำงานปกติ";

            var kb = InverterKnowledgeBase.GetApiErrorCase(inst.InverterBrand,
                string.Join(" ", recentErrors.Take(3)));
            diag.CheckSteps.AddRange(kb.CheckSteps);
            diag.FixSteps.AddRange(kb.FixSteps);
            diag.KnownCaseRef ??= kb.CaseRef;
        }
        else
        {
            // Device offline — WiFi/hardware issue
            diag.PredictedCause ??= "Inverter/Data logger ไม่ส่งข้อมูล — อาจเป็นปัญหา WiFi หรือ hardware";

            var kb = InverterKnowledgeBase.GetIntermittentOfflineCase(inst.InverterBrand);
            diag.CheckSteps.AddRange(kb.CheckSteps);
            diag.FixSteps.AddRange(kb.FixSteps);
            diag.KnownCaseRef ??= kb.CaseRef;
        }

        // Check if intermittent pattern
        var weekAgo = DateTime.UtcNow.AddDays(-7);
        var offlineCount = await _context.InverterApiLogs
            .CountAsync(l => l.InstallationId == inst.Id && l.RequestTime >= weekAgo
                && l.IsSuccess && l.ErrorMessage != null && l.ErrorMessage.Contains("[DEVICE OFFLINE]"));
        var totalCount = await _context.InverterApiLogs
            .CountAsync(l => l.InstallationId == inst.Id && l.RequestTime >= weekAgo && l.IsSuccess);

        if (totalCount > 5 && offlineCount > 0)
        {
            var offlinePct = (double)offlineCount / totalCount * 100;
            if (offlinePct >= 20)
                diag.AddContextNote($"7 วันที่ผ่านมา: offline {offlinePct:0}% ของเวลา ({offlineCount}/{totalCount} readings) — สัญญาณไม่เสถียร");
        }
    }

    private static void EnrichErrorAlert(AlertDiagnostics diag, SystemAlert alert, Installation inst)
    {
        diag.PredictedCause ??= $"Inverter แจ้ง fault/error — ตรวจ error code เพื่อระบุสาเหตุ";

        if (diag.CheckSteps.Count == 0)
        {
            diag.CheckSteps.Add($"ดู error code ใน {inst.InverterBrand} cloud portal");
            diag.CheckSteps.Add("ตรวจ inverter display ว่าแสดง error code อะไร");
            diag.CheckSteps.Add("ตรวจ LED status ที่ตัว inverter (เขียว/ส้ม/แดง)");
            diag.CheckSteps.Add("ถ่ายรูป error code + LED status ส่งให้ช่าง");
        }

        if (diag.FixSteps.Count == 0)
        {
            diag.FixSteps.Add("ลอง restart: ปิด AC breaker → รอ 30 วิ → เปิด");
            diag.FixSteps.Add("ถ้า error ยังอยู่หลัง restart → สร้าง work order ส่งช่างไปตรวจ");
            diag.FixSteps.Add($"แจ้ง {inst.InverterBrand} service center พร้อม S/N + error code");
        }
    }

    private async Task EnrichPerformanceAlertAsync(AlertDiagnostics diag, Installation inst)
    {
        var now = DateTime.UtcNow;
        var recentAvg = await _context.EnergyData
            .Where(e => e.InstallationId == inst.Id && e.Timestamp >= now.AddDays(-7) && e.EnergyGenerationKwh > 0)
            .AverageAsync(e => (decimal?)e.EnergyGenerationKwh) ?? 0;
        var prevAvg = await _context.EnergyData
            .Where(e => e.InstallationId == inst.Id && e.Timestamp >= now.AddDays(-37) && e.Timestamp <= now.AddDays(-30) && e.EnergyGenerationKwh > 0)
            .AverageAsync(e => (decimal?)e.EnergyGenerationKwh) ?? 0;

        if (prevAvg > 0 && recentAvg > 0)
        {
            var decline = (1 - recentAvg / prevAvg) * 100;
            if (decline > 0)
                diag.AddContextNote($"ผลิตไฟลดลง {decline:0}% (7 วัน: {recentAvg:0.0} kWh vs 30 วันก่อน: {prevAvg:0.0} kWh)");
        }

        var kb = InverterKnowledgeBase.GetEnergyDeclineCase(inst.InverterBrand, 20);
        diag.PredictedCause ??= "ผลิตไฟต่ำกว่าปกติ — อาจมีปัญหาที่แผง/inverter/shading";
        diag.CheckSteps.AddRange(kb.CheckSteps);
        diag.FixSteps.AddRange(kb.FixSteps);
        diag.KnownCaseRef ??= kb.CaseRef;
    }
}

public class AlertDiagnostics
{
    public string? PredictedCause { get; set; }
    public List<string> CheckSteps { get; set; } = new();
    public List<string> FixSteps { get; set; } = new();
    public string? KnownCaseRef { get; set; }
    public string? SafetyWarning { get; set; }
    public List<string> ContextNotes { get; set; } = new();

    public void AddContextNote(string note)
    {
        if (!ContextNotes.Contains(note))
            ContextNotes.Add(note);
    }

    public bool HasDiagnostics => PredictedCause != null || CheckSteps.Count > 0 || FixSteps.Count > 0;

    public string ToRootCauseAppendix()
    {
        var parts = new List<string>();

        if (PredictedCause != null)
            parts.Add($"🔍 คาดว่า: {PredictedCause}");

        if (ContextNotes.Count > 0)
            parts.Add(string.Join("\n", ContextNotes));

        if (CheckSteps.Count > 0)
            parts.Add("สิ่งที่ต้องตรวจ:\n" + string.Join("\n", CheckSteps.Take(6).Select(s => $"  • {s}")));

        if (FixSteps.Count > 0)
            parts.Add("วิธีแก้ไข:\n" + string.Join("\n", FixSteps.Take(6).Select(s => $"  • {s}")));

        if (KnownCaseRef != null)
            parts.Add($"อ้างอิง: {KnownCaseRef}");

        if (SafetyWarning != null)
            parts.Add($"⚠ SAFETY: {SafetyWarning}");

        return string.Join("\n\n", parts);
    }
}
