using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Services.InverterApi.Common;

namespace SolarApp.Pages.Installations;

[Authorize(Roles = "Admin,Partner")]
public class DiagnosticsModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly IAdapterDiagnostics _diagnostics;

    public DiagnosticsModel(ApplicationDbContext context, IAdapterDiagnostics diagnostics)
    {
        _context = context;
        _diagnostics = diagnostics;
    }

    public Installation Installation { get; set; } = null!;
    public DiagnosticReport? Report { get; set; }
    public List<InverterApiLog> RecentLogs { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id, bool run = false)
    {
        var inst = await _context.Installations
            .Include(i => i.Customer).Include(i => i.Partner)
            .FirstOrDefaultAsync(i => i.Id == id);
        if (inst == null) return NotFound();

        Installation = inst;

        if (run)
        {
            Report = await _diagnostics.RunAsync(inst);
        }

        // Last 20 API call logs
        RecentLogs = await _context.InverterApiLogs
            .Where(l => l.InstallationId == id)
            .OrderByDescending(l => l.RequestTime)
            .Take(20)
            .ToListAsync();

        return Page();
    }
}
