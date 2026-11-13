using SolarApp.Models.Enums;

namespace SolarApp.Models.Notifications;

/// <summary>
/// Somebody who receives a customer's notifications on one channel: a LINE
/// user id bound through the add-friend link, or an e-mail address. A
/// customer can have several (owner, spouse, site manager).
/// </summary>
public class CustomerNotificationRecipient
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    /// <summary>Null = every site of the customer; otherwise only this site.</summary>
    public int? InstallationId { get; set; }
    public Installation? Installation { get; set; }

    public NotificationChannel Channel { get; set; }
    /// <summary>LINE user id ("U…") or e-mail address.</summary>
    public string Address { get; set; } = string.Empty;
    /// <summary>LINE display name or a label the operator typed.</summary>
    public string? DisplayName { get; set; }

    public bool IsEnabled { get; set; } = true;
    /// <summary>When the address was proven (LINE bound by code; e-mail test sent).</summary>
    public DateTime? VerifiedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSentAt { get; set; }
}

/// <summary>Per customer and event type: which channels carry it.</summary>
public class CustomerNotificationPreference
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    public NotificationEventType EventType { get; set; }
    public bool LineEnabled { get; set; } = true;
    public bool EmailEnabled { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A one-time code behind a "ผูก LINE" link. The customer adds the OA as a
/// friend and sends the code; the webhook binds the sender to the customer.
/// </summary>
public class LineBindingCode
{
    public int Id { get; set; }
    /// <summary>8 characters from an unambiguous alphabet (no 0/O/1/I).</summary>
    public string Code { get; set; } = string.Empty;
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public int? InstallationId { get; set; }

    public string? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }

    public DateTime? UsedAt { get; set; }
    public string? UsedByLineUserId { get; set; }

    public bool IsUsable(DateTime nowUtc) => UsedAt == null && nowUtc < ExpiresAt;
}
