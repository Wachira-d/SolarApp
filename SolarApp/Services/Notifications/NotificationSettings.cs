using Microsoft.EntityFrameworkCore;
using SolarApp.Data;

namespace SolarApp.Services.Notifications;

/// <summary>
/// SystemSettings lookups shared by the notification center (e-mail sender,
/// LINE binding links, delivery). Each piece used to read its own key with
/// its own fallback rules; links built from two different readings of
/// "App.BaseUrl" would disagree the first time someone changed it.
/// </summary>
public static class NotificationSettings
{
    public const string AppBaseUrlKey = "App.BaseUrl";
    public const string LineBasicIdKey = "Line.BasicId";

    public static async Task<string?> GetAsync(ApplicationDbContext context, string key)
    {
        var value = await context.SystemSettings.AsNoTracking()
            .Where(s => s.Key == key)
            .Select(s => s.Value)
            .FirstOrDefaultAsync();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Public base URL ("https://host", no trailing slash) — the same setting
    /// LINE deep links use, with the same appsettings fallback. Null when
    /// unset or not an absolute http(s) URL; callers then omit the link.
    /// </summary>
    public static async Task<string?> GetAppBaseUrlAsync(ApplicationDbContext context, IConfiguration? configuration)
    {
        var raw = await GetAsync(context, AppBaseUrlKey) ?? configuration?["App:BaseUrl"];
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim().TrimEnd('/');
        return Uri.TryCreate(raw, UriKind.Absolute, out var u)
               && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
            ? raw
            : null;
    }

    /// <summary>
    /// The OA's basic id with its leading "@" (what line.me URLs expect).
    /// Operators paste it with or without the "@", so normalise here.
    /// </summary>
    public static async Task<string?> GetLineBasicIdAsync(ApplicationDbContext context)
    {
        var raw = await GetAsync(context, LineBasicIdKey);
        return NormalizeBasicId(raw);
    }

    public static string? NormalizeBasicId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var id = raw.Trim();
        return id.StartsWith('@') ? id : "@" + id;
    }
}
