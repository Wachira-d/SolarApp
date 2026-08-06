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
    public bool IsResolved => ResolvedAt.HasValue;

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
