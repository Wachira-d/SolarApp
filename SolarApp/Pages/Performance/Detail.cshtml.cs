using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SolarApp.Services.Authorization;
using SolarApp.Services.Performance;

namespace SolarApp.Pages.Performance;

[Authorize]
public class DetailModel : PageModel
{
    private readonly IPerformanceMetricsService _metrics;
    private readonly IInstallationAccessHelper _access;

    public DetailModel(IPerformanceMetricsService metrics, IInstallationAccessHelper access)
    {
        _metrics = metrics;
        _access = access;
    }

    public InstallationPerformance Report { get; set; } = null!;

    [BindProperty(SupportsGet = true)]
    public DateTime From { get; set; } = DateTime.UtcNow.Date.AddDays(-29);

    [BindProperty(SupportsGet = true)]
    public DateTime To { get; set; } = DateTime.UtcNow.Date;

    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (!await _access.CanAccessAsync(id, User)) return Forbid();

        if (To < From) (From, To) = (To, From);
        if ((To - From).TotalDays > 365) From = To.AddDays(-365);

        Report = await _metrics.GetPerformanceAsync(id, From, To);
        return Page();
    }
}
