using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SolarApp.Pages.Maintenance;

/// <summary>
/// The maintenance list and the cleaning-due list now live on the field-work
/// hub (/Jobs), which also shows work orders and tickets. This route stays
/// so old links, LINE messages and bookmarks still land somewhere useful.
/// </summary>
[Authorize]
public class IndexModel : PageModel
{
    public IActionResult OnGet() => RedirectToPagePermanent("/Jobs/Index");
}
