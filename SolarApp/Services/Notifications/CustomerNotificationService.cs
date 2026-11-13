using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Models.Notifications;
using SolarApp.Services.LineApi;

namespace SolarApp.Services.Notifications;

/// <summary>
/// Which customer-facing events the notification center offers, what they
/// are called, and what each defaults to when the operator hasn't decided.
/// </summary>
public static class CustomerNotificationEvents
{
    /// <summary>
    /// Matrix rows, in the order a customer cares about them. DailySummary is
    /// the operator's report and All is a routing wildcard — neither is
    /// something a customer opts into. StationFaulty is folded into
    /// GridLossOffline: both hang off the one NotifyOnGridLoss switch, and to a
    /// customer "the system reported a fault" and "the system is down" are
    /// the same news.
    /// </summary>
    public static readonly NotificationEventType[] Matrix =
    {
        NotificationEventType.GridLossOffline,
        NotificationEventType.Recovery,
        NotificationEventType.LowProduction,
        NotificationEventType.ApiFailure,
        NotificationEventType.CleaningDue,
        NotificationEventType.WarrantyExpiry,
        NotificationEventType.PredictiveDiagnostic,
    };

    public static string Label(NotificationEventType evt) => evt switch
    {
        NotificationEventType.GridLossOffline => "ระบบดับ / ออฟไลน์",
        NotificationEventType.StationFaulty => "อุปกรณ์แจ้งขัดข้อง",
        NotificationEventType.Recovery => "กลับมาทำงานปกติ",
        NotificationEventType.LowProduction => "ผลิตไฟต่ำกว่าปกติ",
        NotificationEventType.ApiFailure => "API เชื่อมต่อคลาวด์ไม่ได้",
        NotificationEventType.CleaningDue => "ครบกำหนดล้างแผง",
        NotificationEventType.WarrantyExpiry => "ใกล้หมดประกัน",
        NotificationEventType.PredictiveDiagnostic => "วินิจฉัยล่วงหน้า",
        NotificationEventType.DailySummary => "สรุปรายวัน",
        _ => evt.ToString(),
    };

    /// <summary>
    /// The site's existing NotifyOn* switch for this event — the default when
    /// no preference row exists, so turning the notification center on
    /// changes nothing until an operator edits it.
    /// </summary>
    public static bool SiteDefault(Installation site, NotificationEventType evt) => evt switch
    {
        NotificationEventType.GridLossOffline => site.NotifyOnGridLoss,
        NotificationEventType.StationFaulty => site.NotifyOnGridLoss,
        NotificationEventType.Recovery => site.NotifyOnRecovery,
        NotificationEventType.LowProduction => site.NotifyOnLowProduction,
        NotificationEventType.ApiFailure => site.NotifyOnApiFailure,
        NotificationEventType.CleaningDue => site.NotifyOnCleaningDue,
        NotificationEventType.WarrantyExpiry => site.NotifyOnWarrantyExpiry,
        // Predictions were never sent to customers; keep it that way unless asked.
        _ => false,
    };

    /// <summary>
    /// Per channel: an explicit preference row wins; otherwise both channels
    /// follow the site flag. E-mail follows too because an e-mail recipient
    /// the operator just added should get what the customer already gets on
    /// LINE — not silently nothing until a second screen is visited.
    /// </summary>
    public static (bool Line, bool Email) Resolve(NotificationEventType evt,
        CustomerNotificationPreference? pref, Installation site)
    {
        if (pref != null) return (pref.LineEnabled, pref.EmailEnabled);
        var d = SiteDefault(site, evt);
        return (d, d);
    }

    /// <summary>Customer-level default for the matrix: on if any of their sites has it on.</summary>
    public static bool CustomerDefault(NotificationEventType evt, IEnumerable<Installation> sites) =>
        sites.Any(s => SiteDefault(s, evt));

    /// <summary>
    /// Writes a matrix LINE choice back to the site's NotifyOn* switch. The
    /// customer's primary LINE id (Customer.LineUserId / site LineGroupId) is
    /// still served by the original alert step, which reads only those
    /// switches; without this, turning an event off in the matrix would stop
    /// every recipient except the one the customer most likely means.
    /// Returns false for events with no site switch.
    /// </summary>
    public static bool ApplyLineToSiteFlag(Installation site, NotificationEventType evt, bool line)
    {
        switch (evt)
        {
            case NotificationEventType.GridLossOffline: site.NotifyOnGridLoss = line; return true;
            case NotificationEventType.Recovery: site.NotifyOnRecovery = line; return true;
            case NotificationEventType.LowProduction: site.NotifyOnLowProduction = line; return true;
            case NotificationEventType.ApiFailure: site.NotifyOnApiFailure = line; return true;
            case NotificationEventType.CleaningDue: site.NotifyOnCleaningDue = line; return true;
            case NotificationEventType.WarrantyExpiry: site.NotifyOnWarrantyExpiry = line; return true;
            default: return false;
        }
    }

    /// <summary>
    /// The event an alert belongs to, or null when it is never for customers
    /// (contract renewals are a sales conversation; plain warnings are noise).
    /// Differs from NotificationRouteService.FromAlertType in one place on
    /// purpose: API-failure alerts are their own event here, because the
    /// customer has a separate switch for them.
    /// </summary>
    public static NotificationEventType? EventFor(SystemAlert alert)
    {
        var title = alert.Title ?? "";
        if (title.StartsWith(AlertTitles.ContractExpiring)) return null;
        if (title.StartsWith(AlertTitles.ApiFailure)) return NotificationEventType.ApiFailure;
        return alert.AlertType switch
        {
            AlertType.Offline or AlertType.Error => NotificationEventType.GridLossOffline,
            AlertType.LowPerformance => NotificationEventType.LowProduction,
            AlertType.MaintenanceDue => NotificationEventType.CleaningDue,
            AlertType.WarrantyExpiring => NotificationEventType.WarrantyExpiry,
            AlertType.PredictiveDiagnostic => NotificationEventType.PredictiveDiagnostic,
            _ => null,
        };
    }
}

public interface ICustomerNotificationService
{
    /// <summary>
    /// Sends one alert (or, with <paramref name="evt"/> = Recovery, its
    /// all-clear) to every enabled notification-center recipient of the
    /// site's customer whose preference allows it. LINE ids in
    /// <paramref name="alreadyReachedLineIds"/> are skipped; when that is a
    /// mutable set, the ids reached here are added so later fan-out steps
    /// skip them too. Returns the number of successful sends.
    /// </summary>
    Task<int> DeliverAlertAsync(SystemAlert alert, Installation site, NotificationEventType evt,
        string lineText, IEnumerable<string> alreadyReachedLineIds);

    /// <summary>"ส่งทดสอบ" from the notification center.</summary>
    Task<(bool Success, string Message)> SendTestAsync(CustomerNotificationRecipient recipient);
}

public class CustomerNotificationService : ICustomerNotificationService
{
    /// <summary>NotificationLogs.RelatedEntityType for sends made here; RelatedEntityId is the alert id.</summary>
    public const string LogEntityType = "CustomerNotification";

    private readonly ApplicationDbContext _context;
    private readonly ILineMessagingService _line;
    private readonly ICustomerEmailSender _email;
    private readonly IConfiguration? _configuration;
    private readonly ILogger<CustomerNotificationService> _logger;
    private readonly TimeProvider _clock;

    public CustomerNotificationService(
        ApplicationDbContext context,
        ILineMessagingService line,
        ICustomerEmailSender email,
        ILogger<CustomerNotificationService> logger,
        IConfiguration? configuration = null,
        TimeProvider? clock = null)
    {
        _context = context;
        _line = line;
        _email = email;
        _logger = logger;
        _configuration = configuration;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<int> DeliverAlertAsync(SystemAlert alert, Installation site, NotificationEventType evt,
        string lineText, IEnumerable<string> alreadyReachedLineIds)
    {
        try
        {
            return await DeliverCoreAsync(alert, site, evt, lineText, alreadyReachedLineIds);
        }
        catch (Exception ex)
        {
            // Tables may not exist on a first boot before the schema stages
            // run; the notification center must never break the alert pipeline.
            _logger.LogWarning(ex, "Customer notification delivery failed for alert {Id}", alert.Id);
            return 0;
        }
    }

    private async Task<int> DeliverCoreAsync(SystemAlert alert, Installation site, NotificationEventType evt,
        string lineText, IEnumerable<string> alreadyReachedLineIds)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var isRecovery = evt == NotificationEventType.Recovery;

        // A recovery is only welcome after the down-message it closes; the
        // existing customer step gates it the same way.
        var downEvt = isRecovery ? CustomerNotificationEvents.EventFor(alert) : evt;
        if (downEvt == null) return 0;
        if (!isRecovery && alert.Title.StartsWith(AlertTitles.ContractExpiring)) return 0;

        // Quiet hours as on the existing customer LINE step: Critical bypasses,
        // and the short "all clear" is never held back.
        if (!isRecovery && CustomerAlertMessageBuilder.IsInCustomerQuietHours(site, now, alert.Severity))
            return 0;

        var recipients = await _context.CustomerNotificationRecipients
            .Where(r => r.CustomerId == site.CustomerId && r.IsEnabled
                        && (r.InstallationId == null || r.InstallationId == site.Id))
            .OrderBy(r => r.Id)
            .ToListAsync();
        if (recipients.Count == 0) return 0;

        var events = new[] { evt, downEvt.Value }.Distinct().ToList();
        var prefs = await _context.CustomerNotificationPreferences.AsNoTracking()
            .Where(p => p.CustomerId == site.CustomerId && events.Contains(p.EventType))
            .ToListAsync();
        var allowed = CustomerNotificationEvents.Resolve(evt, prefs.FirstOrDefault(p => p.EventType == evt), site);
        if (isRecovery)
        {
            var down = CustomerNotificationEvents.Resolve(downEvt.Value,
                prefs.FirstOrDefault(p => p.EventType == downEvt.Value), site);
            allowed = (allowed.Line && down.Line, allowed.Email && down.Email);
        }
        if (!allowed.Line && !allowed.Email) return 0;

        var logTitle = isRecovery ? $"[RECOVERED] {alert.Title}" : alert.Title;
        var alertKey = alert.Id.ToString();

        // One attempt per recipient per event. The recovery step re-examines
        // the same resolved alerts every cycle for a day, and a pending alert
        // whose other channels all failed is retried — neither may resend
        // to someone already sent to.
        var alreadyLogged = (await _context.NotificationLogs.AsNoTracking()
                .Where(n => n.RelatedEntityType == LogEntityType && n.RelatedEntityId == alertKey
                            && n.Title == logTitle)
                .Select(n => new { n.Channel, n.RecipientLineId })
                .ToListAsync())
            .Select(n => $"{(int)n.Channel}|{n.RecipientLineId}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var reached = alreadyReachedLineIds as ISet<string>;
        var skipLine = new HashSet<string>(
            alreadyReachedLineIds.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var seenEmail = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool? emailConfigured = null;
        string? emailHtml = null, emailSubject = null;

        var sent = 0;
        foreach (var r in recipients)
        {
            var address = r.Address?.Trim();
            if (string.IsNullOrEmpty(address)) continue;
            if (alreadyLogged.Contains($"{(int)r.Channel}|{address}")) continue;

            var log = new NotificationLog
            {
                // The only address column NotificationLog has; Channel says
                // whether it holds a LINE id or an e-mail address.
                RecipientLineId = address,
                Channel = r.Channel,
                Title = logTitle,
                Message = lineText,
                RelatedEntityType = LogEntityType,
                RelatedEntityId = alertKey,
                Status = NotificationStatus.Pending,
                CreatedAt = now,
            };

            if (r.Channel == NotificationChannel.Line)
            {
                if (!allowed.Line || !skipLine.Add(address)) continue;
                try
                {
                    await _line.SendMessageAsync(address, lineText);
                    log.Status = NotificationStatus.Sent;
                    log.SentAt = now;
                    r.LastSentAt = now;
                    reached?.Add(address);
                    sent++;
                }
                catch (Exception ex)
                {
                    log.Status = NotificationStatus.Failed;
                    log.ErrorMessage = ex.Message;
                    _logger.LogWarning(ex, "Customer LINE recipient {Id} failed for alert {AlertId}", r.Id, alert.Id);
                }
            }
            else if (r.Channel == NotificationChannel.Email)
            {
                if (!allowed.Email || !seenEmail.Add(address)) continue;
                emailConfigured ??= await _email.IsConfiguredAsync();
                if (emailConfigured != true)
                {
                    _logger.LogDebug("SMTP not configured — skipping e-mail recipient {Id}", r.Id);
                    continue;
                }
                if (emailHtml == null)
                {
                    var (title, body) = CustomerEmailTemplate.SplitLineText(lineText);
                    var baseUrl = await NotificationSettings.GetAppBaseUrlAsync(_context, _configuration);
                    var siteUrl = baseUrl != null ? $"{baseUrl}/My/Site/{site.Id}" : null;
                    var when = isRecovery ? alert.ResolvedAt ?? now : alert.OccurredAt == default ? now : alert.OccurredAt;
                    emailSubject = CustomerEmailTemplate.Subject(site.SiteName, title);
                    emailHtml = CustomerEmailTemplate.BuildAlertHtml(site.SiteName, title, body, when, siteUrl);
                }
                try
                {
                    await _email.SendAsync(address, emailSubject!, emailHtml, lineText);
                    log.Status = NotificationStatus.Sent;
                    log.SentAt = now;
                    r.LastSentAt = now;
                    sent++;
                }
                catch (Exception ex)
                {
                    log.Status = NotificationStatus.Failed;
                    log.ErrorMessage = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
                    _logger.LogWarning(ex, "Customer e-mail recipient {Id} failed for alert {AlertId}", r.Id, alert.Id);
                }
            }
            else continue;

            _context.NotificationLogs.Add(log);
        }

        await _context.SaveChangesAsync();
        return sent;
    }

    public async Task<(bool Success, string Message)> SendTestAsync(CustomerNotificationRecipient recipient)
    {
        if (recipient.Channel == NotificationChannel.Line)
            return await _line.SendTestMessageAsync(recipient.Address);

        if (recipient.Channel != NotificationChannel.Email)
            return (false, "ช่องทางนี้ยังไม่รองรับการส่งทดสอบ");
        if (!await _email.IsConfiguredAsync())
            return (false, "ยังไม่ได้ตั้งค่า SMTP — ไปที่ ตั้งค่าระบบ → ตั้งค่าอีเมล");

        var now = _clock.GetUtcNow().UtcDateTime;
        var log = new NotificationLog
        {
            RecipientLineId = recipient.Address,
            Channel = NotificationChannel.Email,
            Title = "Test e-mail",
            Message = "ทดสอบการส่งอีเมลแจ้งเตือน",
            RelatedEntityType = "Test",
            CreatedAt = now,
        };
        try
        {
            var baseUrl = await NotificationSettings.GetAppBaseUrlAsync(_context, _configuration);
            await _email.SendAsync(recipient.Address, "[SolarApp] ทดสอบการส่งอีเมลแจ้งเตือน",
                CustomerEmailTemplate.BuildTestHtml(baseUrl));
            log.Status = NotificationStatus.Sent;
            log.SentAt = now;
            // A delivered test is the proof an e-mail address can have.
            recipient.VerifiedAt ??= now;
            recipient.LastSentAt = now;
            _context.NotificationLogs.Add(log);
            await _context.SaveChangesAsync();
            return (true, $"ส่งอีเมลทดสอบไปที่ {recipient.Address} แล้ว");
        }
        catch (Exception ex)
        {
            log.Status = NotificationStatus.Failed;
            log.ErrorMessage = ex.Message;
            _context.NotificationLogs.Add(log);
            try { await _context.SaveChangesAsync(); } catch { /* logging only */ }
            return (false, $"ส่งไม่สำเร็จ: {ex.Message}");
        }
    }
}
