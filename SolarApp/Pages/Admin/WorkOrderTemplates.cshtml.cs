using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.WorkOrderTemplates;

namespace SolarApp.Pages.Admin;

[Authorize(Roles = "Admin")]
public class WorkOrderTemplatesModel : PageModel
{
    private readonly IWorkOrderTemplateService _templates;
    private readonly UserManager<ApplicationUser> _userManager;

    public WorkOrderTemplatesModel(IWorkOrderTemplateService templates, UserManager<ApplicationUser> userManager)
    {
        _templates = templates;
        _userManager = userManager;
    }

    public Dictionary<WorkOrderType, WorkOrderTypeTemplate> Templates { get; set; } = new();

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public static string ThaiLabel(WorkOrderType t) => t switch
    {
        WorkOrderType.PanelCleaning => "ล้างแผง",
        WorkOrderType.PreventiveMaintenance => "บำรุงรักษาเชิงป้องกัน (PM)",
        WorkOrderType.CorrectiveRepair => "ซ่อมแก้ไข",
        WorkOrderType.Inspection => "ตรวจสอบ",
        WorkOrderType.NewInstallation => "ติดตั้งใหม่",
        WorkOrderType.Commissioning => "Commissioning",
        WorkOrderType.Decommission => "ถอนการติดตั้ง",
        WorkOrderType.Other => "อื่นๆ",
        _ => t.ToString()
    };

    public async Task OnGetAsync()
    {
        Templates = await _templates.GetAllAsync();
    }

    public async Task<IActionResult> OnPostSaveAsync(WorkOrderType type, string? title, string? checklist)
    {
        if (string.IsNullOrWhiteSpace(title) && type != WorkOrderType.Other)
        {
            ErrorMessage = $"กรุณากรอกหัวข้องานสำหรับประเภท: {ThaiLabel(type)}";
            return RedirectToPage();
        }

        var user = await _userManager.GetUserAsync(User);
        await _templates.SaveAsync(type, title ?? "", checklist ?? "", user?.Id);
        StatusMessage = $"บันทึกค่ามาตรฐานสำหรับ \"{ThaiLabel(type)}\" เรียบร้อย";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResetAsync(WorkOrderType type)
    {
        var user = await _userManager.GetUserAsync(User);
        await _templates.ResetAsync(type, user?.Id);
        StatusMessage = $"รีเซ็ตค่ามาตรฐานสำหรับ \"{ThaiLabel(type)}\" กลับเป็นค่าเริ่มต้นแล้ว";
        return RedirectToPage();
    }
}
