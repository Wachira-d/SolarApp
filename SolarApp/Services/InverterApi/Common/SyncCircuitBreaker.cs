using System.Collections.Concurrent;

namespace SolarApp.Services.InverterApi.Common;

/// <summary>
/// Per-installation circuit breaker for the background sync.  After a site
/// fails to sync N times in a row (dead DNS, revoked credentials, a vendor
/// account that's been disabled), the breaker "opens" and the sync skips
/// that site for a cooldown instead of re-hitting a hopeless endpoint every
/// cycle — which wastes the vendor's rate budget and slows the round for
/// everyone else.
///
/// Conservative by design so it never hides a short outage:
///   • Trips only after <see cref="FailureThreshold"/> CONSECUTIVE failures
///     (at the default 15-min cadence that's ~75 min of solid failure).
///   • Re-opens for just <see cref="OpenDuration"/>, then lets one probe
///     through; a single success resets the counter immediately.
///   • "Failure" = the sync threw (network / timeout / unexpected).  A
///     device merely reporting offline is NOT a failure.
/// </summary>
public static class SyncCircuitBreaker
{
    public static int FailureThreshold { get; set; } = 5;
    public static TimeSpan OpenDuration { get; set; } = TimeSpan.FromMinutes(30);

    private sealed class State
    {
        public int ConsecutiveFailures;
        public DateTime OpenUntilUtc;
    }

    private static readonly ConcurrentDictionary<int, State> _state = new();

    /// <summary>True while the breaker is open (site should be skipped this round).</summary>
    public static bool IsOpen(int installationId)
    {
        return _state.TryGetValue(installationId, out var s)
            && DateTime.UtcNow < s.OpenUntilUtc;
    }

    /// <summary>UTC time the breaker re-closes (for logging), or null if closed.</summary>
    public static DateTime? OpenUntil(int installationId)
    {
        if (_state.TryGetValue(installationId, out var s) && DateTime.UtcNow < s.OpenUntilUtc)
            return s.OpenUntilUtc;
        return null;
    }

    public static void RecordSuccess(int installationId)
    {
        if (_state.TryGetValue(installationId, out var s))
        {
            s.ConsecutiveFailures = 0;
            s.OpenUntilUtc = DateTime.MinValue;
        }
    }

    /// <summary>
    /// Record a hard sync failure.  Returns true if this failure just tripped
    /// the breaker open (so the caller can log it once).
    /// </summary>
    public static bool RecordFailure(int installationId)
    {
        var s = _state.GetOrAdd(installationId, _ => new State());
        lock (s)
        {
            s.ConsecutiveFailures++;
            if (s.ConsecutiveFailures >= FailureThreshold && DateTime.UtcNow >= s.OpenUntilUtc)
            {
                s.OpenUntilUtc = DateTime.UtcNow.Add(OpenDuration);
                return true;
            }
        }
        return false;
    }
}
