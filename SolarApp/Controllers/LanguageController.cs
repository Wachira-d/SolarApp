using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;

namespace SolarApp.Controllers;

[Route("[controller]/[action]")]
public class LanguageController : Controller
{
    [HttpGet]
    public IActionResult Set(string culture, string returnUrl = "/")
    {
        if (string.IsNullOrEmpty(culture)) culture = "en";
        if (culture != "en" && culture != "th") culture = "en";

        Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true });

        if (!Url.IsLocalUrl(returnUrl)) returnUrl = "/";
        return LocalRedirect(returnUrl);
    }
}
