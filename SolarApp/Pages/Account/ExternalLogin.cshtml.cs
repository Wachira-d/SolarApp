using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Pages.Account;

[AllowAnonymous]
public class ExternalLoginModel : PageModel
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<ExternalLoginModel> _logger;

    public ExternalLoginModel(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        ILogger<ExternalLoginModel> logger)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _logger = logger;
    }

    [TempData] public string? ErrorMessage { get; set; }

    /// <summary>
    /// POST /Account/ExternalLogin?provider=Microsoft — kicks off the OAuth challenge.
    /// The browser is redirected to the provider (Microsoft/LINE), which redirects back
    /// to /signin-{provider}, which the auth middleware then funnels to OnGetCallback.
    /// </summary>
    public IActionResult OnPost(string provider, string? returnUrl = null)
    {
        if (string.IsNullOrEmpty(provider) ||
            (provider != "Microsoft" && provider != "LINE"))
            return BadRequest("Unsupported provider");

        // returnUrl sanitation
        if (!string.IsNullOrEmpty(returnUrl) && !Url.IsLocalUrl(returnUrl))
            returnUrl = "/";

        var redirectUrl = Url.Page("./ExternalLogin", pageHandler: "Callback",
            values: new { returnUrl });
        var properties = _signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);
        return Challenge(properties, provider);
    }

    public async Task<IActionResult> OnGetCallbackAsync(string? returnUrl = null, string? remoteError = null)
    {
        returnUrl ??= Url.Content("~/");
        if (!Url.IsLocalUrl(returnUrl)) returnUrl = "/";

        if (remoteError != null)
        {
            ErrorMessage = $"External login error: {remoteError}";
            return RedirectToPage("./Login");
        }

        var info = await _signInManager.GetExternalLoginInfoAsync();
        if (info == null)
        {
            ErrorMessage = "Could not retrieve external login info. Try again.";
            return RedirectToPage("./Login");
        }

        // Try sign-in with existing external login
        var result = await _signInManager.ExternalLoginSignInAsync(
            info.LoginProvider, info.ProviderKey, isPersistent: false, bypassTwoFactor: true);
        if (result.Succeeded)
        {
            _logger.LogInformation("{Provider} user signed in via external login",
                info.LoginProvider);
            return LocalRedirect(returnUrl);
        }
        if (result.IsLockedOut)
        {
            ErrorMessage = "Account locked. Try again in 15 minutes.";
            return RedirectToPage("./Login");
        }

        // No local user linked yet — try to find by email (Microsoft) or create one
        var email = info.Principal.FindFirstValue(ClaimTypes.Email);
        var displayName = info.Principal.FindFirstValue(ClaimTypes.Name)
                       ?? info.Principal.FindFirstValue(ClaimTypes.GivenName);
        var providerKey = info.ProviderKey;

        ApplicationUser? user = null;
        if (!string.IsNullOrEmpty(email))
            user = await _userManager.FindByEmailAsync(email);

        if (user == null)
        {
            // Create a new Customer-role user from external info.
            // For LINE there's no email — synthesise a placeholder using providerKey.
            var fakeEmail = email ?? $"{info.LoginProvider.ToLower()}.{providerKey}@solarapp.local";
            user = new ApplicationUser
            {
                UserName = fakeEmail,
                Email = fakeEmail,
                EmailConfirmed = !string.IsNullOrEmpty(email),
                FullName = displayName ?? fakeEmail,
                IsActive = true,
                LineUserId = info.LoginProvider == "LINE" ? providerKey : null
            };
            var createResult = await _userManager.CreateAsync(user);
            if (!createResult.Succeeded)
            {
                ErrorMessage = "Could not create local account: " +
                    string.Join(", ", createResult.Errors.Select(e => e.Description));
                return RedirectToPage("./Login");
            }
            await _userManager.AddToRoleAsync(user, UserRoles.Customer);
            _logger.LogInformation("Created local user {Email} from {Provider} login",
                user.Email, info.LoginProvider);
        }

        // Link the external login to this local user
        var addLoginResult = await _userManager.AddLoginAsync(user, info);
        if (!addLoginResult.Succeeded)
        {
            ErrorMessage = "Could not link external login: " +
                string.Join(", ", addLoginResult.Errors.Select(e => e.Description));
            return RedirectToPage("./Login");
        }

        // Save tokens (access/refresh) for later use if needed
        await _signInManager.UpdateExternalAuthenticationTokensAsync(info);

        await _signInManager.SignInAsync(user, isPersistent: false);
        return LocalRedirect(returnUrl);
    }
}
