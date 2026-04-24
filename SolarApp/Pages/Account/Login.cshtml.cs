using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SolarApp.Models;

namespace SolarApp.Pages.Account;

public class LoginModel : PageModel
{
    private readonly SignInManager<ApplicationUser> _signInManager;

    public LoginModel(SignInManager<ApplicationUser> signInManager)
    {
        _signInManager = signInManager;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ReturnUrl { get; set; }

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

    public void OnGet(string? returnUrl = null)
    {
        ReturnUrl = returnUrl ?? Url.Content("~/");
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        returnUrl ??= Url.Content("~/");
        // Open-redirect guard
        if (!Url.IsLocalUrl(returnUrl) || returnUrl.StartsWith("//") || returnUrl.Contains(':'))
            returnUrl = Url.Content("~/");

        if (!ModelState.IsValid)
            return Page();

        // lockoutOnFailure=true enables brute-force protection (configured in Startup
        // with 5 attempts / 15-min lockout)
        var result = await _signInManager.PasswordSignInAsync(
            Input.Email, Input.Password, Input.RememberMe, lockoutOnFailure: true);

        if (result.Succeeded)
            return LocalRedirect(returnUrl);

        if (result.IsLockedOut)
        {
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

        // Use a generic message — don't reveal whether email exists
        ModelState.AddModelError(string.Empty, "Invalid email or password.");
        return Page();
    }
}
