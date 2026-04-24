using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Services.Security;

namespace SolarApp.Pages.Admin;

[Authorize(Roles = "Admin")]
public class ExternalAuthModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly ICredentialProtector _protector;
    private readonly IConfiguration _configuration;

    public ExternalAuthModel(
        ApplicationDbContext context,
        ICredentialProtector protector,
        IConfiguration configuration)
    {
        _context = context;
        _protector = protector;
        _configuration = configuration;
    }

    [BindProperty] public string? MsClientId { get; set; }
    [BindProperty] public string? MsClientSecret { get; set; }
    [BindProperty] public string? LineClientId { get; set; }
    [BindProperty] public string? LineClientSecret { get; set; }

    public string MicrosoftCallbackUrl { get; set; } = "";
    public string LineCallbackUrl { get; set; } = "";

    public bool MsConfiguredNow { get; set; }
    public bool LineConfiguredNow { get; set; }
    public string MsConfigSource { get; set; } = "";
    public string LineConfigSource { get; set; } = "";

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        // Pull values from DB first, fall back to appsettings.json
        var dbMsId = await GetSettingAsync("Auth.Microsoft.ClientId");
        var dbMsSecret = await GetSettingAsync("Auth.Microsoft.ClientSecret");
        var dbLineId = await GetSettingAsync("Auth.LINE.ClientId");
        var dbLineSecret = await GetSettingAsync("Auth.LINE.ClientSecret");

        MsClientId = dbMsId ?? _configuration["Authentication:Microsoft:ClientId"];
        // Don't echo secret back to form for security — show masked length instead
        MsClientSecret = null;
        LineClientId = dbLineId ?? _configuration["Authentication:LINE:ClientId"];
        LineClientSecret = null;

        MsConfiguredNow = !string.IsNullOrWhiteSpace(MsClientId)
                       && !string.IsNullOrWhiteSpace(dbMsSecret ?? _configuration["Authentication:Microsoft:ClientSecret"]);
        LineConfiguredNow = !string.IsNullOrWhiteSpace(LineClientId)
                         && !string.IsNullOrWhiteSpace(dbLineSecret ?? _configuration["Authentication:LINE:ClientSecret"]);

        MsConfigSource = dbMsId != null ? "Database (web-configured)"
                       : !string.IsNullOrWhiteSpace(_configuration["Authentication:Microsoft:ClientId"]) ? "appsettings.json"
                       : "Not set";
        LineConfigSource = dbLineId != null ? "Database (web-configured)"
                         : !string.IsNullOrWhiteSpace(_configuration["Authentication:LINE:ClientId"]) ? "appsettings.json"
                         : "Not set";

        var req = HttpContext.Request;
        var baseUrl = $"{req.Scheme}://{req.Host}";
        MicrosoftCallbackUrl = $"{baseUrl}/signin-microsoft";
        LineCallbackUrl = $"{baseUrl}/signin-line";
    }

    public async Task<IActionResult> OnPostSaveMicrosoftAsync()
    {
        if (string.IsNullOrWhiteSpace(MsClientId) && string.IsNullOrWhiteSpace(MsClientSecret))
        {
            // User wants to clear → remove both settings
            await RemoveSettingAsync("Auth.Microsoft.ClientId");
            await RemoveSettingAsync("Auth.Microsoft.ClientSecret");
            await _context.SaveChangesAsync();
            StatusMessage = "Microsoft auth settings cleared. Restart the app pool to apply.";
            return RedirectToPage();
        }

        if (string.IsNullOrWhiteSpace(MsClientId))
        {
            ErrorMessage = "Microsoft Client ID is required.";
            return RedirectToPage();
        }

        await UpsertSettingAsync("Auth.Microsoft.ClientId", MsClientId.Trim(), encrypted: false,
            "Microsoft/Azure AD Application (Client) ID for external login");

        // Only overwrite the secret if the admin typed a new value. Empty input
        // keeps the existing stored value (don't accidentally wipe it).
        if (!string.IsNullOrWhiteSpace(MsClientSecret))
        {
            await UpsertSettingAsync("Auth.Microsoft.ClientSecret", MsClientSecret.Trim(), encrypted: true,
                "Microsoft client secret (encrypted at rest)");
        }

        await _context.SaveChangesAsync();
        StatusMessage = "Microsoft auth settings saved. " +
                        "⚠ Restart the IIS app pool (or the app process) for changes to take effect.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSaveLineAsync()
    {
        if (string.IsNullOrWhiteSpace(LineClientId) && string.IsNullOrWhiteSpace(LineClientSecret))
        {
            await RemoveSettingAsync("Auth.LINE.ClientId");
            await RemoveSettingAsync("Auth.LINE.ClientSecret");
            await _context.SaveChangesAsync();
            StatusMessage = "LINE Login settings cleared. Restart the app pool to apply.";
            return RedirectToPage();
        }

        if (string.IsNullOrWhiteSpace(LineClientId))
        {
            ErrorMessage = "LINE Channel ID is required.";
            return RedirectToPage();
        }

        await UpsertSettingAsync("Auth.LINE.ClientId", LineClientId.Trim(), encrypted: false,
            "LINE Login Channel ID");

        if (!string.IsNullOrWhiteSpace(LineClientSecret))
        {
            await UpsertSettingAsync("Auth.LINE.ClientSecret", LineClientSecret.Trim(), encrypted: true,
                "LINE Login Channel Secret (encrypted at rest)");
        }

        await _context.SaveChangesAsync();
        StatusMessage = "LINE Login settings saved. " +
                        "⚠ Restart the IIS app pool (or the app process) for changes to take effect.";
        return RedirectToPage();
    }

    private async Task<string?> GetSettingAsync(string key)
    {
        var s = await _context.SystemSettings.FirstOrDefaultAsync(x => x.Key == key);
        if (s == null) return null;
        return _protector.Unprotect(s.Value);
    }

    private async Task UpsertSettingAsync(string key, string value, bool encrypted, string description)
    {
        var stored = encrypted ? (_protector.Protect(value) ?? "") : value;
        var existing = await _context.SystemSettings.FirstOrDefaultAsync(x => x.Key == key);
        if (existing == null)
        {
            _context.SystemSettings.Add(new SystemSetting
            {
                Key = key,
                Value = stored,
                Description = description
            });
        }
        else
        {
            existing.Value = stored;
            existing.Description = description;
            existing.UpdatedAt = DateTime.UtcNow;
        }
    }

    private async Task RemoveSettingAsync(string key)
    {
        var existing = await _context.SystemSettings.FirstOrDefaultAsync(x => x.Key == key);
        if (existing != null) _context.SystemSettings.Remove(existing);
    }
}
