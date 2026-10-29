using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Pages.Jobs;

/// <summary>
/// One list of everything the field crew has to do.
///
/// Field work lives in three tables that grew separately — WorkOrders,
/// MaintenanceRecords and CleaningRecords — each with its own page, its own
/// status enum and its own idea of what "done" means. Asking "what is on for
/// this week" meant opening three screens and merging them by eye, and the
/// same visit sometimes got written up twice because two of the screens
/// claimed it.
///
/// This does not merge the tables. Collapsing three schemas with live history
/// into one is a migration that has to be rehearsed against real data, and
/// getting it wrong destroys the record an O&amp;M contract is settled on. What
/// it does is remove the reason the operator had to care: one chronological
/// list, one set of filters, each row linking back to wherever it actually
/// lives. The schema merge stays possible later; the daily confusion doesn't
/// have to wait for it.
/// </summary>
[Authorize(Roles = "Admin,Partner")]
public class IndexModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;

    public IndexModel(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    public record JobRow(
        string Source,          // "ใบสั่งงาน" / "บำรุงรักษา" / "ล้างแผง"
        string SourceKey,       // "workorder" / "maintenance" / "cleaning" — for filtering
        string Icon,
        int Id,
        int? InstallationId,
        string SiteName,
        string? CustomerName,
        string Title,
        DateTime ScheduledDate,
        DateTime? CompletedDate,
        string StatusLabel,
        string StatusClass,
        bool IsOpen,
        string? AssignedTo,
        string Url)
    {
        /// <summary>Open and past its date. The reason this page is read at all.</summary>
        public bool IsOverdue => IsOpen && ScheduledDate.Date < SolarApp.Services.SiteTime.Today;
    }

    public List<JobRow> Jobs { get; private set; } = new();

    /// <summary>
    /// Sites whose cleaning is due, by the shared rule (Installation.CleaningDueAsOf).
    /// This used to live on its own "การบำรุงรักษา" page, so the one thing the
    /// crew most often has to do next was not on the page listing their work.
    /// </summary>
    public List<Installation> CleaningDue { get; private set; } = new();

    /// <summary>Open service tickets with no work order yet — complaints waiting for a visit.</summary>
    public List<ServiceTicket> UnansweredTickets { get; private set; } = new();
    public bool CanCreate { get; private set; }

    [BindProperty(SupportsGet = true)] public string? Source { get; set; }
    /// <summary>"open" (default), "overdue", "done", or "all".</summary>
    [BindProperty(SupportsGet = true)] public string Show { get; set; } = "open";
    [BindProperty(SupportsGet = true)] public DateTime? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? To { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }

    public int OverdueCount { get; private set; }
    public int DueThisWeekCount { get; private set; }
    public int OpenCount { get; private set; }

    public async Task OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        var roles = user != null ? await _userManager.GetRolesAsync(user) : new List<string>();

        // Default window: a fortnight back for things left undone, a month
        // ahead for planning. Loading all history by default would make the
        // page slow and the useful rows hard to find.
        var from = (From ?? SolarApp.Services.SiteTime.Today.AddDays(-14)).Date;
        var to = (To ?? SolarApp.Services.SiteTime.Today.AddDays(30)).Date;
        if (to < from) (from, to) = (to, from);
        var toExclusive = to.AddDays(1);
        // The default window starts 14 days back, which hid any job still open
        // from before then — the most overdue work vanished from the list and
        // from the overdue count. Unless the operator picks a start date,
        // still-open jobs are shown however old they are.
        var includeOlderOpen = From == null;

        var isAdmin = roles.Contains(UserRoles.Admin);

        var sites = _context.Installations.AsNoTracking();
        if (!isAdmin)
        {
            if (roles.Contains(UserRoles.Partner) && user!.PartnerId.HasValue)
                sites = sites.Where(i => i.PartnerId == user.PartnerId);
            else if (roles.Contains(UserRoles.Customer))
                sites = sites.Where(i => i.Customer.UserId == user!.Id);
            else
                sites = sites.Where(i => false);
        }

        var siteIds = await sites.Select(i => i.Id).ToListAsync();
        var rows = new List<JobRow>();
        CanCreate = isAdmin || roles.Contains(UserRoles.Partner);

        CleaningDue = await sites
            .Include(i => i.Customer)
            .Where(Installation.CleaningDueAsOf(SolarApp.Services.SiteTime.Today))
            .OrderBy(i => i.NextCleaningDueDate)
            .ToListAsync();

        if (CanCreate)
        {
            var answered = _context.WorkOrders
                .Where(w => w.ServiceTicketId != null && w.Status != WorkOrderStatus.Cancelled)
                .Select(w => w.ServiceTicketId!.Value);
            UnansweredTickets = await _context.ServiceTickets.AsNoTracking()
                .Include(t => t.Installation)
                .Where(t => siteIds.Contains(t.InstallationId)
                            && (t.Status == TicketStatus.Open || t.Status == TicketStatus.Assigned)
                            && !answered.Contains(t.Id))
                .OrderBy(t => t.Priority).ThenBy(t => t.CreatedAt)
                .Take(20)
                .ToListAsync();
        }

        // ── Work orders ──
        var workOrders = await _context.WorkOrders.AsNoTracking()
            .Include(w => w.Installation).ThenInclude(i => i!.Customer)
            .Include(w => w.AssignedToUser)
            .Where(w => (w.ScheduledDate >= from
                         || (includeOlderOpen && w.Status < WorkOrderStatus.Completed))
                        && w.ScheduledDate < toExclusive)
            .Where(w => (w.InstallationId != null && siteIds.Contains(w.InstallationId.Value))
                        // Standalone jobs have no site to scope by, so only an
                        // admin sees them.
                        || (w.InstallationId == null && isAdmin))
            .ToListAsync();

        rows.AddRange(workOrders.Select(w => new JobRow(
            "ใบสั่งงาน", "workorder", "clipboard2-check",
            w.Id, w.InstallationId, w.SiteName,
            w.Installation?.Customer?.FullName ?? w.ExternalCustomerName,
            string.IsNullOrWhiteSpace(w.Title) ? UiLabels.Label(w.Type) : w.Title,
            w.ScheduledDate, w.ActualEndTime,
            UiLabels.Label(w.Status), UiLabels.Badge(w.Status),
            w.Status < WorkOrderStatus.Completed && w.Status != WorkOrderStatus.Cancelled,
            w.AssignedToUser?.FullName ?? w.AssignedTeam,
            $"/WorkOrders/Detail/{w.Id}")));

        // ── Maintenance records ──
        var maintenance = await _context.MaintenanceRecords.AsNoTracking()
            .Include(m => m.Installation).ThenInclude(i => i.Customer)
            .Include(m => m.PerformedByUser)
            .Where(m => siteIds.Contains(m.InstallationId))
            .Where(m => (m.ScheduledDate >= from
                         || (includeOlderOpen && m.Status != MaintenanceStatus.Completed
                             && m.Status != MaintenanceStatus.Cancelled))
                        && m.ScheduledDate < toExclusive)
            .ToListAsync();

        rows.AddRange(maintenance.Select(m => new JobRow(
            "บำรุงรักษา", "maintenance", "tools",
            m.Id, m.InstallationId, m.Installation.SiteName,
            m.Installation.Customer?.FullName,
            string.IsNullOrWhiteSpace(m.Description) ? UiLabels.Label(m.MaintenanceType) : m.Description,
            m.ScheduledDate, m.CompletedDate,
            UiLabels.Label(m.Status), UiLabels.Badge(m.Status),
            m.Status != MaintenanceStatus.Completed && m.Status != MaintenanceStatus.Cancelled,
            m.PerformedByUser?.FullName,
            $"/Maintenance/Edit/{m.Id}")));

        // ── Cleaning records ──
        var cleaning = await _context.CleaningRecords.AsNoTracking()
            .Include(c => c.Installation).ThenInclude(i => i.Customer)
            .Include(c => c.PerformedByUser)
            .Where(c => siteIds.Contains(c.InstallationId))
            .Where(c => (c.ScheduledDate >= from
                         || (includeOlderOpen && c.Status == CleaningStatus.Scheduled))
                        && c.ScheduledDate < toExclusive)
            .ToListAsync();

        rows.AddRange(cleaning.Select(c => new JobRow(
            "ล้างแผง", "cleaning", "droplet",
            c.Id, c.InstallationId, c.Installation.SiteName,
            c.Installation.Customer?.FullName,
            "ล้างแผงโซลาร์",
            c.ScheduledDate, c.ActualDate,
            UiLabels.Label(c.Status), UiLabels.Badge(c.Status),
            c.Status == CleaningStatus.Scheduled,
            c.PerformedByUser?.FullName ?? c.PerformedByExternal,
            // Cleaning has no per-record page — it's edited on the site's
            // cleaning sheet, so that's where the row goes.
            $"/Installations/Cleaning/{c.InstallationId}")));

        // Counts are taken before the view filter, so the summary keeps telling
        // the truth while the operator narrows the list down.
        var weekEnd = SolarApp.Services.SiteTime.Today.AddDays(7);
        OverdueCount = rows.Count(r => r.IsOverdue);
        OpenCount = rows.Count(r => r.IsOpen);
        DueThisWeekCount = rows.Count(r => r.IsOpen
                                           && r.ScheduledDate.Date >= SolarApp.Services.SiteTime.Today
                                           && r.ScheduledDate.Date <= weekEnd);

        if (!string.IsNullOrWhiteSpace(Source))
            rows = rows.Where(r => r.SourceKey == Source).ToList();

        rows = Show switch
        {
            "overdue" => rows.Where(r => r.IsOverdue).ToList(),
            "today" => rows.Where(r => r.IsOpen && r.ScheduledDate.Date == SolarApp.Services.SiteTime.Today).ToList(),
            "week" => rows.Where(r => r.IsOpen && r.ScheduledDate.Date >= SolarApp.Services.SiteTime.Today
                                     && r.ScheduledDate.Date <= weekEnd).ToList(),
            "done" => rows.Where(r => !r.IsOpen).ToList(),
            "all" => rows,
            _ => rows.Where(r => r.IsOpen).ToList(),
        };

        if (!string.IsNullOrWhiteSpace(Q))
        {
            var q = Q.Trim();
            rows = rows.Where(r =>
                r.SiteName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (r.CustomerName ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // Overdue first, then by date. Anything already late is the reason
        // someone opened this page.
        Jobs = rows
            .OrderByDescending(r => r.IsOverdue)
            .ThenBy(r => r.ScheduledDate)
            .ThenBy(r => r.SiteName)
            .ToList();
    }
}
