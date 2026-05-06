using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models.Enums;

namespace SolarApp.Pages.WorkOrders;

[Authorize]
public class CalendarModel : PageModel
{
    private readonly ApplicationDbContext _context;
    public CalendarModel(ApplicationDbContext context) => _context = context;

    public string EventsJson { get; set; } = "[]";

    public async Task OnGetAsync()
    {
        var raw = await _context.WorkOrders
            .Where(w => w.Status != WorkOrderStatus.Cancelled)
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

        var now = DateTime.UtcNow.Date;
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
