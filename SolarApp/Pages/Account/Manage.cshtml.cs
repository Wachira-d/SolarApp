using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SolarApp.Models;

namespace SolarApp.Pages.Account;

[Authorize]
public class ManageModel : PageModel
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;

    public ManageModel(SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager)
    {
        _signInManager = signInManager;
        _userManager = userManager;
    }

    public ApplicationUser CurrentUser { get; set; } = null!;
    public IList<UserLoginInfo> LinkedLogins { get; set; } = new List<UserLoginInfo>();
    public IList<AuthenticationScheme> AvailableProviders { get; set; } = new List<AuthenticationScheme>();

    /// <summary>True when the user already has a local password (typical case).
    /// False for users that signed up via LINE/Microsoft only and never set one
    /// — those see "Set password" instead of "Change password".</summary>
    public bool HasLocalPassword { get; set; }

    [BindProperty] public PasswordInput Password { get; set; } = new();

    public class PasswordInput
    {
        public string? CurrentPassword { get; set; }

        [Required]
        [DataType(DataType.Password)]
        [StringLength(100, MinimumLength = 8, ErrorMessage = "Password must be at least 8 characters")]
        public string NewPassword { get; set; } = "";

        [DataType(DataType.Password)]
        [Compare(nameof(NewPassword), ErrorMessage = "Passwords do not match")]
        public string ConfirmPassword { get; set; } = "";
    }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Forbid();

        CurrentUser = user;
        LinkedLogins = await _userManager.GetLoginsAsync(user);
        HasLocalPassword = await _userManager.HasPasswordAsync(user);

        var allSchemes = await _signInManager.GetExternalAuthenticationSchemesAsync();
        var linkedNames = LinkedLogins.Select(l => l.LoginProvider).ToHashSet();
        AvailableProviders = allSchemes.Where(s => !linkedNames.Contains(s.Name)).ToList();
        return Page();
    }

    /// <summary>
    /// Change (or set, for external-login-only accounts) the local password.
    /// CurrentPassword is required for change; ignored for the set-password
    /// case (HasLocalPassword=false) because there's nothing to verify.
    /// </summary>
    public async Task<IActionResult> OnPostChangePasswordAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Forbid();

        var hasPassword = await _userManager.HasPasswordAsync(user);

        if (hasPassword)
        {
            if (string.IsNullOrEmpty(Password.CurrentPassword))
            {
                ErrorMessage = "Current password is required.";
                return RedirectToPage();
            }
            var result = await _userManager.ChangePasswordAsync(user, Password.CurrentPassword, Password.NewPassword);
            if (!result.Succeeded)
            {
                ErrorMessage = "เปลี่ยนรหัสไม่สำเร็จ: " + string.Join(", ", result.Errors.Select(e => e.Description));
                return RedirectToPage();
            }
        }
        else
        {
            // External-login-only user setting their first local password
            var result = await _userManager.AddPasswordAsync(user, Password.NewPassword);
            if (!result.Succeeded)
            {
                ErrorMessage = "ตั้งรหัสไม่สำเร็จ: " + string.Join(", ", result.Errors.Select(e => e.Description));
                return RedirectToPage();
            }
        }

        await _signInManager.RefreshSignInAsync(user);
        StatusMessage = hasPassword
            ? "เปลี่ยนรหัสผ่านเรียบร้อยแล้ว"
            : "ตั้งรหัสผ่านครั้งแรกเรียบร้อยแล้ว — คุณสามารถเข้าระบบด้วย email + password ได้แล้ว";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUnlinkAsync(string loginProvider, string providerKey)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Forbid();

        // Prevent unlink if that would leave the user with no login method at all
        var passwordSet = await _userManager.HasPasswordAsync(user);
        var logins = await _userManager.GetLoginsAsync(user);
        if (!passwordSet && logins.Count <= 1)
        {
            ErrorMessage = "Cannot unlink your only login method — set a password first or link another provider.";
            return RedirectToPage();
        }

        var result = await _userManager.RemoveLoginAsync(user, loginProvider, providerKey);
        if (!result.Succeeded)
        {
            ErrorMessage = "Unlink failed: " +
                string.Join(", ", result.Errors.Select(e => e.Description));
            return RedirectToPage();
        }

        // Clear the LineUserId cache on the ApplicationUser if we unlinked LINE
        if (loginProvider == "LINE")
        {
            user.LineUserId = null;
            await _userManager.UpdateAsync(user);
        }

        await _signInManager.RefreshSignInAsync(user);
        StatusMessage = $"{loginProvider} account unlinked.";
        return RedirectToPage();
    }
}
