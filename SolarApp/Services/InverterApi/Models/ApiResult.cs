namespace SolarApp.Services.InverterApi.Models;

/// <summary>
/// Classifies the outcome of every adapter API call.
/// Callers must check Kind FIRST before accessing Data.
/// </summary>
public enum ApiResultKind
{
    /// <summary>API connected, device returned data normally.</summary>
    Success,
    /// <summary>API connected, but device/plant reports offline / no data.</summary>
    DeviceOffline,
    /// <summary>HTTP timeout — cloud server didn't respond within deadline.</summary>
    Timeout,
    /// <summary>DNS, TCP, TLS or socket-level failure — can't reach cloud at all.</summary>
    NetworkError,
    /// <summary>401/403 — credentials rejected or expired.</summary>
    AuthenticationFailed,
    /// <summary>429 — too many requests, back off.</summary>
    RateLimited,
    /// <summary>5xx — cloud server returned an error.</summary>
    CloudServerError,
    /// <summary>4xx (non-auth, non-rate-limit) — our request was wrong.</summary>
    ClientError,
    /// <summary>API returned data we couldn't parse — schema changed?</summary>
    ParseError,
    /// <summary>Credentials missing or invalid format (pre-flight check).</summary>
    InvalidCredentials,
    /// <summary>PlantId / StationId not found on cloud account.</summary>
    PlantNotFound,
    /// <summary>Catch-all for truly unexpected errors.</summary>
    UnknownError
}

/// <summary>
/// Wraps every adapter response so callers can distinguish offline devices
/// from API failures without relying on nullability.
/// </summary>
public class ApiResult<T>
{
    public ApiResultKind Kind { get; init; }
    public T? Data { get; init; }
    public string Message { get; init; } = string.Empty;

    /// <summary>Short technical detail for logs (HTTP code, exception type, etc.)</summary>
    public string? TechnicalDetail { get; init; }

    /// <summary>HTTP status code if an HTTP response was received.</summary>
    public int? HttpStatusCode { get; init; }

    /// <summary>How many times the call was retried before giving up.</summary>
    public int RetryCount { get; init; }

    /// <summary>Wall-clock duration of the call (including retries).</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>UTC timestamp when the call completed.</summary>
    public DateTime CompletedAt { get; init; } = DateTime.UtcNow;

    public bool IsSuccess => Kind is ApiResultKind.Success or ApiResultKind.DeviceOffline;
    public bool IsDeviceOffline => Kind == ApiResultKind.DeviceOffline;
    public bool IsApiFailure => !IsSuccess;
    public bool IsRetryable => Kind is ApiResultKind.Timeout
                                      or ApiResultKind.NetworkError
                                      or ApiResultKind.RateLimited
                                      or ApiResultKind.CloudServerError;

    // ── Factory methods ──

    public static ApiResult<T> Ok(T data, string message = "OK", TimeSpan duration = default)
        => new() { Kind = ApiResultKind.Success, Data = data, Message = message, Duration = duration, HttpStatusCode = 200 };

    public static ApiResult<T> Offline(T? partialData, string message, TimeSpan duration = default)
        => new() { Kind = ApiResultKind.DeviceOffline, Data = partialData, Message = message, Duration = duration, HttpStatusCode = 200 };

    public static ApiResult<T> Fail(ApiResultKind kind, string message, string? detail = null,
        int? httpStatus = null, int retries = 0, TimeSpan duration = default)
        => new() { Kind = kind, Data = default, Message = message, TechnicalDetail = detail,
                    HttpStatusCode = httpStatus, RetryCount = retries, Duration = duration };

    /// <summary>
    /// Build a human-readable alert message with full context.
    /// Format: "[BRAND] SITE: What happened | Technical: detail | Retry: N | Duration: Xms"
    /// </summary>
    public string ToAlertMessage(string brand, string siteName)
    {
        var parts = new List<string> { $"[{brand}] {siteName}: {Message}" };
        if (!string.IsNullOrEmpty(TechnicalDetail))
            parts.Add($"Technical: {TechnicalDetail}");
        if (HttpStatusCode.HasValue && HttpStatusCode != 200)
            parts.Add($"HTTP {HttpStatusCode}");
        if (RetryCount > 0)
            parts.Add($"Retried {RetryCount}x");
        parts.Add($"Duration: {Duration.TotalMilliseconds:0}ms");
        return string.Join(" | ", parts);
    }

    /// <summary>
    /// Short status line for dashboard/diagnostics: "✓ Online" or "✕ Timeout after 30s"
    /// </summary>
    public string ToStatusLine()
    {
        if (Kind == ApiResultKind.Success) return "✓ Online, data received";
        if (Kind == ApiResultKind.DeviceOffline) return "⚠ API connected, device offline";
        return $"✕ {Kind}: {Message}";
    }
}
