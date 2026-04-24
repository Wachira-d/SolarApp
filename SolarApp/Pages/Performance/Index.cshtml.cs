using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SolarApp.Services.Performance;

namespace SolarApp.Pages.Performance;

[Authorize]
public class IndexModel : PageModel
{
    private readonly IPerformanceMetricsService _metrics;

    public IndexModel(IPerformanceMetricsService metrics)
    {
        _metrics = metrics;
    }

    public FleetPerformance Fleet { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public DateTime From { get; set; } = DateTime.UtcNow.Date.AddDays(-29);

    [BindProperty(SupportsGet = true)]
    public DateTime To { get; set; } = DateTime.UtcNow.Date;

    public async Task<IActionResult> OnGetAsync()
    {
        if (To < From) (From, To) = (To, From);
        if ((To - From).TotalDays > 365) From = To.AddDays(-365);

        Fleet = await _metrics.GetFleetPerformanceAsync(User, From, To);
        return Page();
    }
}
