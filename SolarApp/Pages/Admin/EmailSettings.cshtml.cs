using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.Notifications;
using SolarApp.Services.Security;

namespace SolarApp.Pages.Admin;

/// <summary>
/// SMTP account for customer alert e-mail. The password is encrypted with the
/// same credential protector as inverter cloud passwords and is never sent
/// back to the browser — the field is write-only.
/// </summary>
[Authorize(Roles = "Admin")]
public class EmailSettingsModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly ICredentialProtector _protector;
    private readonly ICustomerEmailSender _email;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _configuration;

    public EmailSettingsModel(ApplicationDbContext context, ICredentialProtector protector,
        ICustomerEmailSender email, UserManager<ApplicationUser> userManager, IConfiguration configuration)
    {
        _context = context;
        _protector = protector;
        _email = email;
        _userManager = userManager;
        _configuration = configuration;
    }

    [BindProperty] public string? SmtpHost { get; set; }
    [BindProperty] public int SmtpPort { get; set; } = 587;
    [BindProperty] public bool UseSsl { get; set; } = true;
    [BindProperty] public string? Username { get; set; }
    /// <summary>Blank = keep the stored password.</summary>
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public bool ClearPassword { get; set; }
    [BindProperty] public string? FromAddress { get; set; }
    [BindProperty] public string? FromName { get; set; }
    [BindProperty] public string? TestAddress { get; set; }

    public bool HasPassword { get; private set; }
    public bool IsConfigured { get; private set; }
    public List<NotificationLog> RecentEmails { get; private set; } = new();

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        var s = await EmailSettings.LoadAsync(_context, _protector);
        SmtpHost = s.Host;
        SmtpPort = s.Port;
        UseSsl = s.UseSsl;
        Username = s.Username;
        FromAddress = s.FromAddress;
        FromName = s.FromName;
        HasPassword = !string.IsNullOrEmpty(await NotificationSettings.GetAsync(_context, EmailSettings.PasswordKey));
        IsConfigured = s.IsConfigured;

        var me = await _userManager.GetUserAsync(User);
        TestAddress = me?.Email;

        RecentEmails = await _context.NotificationLogs.AsNoTracking()
            .Where(n => n.Channel == NotificationChannel.Email)
            .OrderByDescending(n => n.CreatedAt)
            .Take(20)
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!string.IsNullOrWhiteSpace(FromAddress) && !EmailSettings.IsValidAddress(FromAddress))
        {
            ErrorMessage = "อีเมลผู้ส่งไม่ถูกต้อง";
            return RedirectToPage();
        }
        if (SmtpPort is < 1 or > 65535)
        {
            ErrorMessage = "พอร์ตไม่ถูกต้อง";
            return RedirectToPage();
        }

        await UpsertAsync(EmailSettings.HostKey, SmtpHost?.Trim() ?? "", "SMTP host for customer alert e-mail");
        await UpsertAsync(EmailSettings.PortKey, SmtpPort.ToString(), "SMTP port (465 = SSL, 587 = STARTTLS)");
        await UpsertAsync(EmailSettings.UseSslKey, UseSsl ? "true" : "false", "SMTP TLS on/off");
        await UpsertAsync(EmailSettings.UsernameKey, Username?.Trim() ?? "", "SMTP username");
        await UpsertAsync(EmailSettings.FromAddressKey, FromAddress?.Trim() ?? "", "From address for customer e-mail");
        await UpsertAsync(EmailSettings.FromNameKey, FromName?.Trim() ?? "", "From display name for customer e-mail");

        if (ClearPassword)
            await UpsertAsync(EmailSettings.PasswordKey, "", "SMTP password (encrypted)");
        else if (!string.IsNullOrEmpty(Password))
            await UpsertAsync(EmailSettings.PasswordKey, _protector.Protect(Password) ?? "", "SMTP password (encrypted)");

        await _context.SaveChangesAsync();
        StatusMessage = "บันทึกการตั้งค่าอีเมลแล้ว";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync()
    {
        if (!EmailSettings.IsValidAddress(TestAddress))
        {
            ErrorMessage = "กรุณาใส่อีเมลปลายทางที่ถูกต้อง";
            return RedirectToPage();
        }

        var log = new NotificationLog
        {
            RecipientLineId = TestAddress!.Trim(),
            Channel = NotificationChannel.Email,
            Title = "Test e-mail",
            Message = "ทดสอบการส่งอีเมลจากหน้าตั้งค่า",
            RelatedEntityType = "Test",
        };
        try
        {
            var baseUrl = await NotificationSettings.GetAppBaseUrlAsync(_context, _configuration);
            await _email.SendAsync(TestAddress!.Trim(), "[SolarApp] ทดสอบการส่งอีเมลแจ้งเตือน",
                CustomerEmailTemplate.BuildTestHtml(baseUrl));
            log.Status = NotificationStatus.Sent;
            log.SentAt = DateTime.UtcNow;
            StatusMessage = $"ส่งอีเมลทดสอบไปที่ {TestAddress} แล้ว — ตรวจกล่องจดหมาย (และ Junk)";
        }
        catch (Exception ex)
        {
            log.Status = NotificationStatus.Failed;
            log.ErrorMessage = ex.Message;
            ErrorMessage = $"ส่งไม่สำเร็จ: {ex.Message}";
        }
        _context.NotificationLogs.Add(log);
        await _context.SaveChangesAsync();
        return RedirectToPage();
    }

    private async Task UpsertAsync(string key, string value, string description)
    {
        var row = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (row == null)
        {
            _context.SystemSettings.Add(new SystemSetting { Key = key, Value = value, Description = description });
        }
        else
        {
            row.Value = value;
            row.UpdatedAt = DateTime.UtcNow;
        }
    }
}
