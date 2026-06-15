using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.LineApi;

namespace SolarApp.Pages.Admin;

/// <summary>
/// Admin CRUD for the granular LINE notification routing layer
/// (<see cref="NotificationRoute"/>).  Lets the operator say, e.g.,
/// "warranty-expiry events → group ID X AND personal user ID Y".
/// </summary>
[Authorize(Roles = "Admin")]
public class NotificationRoutesModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILineMessagingService _lineService;

    public NotificationRoutesModel(
        ApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        ILineMessagingService lineService)
    {
        _context = context;
        _userManager = userManager;
        _lineService = lineService;
    }

    public List<NotificationRoute> Routes { get; set; } = new();
    public Dictionary<int, string> InstallationNames { get; set; } = new();
    public List<SelectListItem> InstallationOptions { get; set; } = new();

    [BindProperty] public InputModel Input { get; set; } = new();

    public class InputModel
    {
        public NotificationEventType EventType { get; set; } = NotificationEventType.WarrantyExpiry;
        /// <summary>0 / null = global; otherwise the installation id.</summary>
        public int? InstallationId { get; set; }
        public string LineId { get; set; } = string.Empty;
        public string? Label { get; set; }
    }

    public async Task OnGetAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        Routes = await _context.NotificationRoutes
            .OrderBy(r => r.InstallationId == null ? 0 : 1)   // global first
            .ThenBy(r => r.EventType)
            .ThenBy(r => r.Id)
            .ToListAsync();

        var installs = await _context.Installations
            .OrderBy(i => i.SiteName)
            .Select(i => new { i.Id, i.SiteName, i.SiteCode })
            .ToListAsync();
        InstallationNames = installs.ToDictionary(i => i.Id, i => i.SiteName);
        InstallationOptions = installs
            .Select(i => new SelectListItem
            {
                Value = i.Id.ToString(),
                Text = $"{i.SiteName} ({i.SiteCode})"
            })
            .ToList();
    }

    public async Task<IActionResult> OnPostAddAsync()
    {
        if (string.IsNullOrWhiteSpace(Input.LineId))
        {
            TempData["Error"] = "ต้องกรอก LINE ID (group/user/room).";
            return RedirectToPage();
        }

        var user = await _userManager.GetUserAsync(User);
        // Normalize 0 → null so the dropdown's "Global" option (value 0) is
        // stored as a true global route.
        var installId = Input.InstallationId.HasValue && Input.InstallationId.Value > 0
            ? Input.InstallationId
            : null;

        _context.NotificationRoutes.Add(new NotificationRoute
        {
            EventType = Input.EventType,
            InstallationId = installId,
            LineId = Input.LineId.Trim(),
            Label = string.IsNullOrWhiteSpace(Input.Label) ? null : Input.Label.Trim(),
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            CreatedByUserId = user?.Id,
        });
        await _context.SaveChangesAsync();
        TempData["Success"] = "เพิ่ม route เรียบร้อย.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        var route = await _context.NotificationRoutes.FindAsync(id);
        if (route != null)
        {
            route.IsEnabled = !route.IsEnabled;
            await _context.SaveChangesAsync();
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var route = await _context.NotificationRoutes.FindAsync(id);
        if (route != null)
        {
            _context.NotificationRoutes.Remove(route);
            await _context.SaveChangesAsync();
            TempData["Success"] = "ลบ route แล้ว.";
        }
        return RedirectToPage();
    }

    /// <summary>Fire a test message to the route's LINE id so the operator can verify it before relying on it.</summary>
    public async Task<IActionResult> OnPostTestAsync(int id)
    {
        var route = await _context.NotificationRoutes.FindAsync(id);
        if (route == null) return RedirectToPage();
        try
        {
            await _lineService.SendMessageAsync(route.LineId,
                $"🔔 ทดสอบ NotificationRoute\nEvent: {route.EventType}\n" +
                $"Label: {route.Label ?? "-"}\nถ้าเห็นข้อความนี้ แปลว่า LINE ID ใช้งานได้.");
            TempData["Success"] = $"ส่งข้อความทดสอบไปยัง {route.LineId} แล้ว.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"ส่งทดสอบไม่สำเร็จ: {ex.Message}";
        }
        return RedirectToPage();
    }
}
