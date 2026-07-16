using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SolarApp.Models;

namespace SolarApp.Pages.Account;

public class LoginModel : PageModel
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly Data.ApplicationDbContext _db;

    public LoginModel(SignInManager<ApplicationUser> signInManager, Data.ApplicationDbContext db)
    {
        _signInManager = signInManager;
        _db = db;
    }

    private async Task AuditAuthAsync(string action, string email)
    {
        // Auth events aren't entity changes, so the audit interceptor won't
        // catch them — record them explicitly.  Email is masked so the audit
        // trail doesn't itself become a list of valid accounts.
        try
        {
            var masked = string.IsNullOrEmpty(email) ? "(none)"
                : (email.Length <= 2 ? "••" : email[..2] + "•••");
            _db.AuditLogs.Add(new AuditLog
            {
                Action = action,
                EntityType = "Auth",
                EntityId = masked,
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                Timestamp = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync();
        }
        catch { /* auditing must never block login */ }
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ReturnUrl { get; set; }

    public IList<Microsoft.AspNetCore.Authentication.AuthenticationScheme> ExternalProviders { get; set; } =
        new List<Microsoft.AspNetCore.Authentication.AuthenticationScheme>();

    public class InputModel
    {
        [Required(ErrorMessage = "Email is required")]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Password is required")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        public bool RememberMe { get; set; }
    }

    public async Task OnGetAsync(string? returnUrl = null)
    {
        ReturnUrl = returnUrl ?? Url.Content("~/");
        var schemes = await _signInManager.GetExternalAuthenticationSchemesAsync();
        ExternalProviders = schemes.ToList();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        returnUrl ??= Url.Content("~/");
        // Open-redirect guard
        if (!Url.IsLocalUrl(returnUrl) || returnUrl.StartsWith("//") || returnUrl.Contains(':'))
            returnUrl = Url.Content("~/");

        // Re-populate providers in case we re-render on error
        ExternalProviders = (await _signInManager.GetExternalAuthenticationSchemesAsync()).ToList();

        if (!ModelState.IsValid)
            return Page();

        // lockoutOnFailure=true enables brute-force protection (configured in Startup
        // with 5 attempts / 15-min lockout)
        var result = await _signInManager.PasswordSignInAsync(
            Input.Email, Input.Password, Input.RememberMe, lockoutOnFailure: true);

        if (result.Succeeded)
        {
            await AuditAuthAsync("LoginSuccess", Input.Email);
            return LocalRedirect(returnUrl);
        }

        if (result.IsLockedOut)
        {
            await AuditAuthAsync("LoginLockout", Input.Email);
            ModelState.AddModelError(string.Empty,
                "Account locked due to too many failed attempts. Try again in 15 minutes.");
            return Page();
        }
        if (result.IsNotAllowed)
        {
            ModelState.AddModelError(string.Empty,
                "This account is not allowed to sign in. Contact your administrator.");
            return Page();
        }

        await AuditAuthAsync("LoginFailed", Input.Email);
        // Use a generic message — don't reveal whether email exists
        ModelState.AddModelError(string.Empty, "Invalid email or password.");
        return Page();
    }
}
