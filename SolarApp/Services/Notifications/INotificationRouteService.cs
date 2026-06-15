using SolarApp.Models.Enums;

namespace SolarApp.Services.Notifications;

/// <summary>
/// Resolves and fires the operator-configured "extra recipient" LINE
/// routes (<see cref="SolarApp.Models.NotificationRoute"/>) that sit on
/// top of the built-in customer / alert-group / partner routing.
/// </summary>
public interface INotificationRouteService
{
    /// <summary>
    /// Returns the distinct LINE ids that should ALSO receive this event,
    /// from both global routes and routes scoped to <paramref name="installationId"/>.
    /// Never throws — returns an empty list if the routes table is missing
    /// or any error occurs.
    /// </summary>
    Task<List<string>> ResolveAsync(NotificationEventType eventType, int? installationId);

    /// <summary>
    /// Resolves routes for the event then pushes <paramref name="message"/>
    /// to each, skipping any id in <paramref name="excludeLineIds"/> (the
    /// recipients the built-in flow already reached, so nobody is messaged
    /// twice).  Logs each send to NotificationLog.  Returns the number of
    /// messages actually dispatched.  Never throws.
    /// </summary>
    Task<int> SendEventAsync(
        NotificationEventType eventType,
        int? installationId,
        string title,
        string message,
        IEnumerable<string>? excludeLineIds = null);

    /// <summary>Maps an AlertType (+ its title) to the routing event category.</summary>
    NotificationEventType FromAlertType(AlertType alertType, string? title = null);
}
