using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.Interfaces;
using SolarApp.Services.LineApi;
using SolarApp.Services.Notifications;

namespace SolarApp.Pages.Installations;

[Authorize]
public class DetailModel : PageModel
{
    private readonly IInstallationService _installationService;
    private readonly IMaintenanceService _maintenanceService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ApplicationDbContext _context;
    private readonly ILineMessagingService _lineService;
    private readonly INotificationRouteService _routes;
    private readonly ILogger<DetailModel> _logger;

    public DetailModel(
        IInstallationService installationService,
        IMaintenanceService maintenanceService,
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext context,
        ILineMessagingService lineService,
        INotificationRouteService routes,
        ILogger<DetailModel> logger)
    {
        _installationService = installationService;
        _maintenanceService = maintenanceService;
        _userManager = userManager;
        _context = context;
        _lineService = lineService;
        _routes = routes;
        _logger = logger;
    }

    public Installation Installation { get; set; } = null!;
    public List<MaintenanceRecord> MaintenanceRecords { get; set; } = new();
    public List<ServiceTicket> ServiceTickets { get; set; } = new();
    /// <summary>Work orders on this site, open ones first. The field-work card and history read these.</summary>
    public List<WorkOrder> WorkOrders { get; set; } = new();
    /// <summary>Open "Station Faulty" alerts driving the Maintenance badge.</summary>
    public int OpenStationFaultCount { get; set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var installation = await _installationService.GetByIdAsync(id);
        if (installation == null) return NotFound();

        var user = await _userManager.GetUserAsync(User);
        var roles = await _userManager.GetRolesAsync(user!);

        if (!roles.Contains(UserRoles.Admin))
        {
            if (roles.Contains(UserRoles.Partner))
            {
                if (user!.PartnerId == null || installation.PartnerId != user.PartnerId)
                    return Forbid();
            }
            else if (roles.Contains(UserRoles.Customer))
            {
                var customer = await _context.Customers.FirstOrDefaultAsync(c => c.UserId == user!.Id);
                if (customer == null || installation.CustomerId != customer.Id)
                    return Forbid();
            }
        }

        Installation = installation;
        MaintenanceRecords = await _maintenanceService.GetByInstallationAsync(id);
        ServiceTickets = await _maintenanceService.GetTicketsByInstallationAsync(id);
        WorkOrders = await _context.WorkOrders.AsNoTracking()
            .Include(w => w.AssignedToUser)
            .Where(w => w.InstallationId == id)
            .OrderBy(w => w.Status >= WorkOrderStatus.Completed)   // open first
            .ThenByDescending(w => w.ScheduledDate)
            .Take(30)
            .ToListAsync();

        // Count unresolved "Station Faulty" alerts so the view can decide
        // whether to surface the "Force Recover" button.  Same predicate
        // as SolarDataSyncJob's auto-resolver — keep them in sync.
        OpenStationFaultCount = await _context.SystemAlerts
            .CountAsync(a => a.InstallationId == id
                && a.ResolvedAt == null
                && (a.AlertType == AlertType.Error || a.AlertType == AlertType.Offline)
                && a.Title.StartsWith("Station Faulty:"));

        return Page();
    }

    /// <summary>
    /// Admin/Partner manual recovery.  Used when the FusionSolar (or
    /// other vendor) app shows the site is back to normal but our cloud
    /// poll is still seeing stale real_health_state=1 — usually a delay
    /// of an hour or so between the device clearing the fault and the
    /// vendor cloud reflecting it.
    ///
    /// Resolves every open "Station Faulty" alert, flips Status back to
    /// Active, and fires the recovery LINE message right away (operator
    /// + customer if NotifyOnRecovery is on).  Audited so we can answer
    /// "who manually recovered site X?" later.
    /// </summary>
    public async Task<IActionResult> OnPostForceRecoverAsync(int id)
    {
        var installation = await _context.Installations
            .Include(i => i.Customer)
            .Include(i => i.Partner)
            .FirstOrDefaultAsync(i => i.Id == id);
        if (installation == null) return NotFound();

        var user = await _userManager.GetUserAsync(User);
        var roles = user != null ? await _userManager.GetRolesAsync(user) : new List<string>();
        var isAdmin = roles.Contains(UserRoles.Admin);
        var isMatchingPartner = roles.Contains(UserRoles.Partner)
            && user!.PartnerId.HasValue
            && installation.PartnerId == user.PartnerId;
        if (!isAdmin && !isMatchingPartner) return Forbid();

        var now = DateTime.UtcNow;
        var openFaults = await _context.SystemAlerts
            .Where(a => a.InstallationId == id
                && a.ResolvedAt == null
                && (a.AlertType == AlertType.Error || a.AlertType == AlertType.Offline)
                && a.Title.StartsWith("Station Faulty:"))
            .ToListAsync();

        if (openFaults.Count == 0 && installation.Status != InstallationStatus.Maintenance)
        {
            TempData["FlashWarning"] = "ไม่มี alert ค้าง และสถานะไม่ใช่ Maintenance — ไม่ต้อง force recover.";
            return RedirectToPage(new { id });
        }

        foreach (var f in openFaults) f.ResolvedAt = now;
        if (installation.Status == InstallationStatus.Maintenance)
            installation.Status = InstallationStatus.Active;
        installation.UpdatedAt = now;

        // Fire LINE recovery for each alert we just resolved — bypasses
        // the 24-hour-window catcher in SystemHealthMonitorJob so the
        // operator who clicks the button gets immediate feedback that
        // the message actually went out.
        var alertGroupId = await _context.SystemSettings
            .Where(s => s.Key == "Line.AlertGroupId")
            .Select(s => s.Value)
            .FirstOrDefaultAsync();
        int sent = 0;
        foreach (var alert in openFaults)
        {
            var downtime = now - alert.OccurredAt;
            var downtimeStr = downtime.TotalHours >= 1
                ? $"{downtime.TotalHours:0.0} ชั่วโมง"
                : $"{downtime.TotalMinutes:0} นาที";
            var techMsg = $"{installation.SiteName} กลับมาทำงานปกติแล้ว หลังจาก offline {downtimeStr} " +
                          $"(manual recovery by {user?.UserName})";
            if (!string.IsNullOrWhiteSpace(alertGroupId))
            {
                try
                {
                    await _lineService.SendRecoveryNotificationAsync(
                        alertGroupId, installation.SiteName, alert.Title, techMsg, downtime);
                    sent++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Force-recover LINE to alert group failed for alert {Id}", alert.Id);
                }
            }
            var customerLineId = CustomerAlertMessageBuilder.PickRecipient(installation);
            var customerAllowed = CustomerAlertMessageBuilder.ShouldNotifyCustomer(installation, alert)
                                  && installation.NotifyOnRecovery;
            if (!string.IsNullOrWhiteSpace(customerLineId) && customerAllowed)
            {
                try
                {
                    var customerMsg = CustomerAlertMessageBuilder.BuildRecovery(
                        alert, installation, new List<string>());
                    await _lineService.SendMessageAsync(customerLineId, customerMsg);
                    sent++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Force-recover customer LINE failed for alert {Id}", alert.Id);
                }
            }

            // Operator-configured recovery routes (extra recipients).
            try
            {
                var routeSent = await _routes.SendEventAsync(
                    Models.Enums.NotificationEventType.Recovery,
                    id,
                    $"[RECOVERED] {alert.Title}",
                    $"✅ {installation.SiteName} กลับมาทำงานปกติแล้ว (manual recovery)",
                    string.IsNullOrWhiteSpace(alertGroupId) ? null : new[] { alertGroupId! });
                sent += routeSent;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Force-recover route fan-out failed for alert {Id}", alert.Id);
            }
        }

        _context.AuditLogs.Add(new AuditLog
        {
            UserId = user?.Id,
            UserName = user?.UserName,
            Action = "ForceRecoverInstallation",
            EntityType = nameof(Installation),
            EntityId = id.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                resolvedAlertIds = openFaults.Select(a => a.Id).ToArray(),
                lineMessagesSent = sent,
                statusBefore = "Maintenance",
                statusAfter = "Active",
            }),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
        });
        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Installation {Id} force-recovered by {UserId}: resolved {Count} alert(s), sent {Sent} LINE messages",
            id, user?.Id, openFaults.Count, sent);
        TempData["FlashSuccess"] =
            $"ปลดล็อก {openFaults.Count} alert และส่ง LINE recovery แล้ว ({sent} ข้อความ).";
        return RedirectToPage(new { id });
    }
}
