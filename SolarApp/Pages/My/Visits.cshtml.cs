using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;

namespace SolarApp.Pages.My;

/// <summary>
/// Service-visit history — customer can see every cleaning + maintenance
/// visit performed on their installations, with photos and notes.
/// Single timeline view across all of the customer's sites.
/// </summary>
[Authorize]
public class VisitsModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;

    public VisitsModel(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    public List<VisitVm> Upcoming { get; private set; } = new();
    public List<VisitVm> Recent { get; private set; } = new();

    public class VisitVm
    {
        public DateTime When { get; set; }
        public string SiteName { get; set; } = "";
        public int SiteId { get; set; }
        public string Kind { get; set; } = "";          // "ล้างแผง" / "บำรุงรักษา"
        public string Status { get; set; } = "";
        public string? Performer { get; set; }
        public string? Notes { get; set; }
        public List<string> BeforePhotos { get; set; } = new();
        public List<string> AfterPhotos { get; set; } = new();
        public bool IsScheduled { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Forbid();

        var customerId = await _context.Customers
            .Where(c => c.UserId == user.Id)
            .Select(c => (int?)c.Id)
            .FirstOrDefaultAsync();
        if (customerId == null) return Page();

        var now = DateTime.UtcNow;
        var oneYearAgo = now.AddDays(-365);

        var cleanings = await _context.CleaningRecords
            .Include(c => c.Installation)
            .Include(c => c.PerformedByUser)
            .Where(c => c.Installation.CustomerId == customerId
                && (c.ScheduledDate >= oneYearAgo || c.ActualDate >= oneYearAgo))
            .OrderByDescending(c => c.ActualDate ?? c.ScheduledDate)
            .Take(50)
            .ToListAsync();

        var maintenance = await _context.MaintenanceRecords
            .Include(m => m.Installation)
            .Include(m => m.PerformedByUser)
            .Where(m => m.Installation.CustomerId == customerId
                && (m.ScheduledDate >= oneYearAgo || m.CompletedDate >= oneYearAgo))
            .OrderByDescending(m => m.CompletedDate ?? m.ScheduledDate)
            .Take(50)
            .ToListAsync();

        // Work orders are where visits are recorded now; without them the
        // customer saw cleaning and nothing else.
        var orders = await _context.WorkOrders
            .Include(w => w.Installation)
            .Include(w => w.AssignedToUser)
            .Where(w => w.InstallationId != null && w.Installation!.CustomerId == customerId
                && w.Status != Models.Enums.WorkOrderStatus.Cancelled
                && w.Status != Models.Enums.WorkOrderStatus.Draft
                // A panel-cleaning order also writes a cleaning record, which is listed above.
                && w.Type != Models.Enums.WorkOrderType.PanelCleaning
                && (w.ScheduledDate >= oneYearAgo || w.ActualEndTime >= oneYearAgo))
            .OrderByDescending(w => w.ActualEndTime ?? w.ScheduledDate)
            .Take(50)
            .ToListAsync();

        var all = new List<VisitVm>();

        foreach (var w in orders)
        {
            var done = w.Status >= Models.Enums.WorkOrderStatus.Completed;
            var when = done && w.ActualEndTime.HasValue
                ? Services.SiteTime.ToSiteTime(w.ActualEndTime.Value).Date
                : w.ScheduledDate.Date;
            var icon = w.Type switch
            {
                Models.Enums.WorkOrderType.PreventiveMaintenance => "🔧",
                Models.Enums.WorkOrderType.CorrectiveRepair => "🛠",
                Models.Enums.WorkOrderType.Inspection => "🔍",
                Models.Enums.WorkOrderType.NewInstallation => "📦",
                Models.Enums.WorkOrderType.Commissioning => "⚡",
                _ => "🧰"
            };
            all.Add(new VisitVm
            {
                When = when,
                SiteName = w.Installation!.SiteName,
                SiteId = w.Installation.Id,
                Kind = $"{icon} {Models.Enums.UiLabels.Label(w.Type)}",
                Status = done ? "Completed" : w.Status == Models.Enums.WorkOrderStatus.InProgress ? "InProgress" : "Scheduled",
                Performer = w.AssignedToUser?.FullName ?? w.AssignedTeam,
                // Customers see what was done, not internal planning notes.
                Notes = done ? w.CompletionNotes : w.Title,
                BeforePhotos = SplitPhotos(w.BeforePhotos, null),
                AfterPhotos = SplitPhotos(w.AfterPhotos, null),
                IsScheduled = !done && when >= now.Date,
            });
        }

        foreach (var c in cleanings)
        {
            var when = c.ActualDate ?? c.ScheduledDate;
            all.Add(new VisitVm
            {
                When = when,
                SiteName = c.Installation.SiteName,
                SiteId = c.Installation.Id,
                Kind = "💧 ล้างแผงโซลาร์",
                Status = c.Status.ToString(),
                Performer = c.PerformedByUser?.FullName ?? c.PerformedByExternal,
                Notes = c.Notes,
                BeforePhotos = SplitPhotos(c.BeforePhotos, c.BeforePhotoUrl),
                AfterPhotos = SplitPhotos(c.AfterPhotos, c.AfterPhotoUrl),
                IsScheduled = c.ActualDate == null && when >= now,
            });
        }

        foreach (var m in maintenance)
        {
            var when = m.CompletedDate ?? m.ScheduledDate;
            var kindIcon = m.MaintenanceType switch
            {
                Models.Enums.MaintenanceType.PanelCleaning      => "💧 ล้างแผงโซลาร์",
                Models.Enums.MaintenanceType.Inspection         => "🔍 ตรวจสภาพ",
                Models.Enums.MaintenanceType.Repair             => "🛠 ซ่อมแซม (CM)",
                Models.Enums.MaintenanceType.Replacement        => "🔁 เปลี่ยนอุปกรณ์",
                Models.Enums.MaintenanceType.GeneralMaintenance => "🔧 บำรุงรักษาทั่วไป (PM)",
                _                                               => "🔧 บำรุงรักษา",
            };
            all.Add(new VisitVm
            {
                When = when,
                SiteName = m.Installation.SiteName,
                SiteId = m.Installation.Id,
                Kind = kindIcon,
                Status = m.Status.ToString(),
                Performer = m.PerformedByUser?.FullName,
                Notes = m.Notes ?? m.Description,
                BeforePhotos = SplitPhotos(m.BeforePhotos, null),
                AfterPhotos = SplitPhotos(m.AfterPhotos, null),
                IsScheduled = m.CompletedDate == null && when >= now,
            });
        }

        Upcoming = all.Where(v => v.IsScheduled)
            .OrderBy(v => v.When)
            .Take(10)
            .ToList();
        Recent = all.Where(v => !v.IsScheduled)
            .OrderByDescending(v => v.When)
            .Take(20)
            .ToList();

        return Page();
    }

    private static List<string> SplitPhotos(string? csv, string? legacySingle)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(legacySingle)) list.Add(legacySingle);
        if (!string.IsNullOrWhiteSpace(csv))
        {
            foreach (var url in csv.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = url.Trim();
                if (!string.IsNullOrEmpty(trimmed)) list.Add(trimmed);
            }
        }
        return list;
    }
}
