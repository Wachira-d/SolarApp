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
    [BindProperty] public string? AlertGroupId { get; set; }
    [BindProperty] public string? TestLineUserId { get; set; }
    /// <summary>Public base URL (https://…) used for tappable deep-links in LINE messages.</summary>
    [BindProperty] public string? AppBaseUrl { get; set; }

    public bool IsTokenConfigured { get; set; }
    public string TokenSource { get; set; } = "Not set";
    public string? CurrentAlertGroupId { get; set; }
    public string? CurrentAppBaseUrl { get; set; }
    public List<NotificationLog> RecentNotifications { get; set; } = new();

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }
    [TempData] public string? TestResult { get; set; }
    [TempData] public bool TestSuccess { get; set; }

    public async Task OnGetAsync()
    {
        var dbToken = await _context.SystemSettings
            .FirstOrDefaultAsync(s => s.Key == "Line.ChannelAccessToken");
        var dbSecret = await _context.SystemSettings
            .FirstOrDefaultAsync(s => s.Key == "Line.ChannelSecret");
        var dbGroupId = await _context.SystemSettings
            .FirstOrDefaultAsync(s => s.Key == "Line.AlertGroupId");

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

        CurrentAlertGroupId = dbGroupId?.Value;
        AlertGroupId = CurrentAlertGroupId;

        var dbBaseUrl = await _context.SystemSettings
            .FirstOrDefaultAsync(s => s.Key == "App.BaseUrl");
        CurrentAppBaseUrl = !string.IsNullOrWhiteSpace(dbBaseUrl?.Value)
            ? dbBaseUrl.Value
            : _configuration["App:BaseUrl"];
        AppBaseUrl = dbBaseUrl?.Value;

        var currentUser = await _userManager.GetUserAsync(User);
        if (!string.IsNullOrEmpty(currentUser?.LineUserId))
            TestLineUserId = currentUser.LineUserId;

        RecentNotifications = await _context.NotificationLogs
            .Where(n => n.Channel == NotificationChannel.Line)
            .OrderByDescending(n => n.CreatedAt)
            .Take(20)
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!string.IsNullOrWhiteSpace(ChannelAccessToken) && !IsMasked(ChannelAccessToken))
        {
            await UpsertSettingAsync("Line.ChannelAccessToken", ChannelAccessToken,
                "LINE Messaging API - Channel Access Token");
        }

        if (!string.IsNullOrWhiteSpace(ChannelSecret) && !IsMasked(ChannelSecret))
        {
            await UpsertSettingAsync("Line.ChannelSecret", ChannelSecret,
                "LINE Messaging API - Channel Secret");
        }

        // Alert Group ID (always save, even if empty = clear)
        await UpsertSettingAsync("Line.AlertGroupId", AlertGroupId?.Trim() ?? "",
            "LINE Group ID for system alerts (all alerts sent here)");

        // Public base URL for deep-links in LINE messages (always save).
        // Normalise: strip trailing slash so "{base}/Admin/Alerts" is clean.
        await UpsertSettingAsync("App.BaseUrl", AppBaseUrl?.Trim().TrimEnd('/') ?? "",
            "Public base URL (https://…) for tappable links in LINE messages");

        StatusMessage = "LINE settings saved!";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestUserAsync()
    {
        if (string.IsNullOrWhiteSpace(TestLineUserId))
        {
            ErrorMessage = "Enter a LINE User ID to test";
            return RedirectToPage();
        }

        string? overrideToken = null;
        if (!string.IsNullOrWhiteSpace(ChannelAccessToken) && !IsMasked(ChannelAccessToken))
            overrideToken = ChannelAccessToken;

        var (success, message) = await _lineService.SendTestMessageAsync(TestLineUserId!, overrideToken);
        TestSuccess = success;
        TestResult = $"[User] {message}";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestGroupAsync()
    {
        if (string.IsNullOrWhiteSpace(AlertGroupId))
        {
            ErrorMessage = "Enter a Group ID to test";
            return RedirectToPage();
        }

        string? overrideToken = null;
        if (!string.IsNullOrWhiteSpace(ChannelAccessToken) && !IsMasked(ChannelAccessToken))
            overrideToken = ChannelAccessToken;

        var (success, message) = await _lineService.SendTestMessageAsync(AlertGroupId!, overrideToken);
        TestSuccess = success;
        TestResult = $"[Group] {message}";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostClearTokenAsync()
    {
        foreach (var key in new[] { "Line.ChannelAccessToken", "Line.ChannelSecret", "Line.AlertGroupId" })
        {
            var s = await _context.SystemSettings.FirstOrDefaultAsync(x => x.Key == key);
            if (s != null) _context.SystemSettings.Remove(s);
        }
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
                Key = key, Value = value, Description = description
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
