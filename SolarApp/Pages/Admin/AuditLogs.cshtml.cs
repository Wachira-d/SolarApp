using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;

namespace SolarApp.Pages.Admin;

[Authorize(Roles = "Admin")]
public class AuditLogsModel : PageModel
{
    private readonly ApplicationDbContext _context;

    public AuditLogsModel(ApplicationDbContext context)
    {
        _context = context;
    }

    public List<AuditLog> Logs { get; set; } = new();

    public async Task OnGetAsync()
    {
        Logs = await _context.AuditLogs
            .OrderByDescending(a => a.Timestamp)
            .Take(100)
            .ToListAsync();
    }
}
