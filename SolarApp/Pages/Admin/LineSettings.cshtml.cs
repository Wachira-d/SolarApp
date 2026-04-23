using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.LineApi;

namespace SolarApp.Pages.Admin;

[Authorize(Roles = "Admin")]
public class LineSettingsModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly ILineMessagingService _lineService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _configuration;

    public LineSettingsModel(
        ApplicationDbContext context,
        ILineMessagingService lineService,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration)
    {
        _context = context;
        _lineService = lineService;
        _userManager = userManager;
        _configuration = configuration;
    }

    [BindProperty] public string? ChannelAccessToken { get; set; }
    [BindProperty] public string? ChannelSecret { get; set; }
    [BindProperty] public string? TestLineUserId { get; set; }

    public bool IsTokenConfigured { get; set; }
    public string TokenSource { get; set; } = "Not set";
    public List<NotificationLog> RecentNotifications { get; set; } = new();

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }
    [TempData] public string? TestResult { get; set; }
    [TempData] public bool TestSuccess { get; set; }

    public async Task OnGetAsync()
    {
        // Load current token
        var dbToken = await _context.SystemSettings
            .FirstOrDefaultAsync(s => s.Key == "Line.ChannelAccessToken");
        var dbSecret = await _context.SystemSettings
            .FirstOrDefaultAsync(s => s.Key == "Line.ChannelSecret");

        if (dbToken != null && !string.IsNullOrWhiteSpace(dbToken.Value))
        {
            ChannelAccessToken = MaskToken(dbToken.Value);
            IsTokenConfigured = true;
            TokenSource = "Database (configured via UI)";
        }
        else
        {
            var appsettingsToken = _configuration["LineMessaging:ChannelAccessToken"];
            if (!string.IsNullOrEmpty(appsettingsToken))
            {
                ChannelAccessToken = MaskToken(appsettingsToken);
                IsTokenConfigured = true;
                TokenSource = "appsettings.json (fallback)";
            }
        }

        if (dbSecret != null && !string.IsNullOrWhiteSpace(dbSecret.Value))
            ChannelSecret = MaskToken(dbSecret.Value);

        // Prefill test LINE ID with current user's LINE ID
        var currentUser = await _userManager.GetUserAsync(User);
        if (!string.IsNullOrEmpty(currentUser?.LineUserId))
            TestLineUserId = currentUser.LineUserId;

        // Load recent notifications
        RecentNotifications = await _context.NotificationLogs
            .Where(n => n.Channel == NotificationChannel.Line)
            .OrderByDescending(n => n.CreatedAt)
            .Take(20)
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        // Only save if user entered new values (not masked value)
        if (!string.IsNullOrWhiteSpace(ChannelAccessToken) && !IsMasked(ChannelAccessToken))
        {
            await UpsertSettingAsync("Line.ChannelAccessToken", ChannelAccessToken,
                "LINE Messaging API - Channel Access Token (long-lived)");
        }

        if (!string.IsNullOrWhiteSpace(ChannelSecret) && !IsMasked(ChannelSecret))
        {
            await UpsertSettingAsync("Line.ChannelSecret", ChannelSecret,
                "LINE Messaging API - Channel Secret");
        }

        StatusMessage = "LINE settings saved successfully";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync()
    {
        if (string.IsNullOrWhiteSpace(TestLineUserId))
        {
            ErrorMessage = "Please enter a LINE User ID to test";
            return RedirectToPage();
        }

        // If user entered a new token in the form, use that for testing (not saved yet)
        string? overrideToken = null;
        if (!string.IsNullOrWhiteSpace(ChannelAccessToken) && !IsMasked(ChannelAccessToken))
            overrideToken = ChannelAccessToken;

        var (success, message) = await _lineService.SendTestMessageAsync(TestLineUserId!, overrideToken);

        TestSuccess = success;
        TestResult = message;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostClearTokenAsync()
    {
        var token = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == "Line.ChannelAccessToken");
        if (token != null) _context.SystemSettings.Remove(token);

        var secret = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == "Line.ChannelSecret");
        if (secret != null) _context.SystemSettings.Remove(secret);

        await _context.SaveChangesAsync();
        StatusMessage = "LINE settings cleared";
        return RedirectToPage();
    }

    private async Task UpsertSettingAsync(string key, string value, string description)
    {
        var setting = await _context.SystemSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (setting == null)
        {
            _context.SystemSettings.Add(new SystemSetting
            {
                Key = key,
                Value = value,
                Description = description
            });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = DateTime.UtcNow;
        }
        await _context.SaveChangesAsync();
    }

    private static string MaskToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return "";
        if (token.Length <= 10) return "••••••";
        return token[..6] + new string('•', Math.Min(30, token.Length - 10)) + token[^4..];
    }

    private static bool IsMasked(string value) => value.Contains('•');
}
