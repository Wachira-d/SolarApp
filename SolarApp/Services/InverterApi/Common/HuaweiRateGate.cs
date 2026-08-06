namespace SolarApp.Services.InverterApi.Common;

/// <summary>
/// Process-wide rate limiter for Huawei FusionSolar Northbound API.
///
/// Huawei NBI enforces ~1 request/minute PER ACCOUNT across ALL endpoints.
/// Without coordination, multiple installations sharing one account, or the
/// scheduled sync running concurrently with a manual "Test Connection",
/// quickly trip failCode=407 ACCESS_FREQUENCY_IS_TOO_HIGH.
///
/// This gate keeps a static per-account "next allowed call" timestamp so that
/// every code path (sync job, dashboard refresh, diagnostics) can check
/// whether it is safe to call right now.
///
/// Behaviour:
///   - GetRequiredWait returns the time until next call is allowed.
///   - If callers can afford to wait (sync, manual test), they Delay(wait).
///   - If the wait exceeds MaxAcceptableWait, they should DEFER and try later
///     instead of blocking the whole pipeline.
///   - On a real 407 response the cooldown is pushed out by ExtendOnRateLimit
///     so we back off harder.
/// </summary>
public static class HuaweiRateGate
{
    private static readonly Dictionary<string, DateTime> _nextAllowedAt = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    /// <summary>Minimum spacing between Huawei calls per account. Tunable via configuration.</summary>
    public static TimeSpan MinSpacing { get; set; } = TimeSpan.FromSeconds(65);

    /// <summary>How long to back off after a real 407 response.</summary>
    public static TimeSpan ExtendOnRateLimit { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>If a caller would have to wait longer than this, defer instead of blocking.</summary>
    public static TimeSpan MaxAcceptableWait { get; set; } = TimeSpan.FromSeconds(30);

    public static string AccountKey(string? username) => "huawei:" + (username ?? "(none)").Trim().ToLowerInvariant();

    public static TimeSpan GetRequiredWait(string accountKey)
    {
        lock (_lock)
        {
            if (!_nextAllowedAt.TryGetValue(accountKey, out var next)) return TimeSpan.Zero;
            var now = DateTime.UtcNow;
            return next > now ? next - now : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// AsyncLocal bypass flag for admin diagnostic probes.  When set on the
    /// current async flow, Decide() returns no-defer/no-wait so the probe
    /// can actually exercise the API end-to-end instead of being deferred
    /// the moment TestConnection burns through the 65s budget.
    ///
    /// MarkCalled / RecordRateLimitResponse still update the shared cooldown
    /// — bypassing only short-circuits the gate's decision for THIS caller.
    /// </summary>
    public static readonly AsyncLocal<bool> DiagnosticBypass = new();

    /// <summary>
    /// Marks the current async flow as a USER-INITIATED action (someone
    /// clicked a button and is watching a spinner).
    ///
    /// Background jobs should defer when the gate is busy — there's another
    /// cycle along shortly.  A human cannot "come back next cycle": refusing
    /// them with "try again in 64s" just makes them click again, which is
    /// both worse UX and more load.  So interactive callers WAIT for the
    /// gate instead of being turned away.
    ///
    /// Note this waits, it does not bypass: Huawei's 1-req/min budget is
    /// still fully respected, so it cannot provoke a 407 ban the way a
    /// bypass would.
    /// </summary>
    public static readonly AsyncLocal<bool> Interactive = new();

    /// <summary>How long an interactive caller is willing to block for the gate.</summary>
    public static TimeSpan InteractiveMaxWait { get; set; } = TimeSpan.FromSeconds(75);

    /// <summary>Returns the (DeferOnLongWait, EffectiveWait) decision for a caller.</summary>
    public static (bool ShouldDefer, TimeSpan Wait) Decide(string accountKey)
    {
        if (DiagnosticBypass.Value) return (false, TimeSpan.Zero);
        var wait = GetRequiredWait(accountKey);
        // A human is waiting → tolerate a much longer wait rather than
        // refusing the action outright.
        var limit = Interactive.Value ? InteractiveMaxWait : MaxAcceptableWait;
        return wait > limit ? (true, wait) : (false, wait);
    }

    /// <summary>Reserves the next call window. Call this immediately BEFORE issuing the HTTP request.</summary>
    public static void MarkCalled(string accountKey)
    {
        lock (_lock)
        {
            _nextAllowedAt[accountKey] = DateTime.UtcNow + MinSpacing;
        }
    }

    /// <summary>Push the next-allowed window further out. Use when the server returns 407.</summary>
    public static void RecordRateLimitResponse(string accountKey)
    {
        lock (_lock)
        {
            var pushOutTo = DateTime.UtcNow + ExtendOnRateLimit;
            if (!_nextAllowedAt.TryGetValue(accountKey, out var current) || current < pushOutTo)
                _nextAllowedAt[accountKey] = pushOutTo;
        }
    }

    /// <summary>Diagnostics — returns next-allowed time per account (used by Admin UI).</summary>
    public static IReadOnlyDictionary<string, DateTime> Snapshot()
    {
        lock (_lock)
        {
            return _nextAllowedAt.ToDictionary(kv => kv.Key, kv => kv.Value);
        }
    }
}
