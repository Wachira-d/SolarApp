using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models.Enums;
using SolarApp.Services.Authorization;

namespace SolarApp.Pages.WorkOrders;

[Authorize]
public class CalendarModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly IWorkOrderAccessHelper _access;

    public CalendarModel(ApplicationDbContext context, IWorkOrderAccessHelper access)
    {
        _context = context;
        _access = access;
    }

    public string EventsJson { get; set; } = "[]";

    public async Task OnGetAsync()
    {
        var baseQuery = _context.WorkOrders
            .Include(w => w.Installation)
            .Where(w => w.Status != WorkOrderStatus.Cancelled);

        var visible = await _access.ApplyVisibilityFilterAsync(baseQuery, User);

        var raw = await visible
            .Select(w => new
            {
                w.Id,
                w.WorkOrderNumber,
                w.Title,
                w.ScheduledDate,
                w.Status,
                w.Type,
                Site = w.ExternalSiteName ?? ""
            })
            .ToListAsync();

        var now = SolarApp.Services.SiteTime.Today;
        var events = raw.Select(w => new
        {
            id = w.Id,
            title = w.WorkOrderNumber + " " + w.Title,
            start = w.ScheduledDate.ToString("yyyy-MM-dd"),
            color = w.Status switch
            {
                WorkOrderStatus.Completed or WorkOrderStatus.Verified or WorkOrderStatus.Closed => "#198754",
                WorkOrderStatus.InProgress => "#ffc107",
                _ => w.ScheduledDate.Date < now ? "#dc3545" : "#0d6efd"
            },
            url = $"/WorkOrders/Detail/{w.Id}"
        });

        EventsJson = System.Text.Json.JsonSerializer.Serialize(events);
    }
}
