using System.Collections.Concurrent;

namespace SolarApp.Services.InverterApi.Common;

/// <summary>
/// Client-side token-bucket rate limiter for the inverter-vendor clouds
/// whose per-key request budgets are tight (Deye/FoxESS/Sigenergy ~10/min,
/// Solis 100/min, GoodWe ~1/10s).  The recurring sync processes all due
/// installations back-to-back; when several sites share one account this
/// can burst past the vendor's per-minute budget and earn a 429/ban.
///
/// A DelegatingHandler is the least-invasive enforcement point — no adapter
/// changes, applies to every call automatically.  Buckets are keyed by
/// HOST (not account): for the common single-account-per-brand deployment
/// host≈account; when multiple accounts share a host it merely throttles a
/// little more conservatively, which is always safe (it can never exceed
/// the vendor limit, only stay further under it).
///
/// Huawei and Hoymiles are intentionally NOT listed here — Huawei has its
/// own per-kkid HuaweiRateGate (double-throttling would fight it) and
/// Hoymiles has no documented per-key limit.
/// </summary>
public sealed class VendorRateLimitHandler : DelegatingHandler
{
    private sealed class Bucket
    {
        public double Tokens;
        public DateTime LastRefillUtc;
        public readonly object Lock = new();
    }

    private static readonly ConcurrentDictionary<string, Bucket> _buckets = new();

    // host-suffix → (bucket capacity = max burst, refill tokens per minute).
    // Kept a touch under the published limit for safety margin.
    private static readonly (string Suffix, double Capacity, double PerMinute)[] _limits =
    {
        ("deyecloud.com",   8, 8),    // docs 10/min
        ("solarman.cn",     8, 8),    // Deye/Solarman alt host
        ("foxesscloud.com", 8, 8),    // docs 10/min
        ("sigencloud.com",  8, 8),    // unofficial, treat like the others
        ("soliscloud.com",  80, 80),  // docs 100/min
        ("semsportal.com",  6, 6),    // GoodWe ~1/10s anecdotal
    };

    private readonly ILogger<VendorRateLimitHandler> _logger;

    public VendorRateLimitHandler(ILogger<VendorRateLimitHandler> logger) => _logger = logger;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var host = request.RequestUri?.Host;
        var limit = FindLimit(host);
        if (limit.HasValue)
            await AcquireAsync(host!, limit.Value.Capacity, limit.Value.PerMinute, cancellationToken);
        return await base.SendAsync(request, cancellationToken);
    }

    private static (double Capacity, double PerMinute)? FindLimit(string? host)
    {
        if (string.IsNullOrEmpty(host)) return null;
        foreach (var (suffix, cap, perMin) in _limits)
            if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return (cap, perMin);
        return null;
    }

    /// <summary>
    /// Consume one token, waiting (in small steps) if the bucket is empty.
    /// The wait always happens OUTSIDE the lock; the lock only guards the
    /// refill+consume arithmetic.
    /// </summary>
    private async Task AcquireAsync(string host, double capacity, double perMinute, CancellationToken ct)
    {
        var bucket = _buckets.GetOrAdd(host, _ => new Bucket { Tokens = capacity, LastRefillUtc = DateTime.UtcNow });

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            double waitSeconds;
            lock (bucket.Lock)
            {
                var now = DateTime.UtcNow;
                var elapsed = (now - bucket.LastRefillUtc).TotalSeconds;
                if (elapsed > 0)
                {
                    bucket.Tokens = Math.Min(capacity, bucket.Tokens + elapsed * (perMinute / 60.0));
                    bucket.LastRefillUtc = now;
                }

                if (bucket.Tokens >= 1.0)
                {
                    bucket.Tokens -= 1.0;
                    return;
                }

                // Seconds until one token is available.
                waitSeconds = (1.0 - bucket.Tokens) / (perMinute / 60.0);
            }

            // Cap each wait slice so we stay responsive to cancellation and
            // never block a single call for an unreasonable time.
            var delayMs = (int)Math.Ceiling(Math.Min(waitSeconds, 5.0) * 1000);
            if (delayMs > 0)
            {
                _logger.LogDebug("VendorRateLimit: {Host} bucket empty, waiting {Ms}ms", host, delayMs);
                await Task.Delay(delayMs, ct);
            }
        }
    }
}
