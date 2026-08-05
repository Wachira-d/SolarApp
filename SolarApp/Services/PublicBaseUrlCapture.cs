using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;

namespace SolarApp.Services;

/// <summary>
/// Learns the site's public base URL from real traffic and stores it in
/// SystemSettings["App.BaseUrl"].
///
/// Background jobs (the daily LINE summary, alert messages) need an
/// ABSOLUTE https URL to build tappable links, but they run with no
/// HttpContext so they cannot discover it themselves.  Requiring the
/// operator to type it into Admin → LINE Settings meant the links were
/// silently missing until someone noticed and configured it.
///
/// So: the first genuine external HTTPS request teaches the app its own
/// address, once per process, and only when nothing is configured yet.
/// An operator-set value always wins and is never overwritten.
/// </summary>
public static class PublicBaseUrlCapture
{
    // 0 = not attempted, 1 = attempt in flight or done. Guarantees the DB
    // is touched at most once per process regardless of request volume.
    private static int _attempted;

    public static async Task TryCaptureAsync(HttpContext context)
    {
        if (Interlocked.Exchange(ref _attempted, 1) == 1) return;

        try
        {
            var req = context.Request;
            if (!req.IsHttps) return;                       // links must be https
            var host = req.Host.Host;
            if (string.IsNullOrWhiteSpace(host)) return;

            // Ignore loopback / bare IPs — the keepalive pings localhost and
            // an IP literal is not a usable public link.
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || host.StartsWith("127.")
                || host == "::1"
                || System.Net.IPAddress.TryParse(host, out _))
                return;

            var db = context.RequestServices.GetService<ApplicationDbContext>();
            if (db == null) return;

            var existing = await db.SystemSettings
                .FirstOrDefaultAsync(s => s.Key == "App.BaseUrl");
            if (existing != null && !string.IsNullOrWhiteSpace(existing.Value))
                return;                                     // operator already set it

            var baseUrl = $"{req.Scheme}://{req.Host.Value}".TrimEnd('/');

            if (existing == null)
            {
                db.SystemSettings.Add(new SystemSetting
                {
                    Key = "App.BaseUrl",
                    Value = baseUrl,
                    Description = "Public base URL (auto-detected from traffic; used for links in LINE messages)",
                });
            }
            else
            {
                existing.Value = baseUrl;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync();

            context.RequestServices.GetService<ILoggerFactory>()
                ?.CreateLogger(nameof(PublicBaseUrlCapture))
                .LogInformation("Auto-detected public base URL: {BaseUrl} (used for LINE message links)", baseUrl);
        }
        catch
        {
            // Never let a convenience feature affect a real request.
        }
    }
}
