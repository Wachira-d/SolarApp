using System.Net;

namespace SolarApp.Services.InverterApi.Common;

/// <summary>
/// Shared helpers for adapter HTTP calls.
/// Converts common failure modes into typed, actionable errors so every
/// adapter reports timeouts, auth failures, and rate limiting consistently.
/// </summary>
public static class AdapterResilience
{
    public enum FailureKind
    {
        None,
        Timeout,
        Network,
        AuthExpired,
        RateLimited,
        ServerError,
        ClientError,
        Unknown
    }

    public record CallResult(bool Success, FailureKind Kind, string Message, int StatusCode = 0);

    public static FailureKind Classify(HttpStatusCode code)
    {
        var n = (int)code;
        return n switch
        {
            401 or 403 => FailureKind.AuthExpired,
            429 => FailureKind.RateLimited,
            >= 500 and <= 599 => FailureKind.ServerError,
            >= 400 and <= 499 => FailureKind.ClientError,
            _ => FailureKind.None
        };
    }

    /// <summary>
    /// Wraps an HTTP request call with common timeout/network exception handling.
    /// Returns (response, error message). The caller decides what to do with non-success codes.
    /// </summary>
    public static async Task<(HttpResponseMessage? Response, string? Error, FailureKind Kind)> SafeSendAsync(
        Func<Task<HttpResponseMessage>> sender,
        string context,
        int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var response = await sender();
            return (response, null, FailureKind.None);
        }
        catch (TaskCanceledException) when (cts.IsCancellationRequested)
        {
            return (null, $"{context}: request timed out after {timeoutSeconds}s", FailureKind.Timeout);
        }
        catch (TaskCanceledException ex)
        {
            return (null, $"{context}: request cancelled — {ex.Message}", FailureKind.Timeout);
        }
        catch (HttpRequestException ex)
        {
            return (null, $"{context}: network error — {ex.Message}", FailureKind.Network);
        }
        catch (Exception ex)
        {
            return (null, $"{context}: unexpected error — {ex.Message}", FailureKind.Unknown);
        }
    }

    /// <summary>
    /// Calculates exponential backoff with jitter for retry logic.
    /// </summary>
    public static TimeSpan BackoffDelay(int attempt, int baseMs = 500, int maxMs = 15000)
    {
        var exp = Math.Min(baseMs * Math.Pow(2, attempt - 1), maxMs);
        var jitter = Random.Shared.NextDouble() * 0.3 + 0.85; // 85-115%
        return TimeSpan.FromMilliseconds(exp * jitter);
    }

    /// <summary>
    /// Human-readable summary for logs / UI error messages.
    /// </summary>
    public static string Summarize(FailureKind kind, int statusCode, string? detail = null)
    {
        var prefix = kind switch
        {
            FailureKind.Timeout => "TIMEOUT",
            FailureKind.Network => "NETWORK",
            FailureKind.AuthExpired => "AUTH EXPIRED (re-check credentials)",
            FailureKind.RateLimited => "RATE LIMITED (too many requests)",
            FailureKind.ServerError => $"SERVER ERROR {statusCode}",
            FailureKind.ClientError => $"CLIENT ERROR {statusCode}",
            _ => "ERROR"
        };
        return string.IsNullOrWhiteSpace(detail) ? prefix : $"{prefix}: {detail}";
    }
}
