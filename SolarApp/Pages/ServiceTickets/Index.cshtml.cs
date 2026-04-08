using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Pages.ServiceTickets;

[Authorize]
public class IndexModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;

    public IndexModel(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    public List<ServiceTicket> Tickets { get; set; } = new();

    public async Task OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        var roles = await _userManager.GetRolesAsync(user!);

        var query = _context.ServiceTickets
            .Include(t => t.Installation)
            .Include(t => t.ReportedByUser)
            .Include(t => t.AssignedToUser)
            .AsQueryable();

        if (roles.Contains(UserRoles.Customer))
        {
            query = query.Where(t => t.ReportedByUserId == user!.Id
                || t.Installation.CustomerId == user!.Id);
        }
        else if (roles.Contains(UserRoles.Partner))
        {
            query = query.Where(t => t.Installation.PartnerId == user!.Id);
        }

        Tickets = await query
            .OrderByDescending(t => t.CreatedAt)
            .Take(50)
            .ToListAsync();
    }
}
