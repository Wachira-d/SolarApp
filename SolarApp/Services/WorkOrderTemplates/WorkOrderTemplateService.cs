using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Services.WorkOrderTemplates;

public interface IWorkOrderTemplateService
{
    /// <summary>Returns one template per WorkOrderType. Missing rows are filled with built-in defaults.</summary>
    Task<Dictionary<WorkOrderType, WorkOrderTypeTemplate>> GetAllAsync();

    /// <summary>Persists a template (insert or update).</summary>
    Task SaveAsync(WorkOrderType type, string title, string checklist, string? userId);

    /// <summary>Restores a single type back to the built-in default.</summary>
    Task ResetAsync(WorkOrderType type, string? userId);

    /// <summary>The built-in shipped default for a given type. Useful for "reset" UX and seeding.</summary>
    (string Title, string Checklist) GetBuiltInDefault(WorkOrderType type);
}

public class WorkOrderTemplateService : IWorkOrderTemplateService
{
    private readonly ApplicationDbContext _context;

    public WorkOrderTemplateService(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Dictionary<WorkOrderType, WorkOrderTypeTemplate>> GetAllAsync()
    {
        var rows = await _context.WorkOrderTypeTemplates.AsNoTracking().ToListAsync();
        var byType = rows.ToDictionary(r => r.Type);

        // Backfill missing types with built-in defaults (NOT persisted — only returned)
        foreach (WorkOrderType t in Enum.GetValues(typeof(WorkOrderType)))
        {
            if (byType.ContainsKey(t)) continue;
            var (title, checklist) = GetBuiltInDefault(t);
            byType[t] = new WorkOrderTypeTemplate
            {
                Type = t,
                Title = title,
                ChecklistItems = checklist,
            };
        }
        return byType;
    }

    public async Task SaveAsync(WorkOrderType type, string title, string checklist, string? userId)
    {
        var existing = await _context.WorkOrderTypeTemplates.FirstOrDefaultAsync(t => t.Type == type);
        if (existing == null)
        {
            _context.WorkOrderTypeTemplates.Add(new WorkOrderTypeTemplate
            {
                Type = type,
                Title = title.Trim(),
                ChecklistItems = checklist?.Trim() ?? "",
                UpdatedAt = DateTime.UtcNow,
                UpdatedByUserId = userId,
            });
        }
        else
        {
            existing.Title = title.Trim();
            existing.ChecklistItems = checklist?.Trim() ?? "";
            existing.UpdatedAt = DateTime.UtcNow;
            existing.UpdatedByUserId = userId;
        }
        await _context.SaveChangesAsync();
    }

    public async Task ResetAsync(WorkOrderType type, string? userId)
    {
        var (title, checklist) = GetBuiltInDefault(type);
        await SaveAsync(type, title, checklist, userId);
    }

    public (string Title, string Checklist) GetBuiltInDefault(WorkOrderType type) => type switch
    {
        WorkOrderType.PanelCleaning => (
            "ล้างแผงโซลาร์",
            string.Join("\n", new[]
            {
                "ตรวจ DC isolator ก่อนเริ่มงาน",
                "ปิด AC breaker ก่อนล้างแผง",
                "ล้างแผงทุกแถวด้วยน้ำสะอาด (ไม่ใช้สารทำความสะอาด)",
                "ตรวจรอยรั่ว / น้ำซึมหลังล้าง",
                "ตรวจความสะอาด glass หลังแห้ง",
                "เปิดระบบกลับ + ตรวจค่ากำลังผลิต",
                "ถ่ายรูปก่อน-หลัง ทุกแถวที่ล้าง",
                "จดเลขมิเตอร์ก่อน-หลัง",
            })),

        WorkOrderType.PreventiveMaintenance => (
            "บำรุงรักษาเชิงป้องกัน (PM)",
            string.Join("\n", new[]
            {
                "ตรวจสายไฟและ MC4 connectors ทุกจุด",
                "ขันแน่นน็อต MC4 + AC terminal",
                "วัดความต้านทานฉนวน DC string",
                "ตรวจสภาพ inverter (จอ / เสียง / อุณหภูมิ)",
                "ตรวจระบบดิน + bonding",
                "ทำความสะอาด heatsink / พัดลม inverter",
                "ตรวจ AC + DC breakers และ RCBO",
                "ตรวจ data logger + WiFi / 4G",
                "ตรวจโครงสร้างราง + clamps + รอยสนิม",
                "ถ่ายรูปจุดสำคัญ + สรุปรายงาน PM",
            })),

        WorkOrderType.CorrectiveRepair => (
            "งานซ่อมแก้ไข",
            string.Join("\n", new[]
            {
                "บันทึกอาการที่พบเมื่อมาถึงไซต์",
                "ตรวจหาสาเหตุ (root cause)",
                "ถ่ายรูปจุดเสีย/ก่อนซ่อม",
                "ทำการซ่อม / เปลี่ยนอุปกรณ์",
                "ทดสอบการทำงานหลังซ่อม",
                "ถ่ายรูปจุดซ่อมหลังเสร็จ",
                "บันทึกอะไหล่ที่ใช้ + serial number",
                "อธิบาย/แจ้งลูกค้าเกี่ยวกับการซ่อม",
            })),

        WorkOrderType.Inspection => (
            "ตรวจสอบประจำงวด",
            string.Join("\n", new[]
            {
                "ตรวจหน้าจอ inverter (ผลิตปกติ?)",
                "วัดแรงดัน / กระแส DC ในแต่ละ string",
                "วัดแรงดัน / กระแส AC output",
                "ตรวจ thermal scan หา hot spot (ถ้ามีกล้อง)",
                "ตรวจสภาพสายและ MC4 connectors",
                "ตรวจกล่องไฟ / AC breakers",
                "ถ่ายรูปจุดสำคัญ",
                "เปรียบเทียบกำลังผลิตกับ baseline เดิม",
            })),

        WorkOrderType.NewInstallation => (
            "ติดตั้งโซลาร์ใหม่",
            string.Join("\n", new[]
            {
                "สำรวจหลังคา + วัดมุม + เงา shading",
                "ติดตั้งโครงรางและ clamps",
                "วางแผง + ขัน clamps + เช็คความตึง",
                "ติดตั้ง inverter (ระยะระบายอากาศ)",
                "เดินสาย DC ผ่าน conduit + DC isolator",
                "ติดตั้ง AC breaker + RCBO + ป้ายเตือน",
                "ติดตั้ง data logger / WiFi stick",
                "ทดสอบ open-circuit voltage ทุก string",
                "ทดสอบฉนวน + ลงทะเบียน cloud",
                "ถ่ายรูป before / during / after ทุก stage",
            })),

        WorkOrderType.Commissioning => (
            "Commissioning + ส่งมอบงาน",
            string.Join("\n", new[]
            {
                "ตรวจ open-circuit voltage (Voc) ทุก string",
                "ตรวจความต้านทานฉนวน (insulation)",
                "เปิดระบบ + รัน inverter ครั้งแรก",
                "เช็คกำลังผลิตจริงเทียบสเปก",
                "ตั้งค่า + ลงทะเบียน cloud monitoring",
                "สอนการใช้งานแอป FusionSolar/SmartPV ให้ลูกค้า",
                "ส่งมอบ datasheet + คู่มือ + ใบรับประกัน",
                "ลูกค้าเซ็นรับงาน (CustomerSigned)",
            })),

        WorkOrderType.Decommission => (
            "ถอนการติดตั้ง",
            string.Join("\n", new[]
            {
                "ปิด AC + DC ทุกตัวก่อนเริ่มถอน",
                "ถอด data logger + แจ้ง cloud ตัดบัญชี",
                "ถอด + ติด label แผงเพื่อเก็บ",
                "ม้วน + เก็บสาย DC / AC",
                "รื้อโครงราง",
                "อุดรู screws บนหลังคา + กันรั่ว",
                "ทำความสะอาดพื้นที่",
                "ถ่ายรูปสภาพหลังถอน + ส่งให้ลูกค้า",
            })),

        WorkOrderType.Other => ("", ""),

        _ => ("", "")
    };
}
