using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.Authorization;

namespace SolarApp.Pages.WorkOrders;

[Authorize]
public class IndexModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IWorkOrderAccessHelper _access;

    public IndexModel(
        ApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        IWorkOrderAccessHelper access)
    {
        _context = context;
        _userManager = userManager;
        _access = access;
    }

    public List<WorkOrder> Orders { get; set; } = new();

    [BindProperty(SupportsGet = true)] public WorkOrderStatus? StatusFilter { get; set; }
    [BindProperty(SupportsGet = true)] public WorkOrderType? TypeFilter { get; set; }
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true), DataType(DataType.Date)] public DateTime? FromDate { get; set; }
    [BindProperty(SupportsGet = true), DataType(DataType.Date)] public DateTime? ToDate { get; set; }

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

        var baseQ = _context.WorkOrders
            .Include(w => w.Installation)
            .Include(w => w.AssignedToUser)
            .Include(w => w.Customer)
            .AsQueryable();

        var q = await _access.ApplyVisibilityFilterAsync(baseQ, User);

        if (StatusFilter.HasValue) q = q.Where(w => w.Status == StatusFilter);
        if (TypeFilter.HasValue) q = q.Where(w => w.Type == TypeFilter);
        if (FromDate.HasValue)
        {
            var from = FromDate.Value.Date;
            q = q.Where(w => w.ScheduledDate >= from);
        }
        if (ToDate.HasValue)
        {
            var to = ToDate.Value.Date.AddDays(1);
            q = q.Where(w => w.ScheduledDate < to);
        }
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

        Orders = await q.OrderByDescending(w => w.ScheduledDate).Take(200).ToListAsync();
    }
}
