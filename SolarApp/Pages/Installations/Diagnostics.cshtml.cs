using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Services.Authorization;
using SolarApp.Services.InverterApi.Common;

namespace SolarApp.Pages.Installations;

[Authorize(Roles = "Admin,Partner")]
public class DiagnosticsModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly IAdapterDiagnostics _diagnostics;
    private readonly IInstallationAccessHelper _access;
    private readonly IConnectionHealthChecker _healthChecker;

    public DiagnosticsModel(ApplicationDbContext context, IAdapterDiagnostics diagnostics,
        IInstallationAccessHelper access, IConnectionHealthChecker healthChecker)
    {
        _context = context;
        _diagnostics = diagnostics;
        _access = access;
        _healthChecker = healthChecker;
    }

    public Installation Installation { get; set; } = null!;
    public DiagnosticReport? Report { get; set; }
    public ConnectionHealthReport? HealthReport { get; set; }
    public List<InverterApiLog> RecentLogs { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id, bool run = false, bool deep = false)
    {
        if (!await _access.CanAccessAsync(id, User)) return Forbid();

        var inst = await _context.Installations
            .Include(i => i.Customer).Include(i => i.Partner)
            .FirstOrDefaultAsync(i => i.Id == id);
        if (inst == null) return NotFound();

        Installation = inst;

        if (deep)
        {
            HealthReport = await _healthChecker.RunFullCheckAsync(inst);
        }
        else if (run)
        {
            Report = await _diagnostics.RunAsync(inst);
        }

        RecentLogs = await _context.InverterApiLogs
            .Where(l => l.InstallationId == id)
            .OrderByDescending(l => l.RequestTime)
            .Take(20)
            .ToListAsync();

        return Page();
    }
}
