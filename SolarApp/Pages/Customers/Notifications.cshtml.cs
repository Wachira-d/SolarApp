using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Models.Notifications;
using SolarApp.Services.Notifications;

namespace SolarApp.Pages.Customers;

/// <summary>
/// One customer's notification center: who receives alerts (LINE ids bound
/// by link, e-mail addresses), which events go to which channel, and quiet
/// hours. Previously this was three places — the customer's LINE id on the
/// user screen, NotifyOn* switches and quiet hours on every site's edit page.
/// </summary>
[Authorize(Roles = "Admin,Partner")]
public class NotificationsModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILineBindingService _binding;
    private readonly ICustomerNotificationService _notifications;
    private readonly ICustomerEmailSender _email;
    private readonly IConfiguration _configuration;

    public NotificationsModel(ApplicationDbContext context, UserManager<ApplicationUser> userManager,
        ILineBindingService binding, ICustomerNotificationService notifications,
        ICustomerEmailSender email, IConfiguration configuration)
    {
        _context = context;
        _userManager = userManager;
        _binding = binding;
        _notifications = notifications;
        _email = email;
        _configuration = configuration;
    }

    public Customer Customer { get; private set; } = null!;
    public List<Installation> Sites { get; private set; } = new();
    public List<CustomerNotificationRecipient> Recipients { get; private set; } = new();
    public List<LineBindingCode> PendingCodes { get; private set; } = new();
    public List<MatrixRow> Matrix { get; private set; } = new();
    public bool HasPreferenceRows { get; private set; }
    public string BaseUrl { get; private set; } = "";
    public string? BasicId { get; private set; }
    public bool EmailConfigured { get; private set; }
    public bool IsAdmin { get; private set; }
    public string? QuietStart { get; private set; }
    public string? QuietEnd { get; private set; }
    /// <summary>Sites disagree on quiet hours — saving will make them the same.</summary>
    public bool QuietHoursMixed { get; private set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }
    /// <summary>The code just created, so its card opens expanded with the QR.</summary>
    [TempData] public string? NewCode { get; set; }

    public record MatrixRow(NotificationEventType Event, string Label, bool Line, bool Email, bool IsDefault);

    public class PrefInput
    {
        public NotificationEventType EventType { get; set; }
        public bool Line { get; set; }
        public bool Email { get; set; }
    }

    public string LinkFor(string code) => $"{BaseUrl}/l/{code}";
    public string SiteNameFor(int? siteId) =>
        siteId == null ? "ทุกไซต์" : Sites.FirstOrDefault(s => s.Id == siteId)?.SiteName ?? $"ไซต์ #{siteId}";

    // ── Access ─────────────────────────────────────────────────────────

    /// <summary>
    /// Same rule as the customer detail page: admins see everything, a
    /// partner only customers with a site they service — and only those sites.
    /// Null on success; otherwise the result to return.
    /// </summary>
    private async Task<IActionResult?> AuthorizeAsync(int id, bool track = false)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Forbid();
        IsAdmin = User.IsInRole(UserRoles.Admin);

        var customer = track
            ? await _context.Customers.FirstOrDefaultAsync(c => c.Id == id)
            : await _context.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (customer == null) return NotFound();
        Customer = customer;

        IQueryable<Installation> q = _context.Installations.Where(i => i.CustomerId == id);
        if (!track) q = q.AsNoTracking();
        if (!IsAdmin)
        {
            if (user.PartnerId == null) return Forbid();
            q = q.Where(i => i.PartnerId == user.PartnerId);
        }
        Sites = await q.OrderBy(i => i.SiteName).ToListAsync();
        if (!IsAdmin && Sites.Count == 0) return Forbid();
        return null;
    }

    /// <summary>A partner may touch all-site recipients and those of their own sites.</summary>
    private bool CanTouch(CustomerNotificationRecipient r) =>
        IsAdmin || r.InstallationId == null || Sites.Any(s => s.Id == r.InstallationId);

    private bool IsAccessibleSite(int? siteId) => siteId == null || Sites.Any(s => s.Id == siteId);

    private async Task<CustomerNotificationRecipient?> FindRecipientAsync(int id, int recipientId)
    {
        var r = await _context.CustomerNotificationRecipients
            .FirstOrDefaultAsync(x => x.Id == recipientId && x.CustomerId == id);
        return r != null && CanTouch(r) ? r : null;
    }

    // ── GET ────────────────────────────────────────────────────────────

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var denied = await AuthorizeAsync(id);
        if (denied != null) return denied;

        Recipients = (await _context.CustomerNotificationRecipients.AsNoTracking()
                .Where(r => r.CustomerId == id)
                .OrderBy(r => r.Channel).ThenBy(r => r.CreatedAt)
                .ToListAsync())
            .Where(CanTouch)
            .ToList();

        var now = DateTime.UtcNow;
        PendingCodes = (await _context.LineBindingCodes.AsNoTracking()
                .Where(c => c.CustomerId == id && c.UsedAt == null && c.ExpiresAt > now)
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync())
            .Where(c => IsAccessibleSite(c.InstallationId))
            .ToList();

        var prefs = await _context.CustomerNotificationPreferences.AsNoTracking()
            .Where(p => p.CustomerId == id)
            .ToListAsync();
        HasPreferenceRows = prefs.Count > 0;
        foreach (var evt in CustomerNotificationEvents.Matrix)
        {
            var p = prefs.FirstOrDefault(x => x.EventType == evt);
            var d = CustomerNotificationEvents.CustomerDefault(evt, Sites);
            Matrix.Add(new MatrixRow(evt, CustomerNotificationEvents.Label(evt),
                p?.LineEnabled ?? d, p?.EmailEnabled ?? d, p == null));
        }

        var withQuiet = Sites.Where(s => s.QuietHoursStart.HasValue && s.QuietHoursEnd.HasValue).ToList();
        if (withQuiet.Count > 0)
        {
            QuietStart = withQuiet[0].QuietHoursStart!.Value.ToString("HH:mm");
            QuietEnd = withQuiet[0].QuietHoursEnd!.Value.ToString("HH:mm");
        }
        QuietHoursMixed = Sites.Select(s => (s.QuietHoursStart, s.QuietHoursEnd)).Distinct().Count() > 1;

        BaseUrl = await NotificationSettings.GetAppBaseUrlAsync(_context, _configuration)
                  ?? $"{Request.Scheme}://{Request.Host}";
        BasicId = await NotificationSettings.GetLineBasicIdAsync(_context);
        EmailConfigured = await _email.IsConfiguredAsync();
        return Page();
    }

    // ── Recipients ─────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostAddEmailAsync(int id, string? email, string? label, int? siteId)
    {
        var denied = await AuthorizeAsync(id);
        if (denied != null) return denied;

        email = email?.Trim();
        if (!EmailSettings.IsValidAddress(email))
        {
            ErrorMessage = "รูปแบบอีเมลไม่ถูกต้อง";
            return RedirectToPage(new { id });
        }
        if (!IsAccessibleSite(siteId))
        {
            ErrorMessage = "ไม่มีสิทธิ์กับไซต์นี้";
            return RedirectToPage(new { id });
        }

        var exists = await _context.CustomerNotificationRecipients.AnyAsync(r =>
            r.CustomerId == id && r.Channel == NotificationChannel.Email && r.Address == email);
        if (exists)
        {
            ErrorMessage = $"มี {email} อยู่ในรายชื่อแล้ว";
            return RedirectToPage(new { id });
        }

        _context.CustomerNotificationRecipients.Add(new CustomerNotificationRecipient
        {
            CustomerId = id,
            InstallationId = siteId,
            Channel = NotificationChannel.Email,
            Address = email!,
            DisplayName = string.IsNullOrWhiteSpace(label) ? null : label.Trim()[..Math.Min(100, label.Trim().Length)],
            IsEnabled = true,
        });
        await _context.SaveChangesAsync();
        StatusMessage = $"เพิ่มผู้รับอีเมล {email} แล้ว — กด \"ส่งทดสอบ\" เพื่อยืนยันว่าอีเมลถึงจริง";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostToggleAsync(int id, int recipientId)
    {
        var denied = await AuthorizeAsync(id);
        if (denied != null) return denied;
        var r = await FindRecipientAsync(id, recipientId);
        if (r == null) return NotFound();

        r.IsEnabled = !r.IsEnabled;
        await _context.SaveChangesAsync();
        StatusMessage = r.IsEnabled ? "เปิดการแจ้งเตือนผู้รับนี้แล้ว" : "ปิดการแจ้งเตือนผู้รับนี้แล้ว";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostScopeAsync(int id, int recipientId, int? siteId)
    {
        var denied = await AuthorizeAsync(id);
        if (denied != null) return denied;
        var r = await FindRecipientAsync(id, recipientId);
        if (r == null) return NotFound();
        // A partner widening a recipient to "every site" would route other
        // partners' sites to someone they chose.
        if (!IsAccessibleSite(siteId) || (siteId == null && !IsAdmin && r.InstallationId != null))
        {
            ErrorMessage = "ไม่มีสิทธิ์เปลี่ยนเป็นขอบเขตนี้";
            return RedirectToPage(new { id });
        }

        r.InstallationId = siteId;
        await _context.SaveChangesAsync();
        StatusMessage = $"ผู้รับนี้จะได้รับแจ้งเตือนของ: {SiteNameFor(siteId)}";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id, int recipientId)
    {
        var denied = await AuthorizeAsync(id);
        if (denied != null) return denied;
        var r = await FindRecipientAsync(id, recipientId);
        if (r == null) return NotFound();

        _context.CustomerNotificationRecipients.Remove(r);
        await _context.SaveChangesAsync();
        StatusMessage = "ลบผู้รับแล้ว";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostTestAsync(int id, int recipientId)
    {
        var denied = await AuthorizeAsync(id);
        if (denied != null) return denied;
        var r = await FindRecipientAsync(id, recipientId);
        if (r == null) return NotFound();

        var (ok, msg) = await _notifications.SendTestAsync(r);
        if (ok) StatusMessage = msg; else ErrorMessage = msg;
        return RedirectToPage(new { id });
    }

    // ── LINE binding links ─────────────────────────────────────────────

    public async Task<IActionResult> OnPostCreateLinkAsync(int id, int? siteId)
    {
        var denied = await AuthorizeAsync(id);
        if (denied != null) return denied;
        // A partner's link always names one of their sites: an all-sites
        // binding would deliver other partners' alerts to their contact.
        if (!IsAdmin && siteId == null && Sites.Count == 1) siteId = Sites[0].Id;
        if (!IsAccessibleSite(siteId) || (!IsAdmin && siteId == null))
        {
            ErrorMessage = "กรุณาเลือกไซต์";
            return RedirectToPage(new { id });
        }

        var me = await _userManager.GetUserAsync(User);
        var code = await _binding.CreateCodeAsync(id, siteId, me?.Id);
        NewCode = code.Code;
        StatusMessage = "สร้างลิงก์ผูก LINE แล้ว — ส่งลิงก์นี้ให้ลูกค้า (ใช้ได้ 7 วัน ครั้งเดียว)";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRevokeCodeAsync(int id, int codeId)
    {
        var denied = await AuthorizeAsync(id);
        if (denied != null) return denied;
        var code = await _context.LineBindingCodes.FirstOrDefaultAsync(c => c.Id == codeId && c.CustomerId == id);
        if (code == null || !IsAccessibleSite(code.InstallationId)) return NotFound();

        // Expire rather than delete: the landing page then says "หมดอายุ"
        // instead of "ไม่พบ", which is the truth.
        if (code.UsedAt == null) code.ExpiresAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        StatusMessage = "ยกเลิกลิงก์แล้ว";
        return RedirectToPage(new { id });
    }

    // ── Event matrix ───────────────────────────────────────────────────

    public async Task<IActionResult> OnPostSavePrefsAsync(int id, List<PrefInput> prefs)
    {
        var denied = await AuthorizeAsync(id, track: true);
        if (denied != null) return denied;

        var existing = await _context.CustomerNotificationPreferences
            .Where(p => p.CustomerId == id)
            .ToListAsync();
        var now = DateTime.UtcNow;
        foreach (var input in prefs.Where(p => CustomerNotificationEvents.Matrix.Contains(p.EventType)))
        {
            var row = existing.FirstOrDefault(p => p.EventType == input.EventType);
            if (row == null)
            {
                row = new CustomerNotificationPreference { CustomerId = id, EventType = input.EventType };
                _context.CustomerNotificationPreferences.Add(row);
                existing.Add(row);
            }
            row.LineEnabled = input.Line;
            row.EmailEnabled = input.Email;
            row.UpdatedAt = now;

            foreach (var site in Sites)
                CustomerNotificationEvents.ApplyLineToSiteFlag(site, input.EventType, input.Line);
        }
        await _context.SaveChangesAsync();
        StatusMessage = "บันทึกเรื่องที่จะแจ้งแล้ว";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostResetPrefsAsync(int id)
    {
        var denied = await AuthorizeAsync(id);
        if (denied != null) return denied;

        var rows = await _context.CustomerNotificationPreferences.Where(p => p.CustomerId == id).ToListAsync();
        _context.CustomerNotificationPreferences.RemoveRange(rows);
        await _context.SaveChangesAsync();
        StatusMessage = "กลับไปใช้ค่าตามสวิตช์แจ้งเตือนของแต่ละไซต์แล้ว";
        return RedirectToPage(new { id });
    }

    // ── Quiet hours ────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostQuietHoursAsync(int id, string? quietStart, string? quietEnd, bool clear)
    {
        var denied = await AuthorizeAsync(id, track: true);
        if (denied != null) return denied;

        TimeOnly? start = null, end = null;
        if (!clear)
        {
            if (!TimeOnly.TryParse(quietStart, out var s) || !TimeOnly.TryParse(quietEnd, out var e))
            {
                ErrorMessage = "กรุณาใส่เวลาเริ่มและสิ้นสุดให้ครบ";
                return RedirectToPage(new { id });
            }
            if (s == e)
            {
                ErrorMessage = "เวลาเริ่มและสิ้นสุดต้องไม่เท่ากัน";
                return RedirectToPage(new { id });
            }
            (start, end) = (s, e);
        }

        foreach (var site in Sites)
        {
            site.QuietHoursStart = start;
            site.QuietHoursEnd = end;
        }
        await _context.SaveChangesAsync();
        StatusMessage = clear
            ? $"ยกเลิกช่วงงดแจ้งเตือนของ {Sites.Count} ไซต์แล้ว"
            : $"ตั้งช่วงงดแจ้งเตือน {start:HH:mm}–{end:HH:mm} ให้ {Sites.Count} ไซต์แล้ว";
        return RedirectToPage(new { id });
    }
}
