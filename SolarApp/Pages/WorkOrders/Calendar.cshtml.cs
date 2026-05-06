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
        var orders = await _context.WorkOrders
            .Where(w => w.Status != WorkOrderStatus.Cancelled)
            .Select(w => new
            {
                id = w.Id,
                title = w.WorkOrderNumber + " " + w.Title,
                start = w.ScheduledDate.ToString("yyyy-MM-dd"),
                color = w.Status switch
                {
                    WorkOrderStatus.Completed or WorkOrderStatus.Verified or WorkOrderStatus.Closed => "#198754",
                    WorkOrderStatus.InProgress => "#ffc107",
                    WorkOrderStatus.Cancelled => "#dc3545",
                    _ => w.ScheduledDate.Date < DateTime.UtcNow.Date ? "#dc3545" : "#0d6efd"
                },
                url = $"/WorkOrders/Detail/{w.Id}",
                extendedProps = new { type = w.Type.ToString(), status = w.Status.ToString(), site = w.ExternalSiteName ?? "" }
            })
            .ToListAsync();

        EventsJson = System.Text.Json.JsonSerializer.Serialize(orders);
    }
}
