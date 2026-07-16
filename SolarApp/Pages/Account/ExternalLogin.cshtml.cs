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
    [TempData] public string? StatusMessage { get; set; }

    /// <summary>
    /// POST /Account/ExternalLogin?provider=Microsoft|LINE
    /// Two modes:
    ///   - Unauthenticated: kicks off OAuth for SIGN-IN (callback will sign in
    ///     existing user or auto-create if provider is Microsoft).
    ///   - Authenticated: kicks off OAuth for LINKING (callback attaches the
    ///     external identity to the current user; no new user is created).
    /// </summary>
    public IActionResult OnPost(string provider, string? returnUrl = null)
    {
        if (string.IsNullOrEmpty(provider) ||
            (provider != "Microsoft" && provider != "LINE"))
            return BadRequest("Unsupported provider");

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

        // ─── BRANCH A: User is already signed in → LINKING flow ───
        if (User.Identity?.IsAuthenticated == true)
        {
            var currentUser = await _userManager.GetUserAsync(User);
            if (currentUser == null)
            {
                ErrorMessage = "Session lost. Please sign in again.";
                return RedirectToPage("./Login");
            }

            // Ensure this external identity isn't already linked to a different user
            var ownerOfExternal = await _userManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
            if (ownerOfExternal != null && ownerOfExternal.Id != currentUser.Id)
            {
                ErrorMessage = $"This {info.LoginProvider} account is already linked to another SolarApp user. " +
                    "If you believe this is a mistake, ask an admin to unlink it first.";
                return RedirectToPage("./Manage");
            }

            var addResult = await _userManager.AddLoginAsync(currentUser, info);
            if (!addResult.Succeeded)
            {
                ErrorMessage = "Could not link: " +
                    string.Join(", ", addResult.Errors.Select(e => e.Description));
                return RedirectToPage("./Manage");
            }

            // If LINE, also store the LINE userId on the ApplicationUser
            // so we can send LINE notifications.
            if (info.LoginProvider == "LINE" && string.IsNullOrEmpty(currentUser.LineUserId))
            {
                currentUser.LineUserId = info.ProviderKey;
                await _userManager.UpdateAsync(currentUser);
            }

            await _signInManager.UpdateExternalAuthenticationTokensAsync(info);
            await _signInManager.RefreshSignInAsync(currentUser);
            _logger.LogInformation("User {UserId} linked {Provider} account", currentUser.Id, info.LoginProvider);

            StatusMessage = $"{info.LoginProvider} account linked successfully.";
            return RedirectToPage("./Manage");
        }

        // ─── BRANCH B: Not signed in → SIGN-IN flow ───

        // Existing link? Just sign in.
        var signInResult = await _signInManager.ExternalLoginSignInAsync(
            info.LoginProvider, info.ProviderKey, isPersistent: false, bypassTwoFactor: true);
        if (signInResult.Succeeded)
        {
            _logger.LogInformation("{Provider} user signed in via existing external login",
                info.LoginProvider);
            return LocalRedirect(returnUrl);
        }
        if (signInResult.IsLockedOut)
        {
            ErrorMessage = "Account locked. Try again in 15 minutes.";
            return RedirectToPage("./Login");
        }

        // No link yet. Provider-specific policy:
        //   - Microsoft: allow auto-link or auto-create by email (SSO experience)
        //   - LINE: LINK ONLY — the user must already have a local account,
        //     and they must be signed in to link it. Never auto-create.
        if (info.LoginProvider == "LINE")
        {
            ErrorMessage = "LINE login isn't linked to any SolarApp account yet. " +
                "Please sign in with your email + password first, then go to " +
                "Account → Manage Logins to link your LINE account.";
            return RedirectToPage("./Login");
        }

        // Microsoft flow: try to match an existing local user by email
        var email = info.Principal.FindFirstValue(ClaimTypes.Email);
        var displayName = info.Principal.FindFirstValue(ClaimTypes.Name)
                       ?? info.Principal.FindFirstValue(ClaimTypes.GivenName);

        ApplicationUser? user = null;
        if (!string.IsNullOrEmpty(email))
            user = await _userManager.FindByEmailAsync(email);

        if (user != null)
        {
            // SECURITY (nOAuth): DO NOT auto-link an external login to an
            // EXISTING local account based on an email claim.  The IdP token
            // is minted by whatever tenant the user authenticated against
            // (we use /common when no tenant is pinned), so an attacker who
            // can assert a victim's email — e.g. an admin's — would otherwise
            // be linked and signed straight into that privileged account.
            // Linking must be done while already authenticated, from
            // Account → Manage Logins (same policy as LINE).
            _logger.LogWarning(
                "Refused Microsoft auto-link to existing account for email claim (nOAuth guard). Provider={Provider}",
                info.LoginProvider);
            ErrorMessage = "มีบัญชีที่ใช้อีเมลนี้อยู่แล้ว. เพื่อความปลอดภัย กรุณาเข้าสู่ระบบด้วยรหัสผ่านก่อน " +
                "แล้วไปที่ Account → Manage Logins เพื่อผูกบัญชี Microsoft.";
            return RedirectToPage("./Login");
        }

        // No existing account → create a brand-new Customer-role user.
        if (string.IsNullOrEmpty(email))
        {
            ErrorMessage = "Microsoft account did not return an email — cannot create a local account. " +
                "Ask your admin to create a user for you, then link Microsoft from profile settings.";
            return RedirectToPage("./Login");
        }

        user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FullName = displayName ?? email,
            IsActive = true
        };
        var createResult = await _userManager.CreateAsync(user);
        if (!createResult.Succeeded)
        {
            ErrorMessage = "Could not create local account: " +
                string.Join(", ", createResult.Errors.Select(e => e.Description));
            return RedirectToPage("./Login");
        }
        await _userManager.AddToRoleAsync(user, UserRoles.Customer);
        _logger.LogInformation("Created local user from Microsoft login (new Customer)");

        var addLoginResult = await _userManager.AddLoginAsync(user, info);
        if (!addLoginResult.Succeeded)
        {
            ErrorMessage = "Could not link external login: " +
                string.Join(", ", addLoginResult.Errors.Select(e => e.Description));
            return RedirectToPage("./Login");
        }

        await _signInManager.UpdateExternalAuthenticationTokensAsync(info);
        await _signInManager.SignInAsync(user, isPersistent: false);
        return LocalRedirect(returnUrl);
    }
}
