using System.Linq.Expressions;
using SolarApp.Models.Enums;

namespace SolarApp.Models;

public class SystemAlert
{
    public int Id { get; set; }
    public int InstallationId { get; set; }
    public Installation Installation { get; set; } = null!;

    public AlertType AlertType { get; set; }
    public AlertSeverity Severity { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? RootCauseAnalysis { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
    public DateTime? AcknowledgedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }

    public string? AcknowledgedByUserId { get; set; }
    public ApplicationUser? AcknowledgedByUser { get; set; }

    public bool IsNotified { get; set; }

    /// <summary>
    /// Deliberately never delivered: closed before the notifier reached it,
    /// raised against a site that has since been deleted or deactivated, or
    /// imported from a vendor already cleared. IsNotified is set on these too
    /// so nothing retries them, which made them indistinguishable from alerts
    /// that really went out — and the recovery step then sent a
    /// "✅ กลับมาทำงานปกติแล้ว" for a problem nobody had been told about.
    /// </summary>
    public bool NotificationSuppressed { get; set; }

    /// <summary>
    /// The alert rule that raised this, when one did. Rule alerts were
    /// identified only by Title == rule name, so nothing could close them:
    /// not when the condition cleared, not when the rule was disabled or
    /// deleted. No foreign key on purpose — deleting a rule must not be
    /// blocked by, or cascade into, the history it produced.
    /// </summary>
    public int? AlertRuleId { get; set; }
    public bool IsResolved => ResolvedAt.HasValue;

    /// <summary>
    /// Alerts a customer sees in the portal — the kinds that can reach their
    /// LINE channel, minus contract-renewal reminders, which are a sales
    /// matter for us. The menu badge and the alert list both use this; the
    /// badge used to count every open alert (predictions, rule alerts,
    /// contract reminders) while the list showed only some, so it promised
    /// alerts the customer could never find.
    /// </summary>
    public static readonly Expression<Func<SystemAlert, bool>> CustomerVisible = a =>
        (a.AlertType == AlertType.Offline
         || a.AlertType == AlertType.Error
         || a.AlertType == AlertType.LowPerformance
         || a.AlertType == AlertType.MaintenanceDue
         || a.AlertType == AlertType.WarrantyExpiring)
        && !a.Title.StartsWith(AlertTitles.ContractExpiring);

    /// <summary>
    /// Title as it should be shown. Vendor alarms imported before the adapters
    /// gained a name fallback were stored with an empty Title, and rendered on
    /// the dashboard as a severity badge with no text beside it. This keeps
    /// those rows readable without rewriting history.
    /// </summary>
    public string DisplayTitle => TitleFor(Title, Message);

    /// <summary>
    /// Same fallback as <see cref="DisplayTitle"/>, callable on a projection —
    /// EF can't translate an instance property into SQL, so pages that select
    /// columns instead of entities apply it after materialising.
    /// </summary>
    public static string TitleFor(string? title, string? message)
    {
        if (!string.IsNullOrWhiteSpace(title)) return title;

        // The old import built the body by interpolation, so a nameless alarm
        // with no description left only the scaffolding behind.
        var body = message?.Trim();
        if (!string.IsNullOrEmpty(body) && body != "(Device: N/A)")
            return body;

        return "แจ้งเตือนจากผู้ผลิต — ไม่ได้ส่งรายละเอียดมา";
    }
}
