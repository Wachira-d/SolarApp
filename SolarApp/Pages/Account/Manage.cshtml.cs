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

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Forbid();

        CurrentUser = user;
        LinkedLogins = await _userManager.GetLoginsAsync(user);

        var allSchemes = await _signInManager.GetExternalAuthenticationSchemesAsync();
        var linkedNames = LinkedLogins.Select(l => l.LoginProvider).ToHashSet();
        AvailableProviders = allSchemes.Where(s => !linkedNames.Contains(s.Name)).ToList();
        return Page();
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
