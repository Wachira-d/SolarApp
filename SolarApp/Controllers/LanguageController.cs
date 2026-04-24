using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;

namespace SolarApp.Controllers;

[Route("[controller]/[action]")]
public class LanguageController : Controller
{
    [HttpGet]
    public IActionResult Set(string culture, string returnUrl = "/")
    {
        // Strict whitelist for culture — never accept arbitrary values
        culture = (culture ?? "").ToLowerInvariant() switch
        {
            "th" => "th",
            "en" => "en",
            _ => "en"
        };

        Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                IsEssential = true,
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = true
            });

        // Strict local URL check to prevent open-redirect attacks
        if (string.IsNullOrEmpty(returnUrl) || !Url.IsLocalUrl(returnUrl) || returnUrl.StartsWith("//") || returnUrl.Contains(':'))
            returnUrl = "/";
        return LocalRedirect(returnUrl);
    }
}
