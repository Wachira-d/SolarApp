using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SolarApp.Data;
using SolarApp.Services.Notifications;

namespace SolarApp.Pages.LineLink;

/// <summary>
/// The page behind a "ผูก LINE" link (/l/{code}). Opened by the customer on
/// their phone, usually from a LINE chat or SMS, never signed in — so it shows
/// nothing but their first name and two buttons. The webhook does the binding
/// once the customer sends the code from LINE.
/// </summary>
[AllowAnonymous]
public class IndexModel : PageModel
{
    private readonly ILineBindingService _binding;
    private readonly ApplicationDbContext _context;
    private readonly IConfiguration _configuration;

    public IndexModel(ILineBindingService binding, ApplicationDbContext context, IConfiguration configuration)
    {
        _binding = binding;
        _context = context;
        _configuration = configuration;
    }

    public LineCodeStatus Status { get; private set; } = LineCodeStatus.NotFound;
    public string Code { get; private set; } = "";
    public string? FirstName { get; private set; }
    public string? SiteName { get; private set; }
    public DateTime? ExpiresAtLocal { get; private set; }
    public string? BasicId { get; private set; }
    public string? AddFriendUrl { get; private set; }
    public string? SendCodeUrl { get; private set; }
    public string LandingUrl { get; private set; } = "";
    public string MessageText => $"ผูกบัญชี {Code}";

    public async Task OnGetAsync(string code)
    {
        // Never cached by a shared proxy: the state flips to "used" the
        // moment the customer sends the code.
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Robots-Tag"] = "noindex";

        var lookup = await _binding.LookupAsync(code);
        Status = lookup.Status;
        Code = lookup.Code ?? LineBindingService.Normalize(code);
        FirstName = lookup.CustomerFirstName;
        SiteName = lookup.SiteName;
        ExpiresAtLocal = lookup.ExpiresAt.HasValue ? Services.SiteTime.ToSiteTime(lookup.ExpiresAt.Value) : null;

        var baseUrl = await NotificationSettings.GetAppBaseUrlAsync(_context, _configuration)
                      ?? $"{Request.Scheme}://{Request.Host}";
        LandingUrl = $"{baseUrl}/l/{Uri.EscapeDataString(Code)}";

        BasicId = await NotificationSettings.GetLineBasicIdAsync(_context);
        if (BasicId != null)
        {
            var id = Uri.EscapeDataString(BasicId);
            AddFriendUrl = $"https://line.me/R/ti/p/{id}";
            SendCodeUrl = $"https://line.me/R/oaMessage/{id}/?{Uri.EscapeDataString(MessageText)}";
        }
    }
}
