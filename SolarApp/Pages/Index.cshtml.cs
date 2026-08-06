using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SolarApp.Models.Enums;

namespace SolarApp.Pages;

public class IndexModel : PageModel
{
    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated != true)
            return RedirectToPage("/Account/Login");

        // Customers were landing on the operator dashboard — offline counts,
        // work orders, partner columns — when the portal built for them is at
        // /My.  Anyone who also holds an operator role keeps the dashboard,
        // since that's the screen they log in to work from.
        if (User.IsInRole(UserRoles.Customer)
            && !User.IsInRole(UserRoles.Admin)
            && !User.IsInRole(UserRoles.Partner))
        {
            return RedirectToPage("/My/Index");
        }

        return RedirectToPage("/Dashboard/Index");
    }
}
