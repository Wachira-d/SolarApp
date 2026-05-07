using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Pages.WorkOrders;

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

    public List<WorkOrder> Orders { get; set; } = new();

    [BindProperty(SupportsGet = true)] public WorkOrderStatus? StatusFilter { get; set; }
    [BindProperty(SupportsGet = true)] public WorkOrderType? TypeFilter { get; set; }
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }

    public int TotalCount { get; set; }
    public int OverdueCount { get; set; }
    public int TodayCount { get; set; }
    public int ThisWeekCount { get; set; }

    public string? TableError { get; set; }

    public async Task OnGetAsync()
    {
        // Guard: table might not exist yet if schema upgrade hasn't run
        try
        {
            #pragma warning disable EF1002
            await _context.Database.ExecuteSqlRawAsync("SELECT TOP 0 * FROM [WorkOrders]");
            #pragma warning restore EF1002
        }
        catch
        {
            TableError = "WorkOrders table does not exist yet. Please restart the IIS Application Pool " +
                "(or the app process) so the schema upgrade can create it. " +
                "If the problem persists, check Admin → Startup Logs for errors.";
            return;
        }

        var user = await _userManager.GetUserAsync(User);
        var roles = user != null ? await _userManager.GetRolesAsync(user) : new List<string>();

        var q = _context.WorkOrders
            .Include(w => w.Installation)
            .Include(w => w.AssignedToUser)
            .Include(w => w.Customer)
            .AsQueryable();

        // Role-based filter
        if (!roles.Contains(UserRoles.Admin))
        {
            if (roles.Contains(UserRoles.Partner))
                q = q.Where(w => (w.Installation != null && w.Installation.PartnerId == user!.PartnerId)
                              || w.CreatedByUserId == user!.Id
                              || w.AssignedToUserId == user!.Id);
            else if (roles.Contains(UserRoles.Customer))
            {
                // Customer sees WOs linked to their installations OR where they're the customer
                var custId = await _context.Customers
                    .Where(c => c.UserId == user!.Id)
                    .Select(c => c.Id)
                    .FirstOrDefaultAsync();
                q = q.Where(w =>
                    (w.Installation != null && w.Installation.CustomerId == custId)
                    || w.CustomerId == custId
                    || w.CreatedByUserId == user!.Id);
            }
            else
                q = q.Where(w => w.CreatedByUserId == user!.Id || w.AssignedToUserId == user!.Id);
        }

        if (StatusFilter.HasValue) q = q.Where(w => w.Status == StatusFilter);
        if (TypeFilter.HasValue) q = q.Where(w => w.Type == TypeFilter);
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var s = Search.Trim().ToLower();
            q = q.Where(w => w.Title.ToLower().Contains(s)
                          || w.WorkOrderNumber.ToLower().Contains(s)
                          || (w.ExternalSiteName ?? "").ToLower().Contains(s));
        }

        var now = DateTime.UtcNow.Date;
        var weekEnd = now.AddDays(7);

        TotalCount = await q.CountAsync();
        OverdueCount = await q.CountAsync(w => w.Status < WorkOrderStatus.Completed && w.ScheduledDate.Date < now);
        TodayCount = await q.CountAsync(w => w.ScheduledDate.Date == now);
        ThisWeekCount = await q.CountAsync(w => w.ScheduledDate.Date >= now && w.ScheduledDate.Date <= weekEnd);

        Orders = await q.OrderByDescending(w => w.ScheduledDate).Take(100).ToListAsync();
    }
}
