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

    /// <summary>Returns the (DeferOnLongWait, EffectiveWait) decision for a caller.</summary>
    public static (bool ShouldDefer, TimeSpan Wait) Decide(string accountKey)
    {
        var wait = GetRequiredWait(accountKey);
        return wait > MaxAcceptableWait ? (true, wait) : (false, wait);
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
