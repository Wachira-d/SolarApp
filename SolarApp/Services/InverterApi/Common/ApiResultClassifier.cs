using System.Text.Json;
using SolarApp.Services.InverterApi.Models;

namespace SolarApp.Services.InverterApi.Common;

/// <summary>
/// Shared helpers for turning exceptions / error strings into classified ApiResult failures.
/// All 5 adapters use this so error classification is uniform across brands.
/// </summary>
public static class ApiResultClassifier
{
    public static ApiResult<T> ClassifyException<T>(Exception ex, string brand, TimeSpan duration)
    {
        return ex switch
        {
            TaskCanceledException or OperationCanceledException =>
                ApiResult<T>.Fail(ApiResultKind.Timeout,
                    $"{brand} API timeout — cloud server did not respond in time.",
                    detail: ex.Message, duration: duration),

            HttpRequestException hre =>
                ApiResult<T>.Fail(ApiResultKind.NetworkError,
                    $"Network error reaching {brand}: {hre.Message}.",
                    detail: hre.InnerException?.Message ?? hre.StatusCode?.ToString(),
                    duration: duration),

            JsonException je =>
                ApiResult<T>.Fail(ApiResultKind.ParseError,
                    $"{brand} returned an unexpected response format — cloud API may have changed.",
                    detail: je.Message, duration: duration),

            UnauthorizedAccessException =>
                ApiResult<T>.Fail(ApiResultKind.AuthenticationFailed,
                    $"{brand} rejected the credentials.",
                    detail: ex.Message, duration: duration),

            _ =>
                ApiResult<T>.Fail(ApiResultKind.UnknownError,
                    $"{brand}: {ex.GetType().Name} — {ex.Message}",
                    duration: duration)
        };
    }

    /// <summary>
    /// Best-effort classification from a string error message (used when we only
    /// have a message from legacy TestConnectionAsync).
    /// </summary>
    public static ApiResult<T> ClassifyErrorMessage<T>(string? msg, string brand, TimeSpan duration)
    {
        var m = (msg ?? "").ToLowerInvariant();
        var kind = m switch
        {
            var s when s.Contains("timeout") || s.Contains("timed out") => ApiResultKind.Timeout,
            var s when s.Contains("401") || s.Contains("403")
                      || s.Contains("unauthori") || s.Contains("credential")
                      || s.Contains("login failed") || s.Contains("auth fail") => ApiResultKind.AuthenticationFailed,
            var s when s.Contains("429") || s.Contains("rate limit") => ApiResultKind.RateLimited,
            var s when s.Contains(" 500") || s.Contains(" 502") || s.Contains(" 503") || s.Contains(" 504")
                      || s.Contains("server error") || s.Contains("internal server") => ApiResultKind.CloudServerError,
            var s when s.Contains("network") || s.Contains("connection") || s.Contains("dns")
                      || s.Contains("socket") => ApiResultKind.NetworkError,
            var s when s.Contains("not found") || s.Contains("no plant") || s.Contains("station not")
                      || s.Contains("invalid plant") => ApiResultKind.PlantNotFound,
            var s when s.Contains("parse") || s.Contains("json") || s.Contains("unexpected format")
                      => ApiResultKind.ParseError,
            _ => ApiResultKind.UnknownError
        };

        return ApiResult<T>.Fail(kind,
            $"[{brand}] {msg ?? "Unknown error"}",
            detail: msg,
            duration: duration);
    }
}
