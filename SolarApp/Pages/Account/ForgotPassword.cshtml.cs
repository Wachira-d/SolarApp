using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SolarApp.Pages.Account;

/// <summary>
/// Customer-facing "forgot password" page.  This deployment doesn't have
/// SMTP wired up for end-user emails, so the page does NOT auto-send a
/// reset link.  Instead it explains the manual reset path:
///   1. Show the configured operator contact channels (phone / LINE /
///      email from appsettings → MySolar section).
///   2. Tell the customer to contact the operator with their email
///      address; the operator uses /Admin/Users → Reset Password to
///      generate a temporary one.
/// When SMTP support is added later, swap this page for the standard
/// Identity reset-token flow.
/// </summary>
[AllowAnonymous]
public class ForgotPasswordModel : PageModel
{
    private readonly Microsoft.Extensions.Configuration.IConfiguration _config;

    public ForgotPasswordModel(Microsoft.Extensions.Configuration.IConfiguration config)
    {
        _config = config;
    }

    public string OperatorName  => _config["MySolar:OperatorName"]  ?? "ทีมงาน SolarApp";
    public string? OperatorPhone => _config["MySolar:OperatorPhone"];
    public string? OperatorLine  => _config["MySolar:OperatorLine"];
    public string? OperatorEmail => _config["MySolar:OperatorEmail"];

    public void OnGet() { }
}
