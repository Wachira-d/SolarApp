using SolarApp.Models.Enums;

namespace SolarApp.Models;

/// <summary>
/// A single "when EVENT happens (for this site, or globally), ALSO push a
/// LINE message to RECIPIENT" rule.
///
/// This is the granular routing layer the operator asked for: e.g. two
/// rows with EventType = WarrantyExpiry — one whose LineId is a group, one
/// whose LineId is a personal user — means a warranty-expiry event fans
/// out to BOTH.
///
/// It is ADDITIVE on top of the existing routing (operator alert group +
/// customer LineGroupId/LineUserId + partner staff).  The dispatcher
/// de-duplicates by LineId, so listing a recipient here that the legacy
/// flow already reached costs nothing — it's sent once.
///
/// InstallationId semantics:
///   * null  → GLOBAL route: applies to every installation.
///   * value → applies only to that one installation.
/// Both global and site-specific routes that match an event all fire.
/// </summary>
public class NotificationRoute
{
    public int Id { get; set; }

    /// <summary>Null = global (all installations); otherwise scoped to one site.</summary>
    public int? InstallationId { get; set; }
    public Installation? Installation { get; set; }

    /// <summary>Which event category triggers this route. <see cref="NotificationEventType.All"/> = every event.</summary>
    public NotificationEventType EventType { get; set; }

    /// <summary>LINE group / user / room id to push to.</summary>
    public string LineId { get; set; } = string.Empty;

    /// <summary>Human-friendly note shown in the admin list (e.g. "ทีมช่าง", "เจ้าของไซต์").</summary>
    public string? Label { get; set; }

    /// <summary>Disabled routes are kept for history but never fire.</summary>
    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedByUserId { get; set; }
}
