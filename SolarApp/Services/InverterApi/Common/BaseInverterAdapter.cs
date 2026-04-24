using System.Diagnostics;
using System.Net;
using System.Text.Json;
using SolarApp.Services.InverterApi.Models;

namespace SolarApp.Services.InverterApi.Common;

/// <summary>
/// Shared foundation for all inverter adapters.
/// Every HTTP call goes through ExecuteApiCallAsync which provides:
///   - Per-call timeout (default 25s)
///   - Automatic retry with exponential backoff for retryable errors (timeout, 429, 5xx)
///   - HTTP status classification (401→AuthFailed, 429→RateLimited, etc.)
///   - JSON parse safety
///   - Structured logging
///   - Duration tracking
///   - Consistent error messages
/// </summary>
public abstract class BaseInverterAdapter
{
    protected readonly IHttpClientFactory HttpClientFactory;
    protected readonly ILogger Logger;
    protected const int DefaultTimeoutSeconds = 25;
    protected const int MaxRetries = 2;

    protected BaseInverterAdapter(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        HttpClientFactory = httpClientFactory;
        Logger = logger;
    }

    protected abstract string BrandName { get; }

    // ═══════════════════════════════════════════════════════════
    //  Core HTTP call wrapper — ALL adapter HTTP calls go through here
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Executes an HTTP call with timeout, retry, classification, and logging.
    /// Returns ApiResult with classified Kind so callers never need to catch HTTP exceptions.
    /// </summary>
    protected async Task<ApiResult<JsonElement>> ExecuteApiCallAsync(
        Func<HttpClient, Task<HttpResponseMessage>> sender,
        string endpoint,
        int timeoutSeconds = DefaultTimeoutSeconds,
        int maxRetries = MaxRetries,
        bool retryOnAuthFailure = false)
    {
        var sw = Stopwatch.StartNew();
        int attempt = 0;
        string lastError = "No attempts made";
        string? lastDetail = null;
        int? lastHttpCode = null;

        while (attempt <= maxRetries)
        {
            attempt++;
            try
            {
                var client = HttpClientFactory.CreateClient("default");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

                var response = await sender(client);
                var statusCode = (int)response.StatusCode;
                lastHttpCode = statusCode;

                var content = await response.Content.ReadAsStringAsync();

                // ─── Classify HTTP status ───
                if (response.StatusCode == HttpStatusCode.Unauthorized ||
                    response.StatusCode == HttpStatusCode.Forbidden)
                {
                    if (retryOnAuthFailure && attempt <= 1)
                    {
                        Logger.LogWarning("{Brand} {Endpoint}: auth rejected (HTTP {Code}), will retry with fresh token",
                            BrandName, endpoint, statusCode);
                        continue;
                    }
                    sw.Stop();
                    return ApiResult<JsonElement>.Fail(
                        ApiResultKind.AuthenticationFailed,
                        $"Authentication failed — credentials rejected (HTTP {statusCode}). Re-check API key, username, and password.",
                        detail: Truncate(content, 200),
                        httpStatus: statusCode,
                        duration: sw.Elapsed);
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 30;
                    Logger.LogWarning("{Brand} {Endpoint}: rate limited (429), backing off {Sec}s", BrandName, endpoint, retryAfter);
                    if (attempt <= maxRetries)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(retryAfter, 60)));
                        continue;
                    }
                    sw.Stop();
                    return ApiResult<JsonElement>.Fail(
                        ApiResultKind.RateLimited,
                        $"Rate limited — cloud API blocked requests (HTTP 429). Reduce sync frequency.",
                        detail: $"Retry-After: {retryAfter}s",
                        httpStatus: 429,
                        retries: attempt - 1,
                        duration: sw.Elapsed);
                }

                if (statusCode >= 500)
                {
                    Logger.LogWarning("{Brand} {Endpoint}: server error {Code}, attempt {Att}/{Max}",
                        BrandName, endpoint, statusCode, attempt, maxRetries + 1);
                    lastError = $"Cloud server error (HTTP {statusCode})";
                    lastDetail = Truncate(content, 200);
                    if (attempt <= maxRetries)
                    {
                        await Task.Delay(AdapterResilience.BackoffDelay(attempt));
                        continue;
                    }
                    sw.Stop();
                    return ApiResult<JsonElement>.Fail(
                        ApiResultKind.CloudServerError, lastError, detail: lastDetail,
                        httpStatus: statusCode, retries: attempt - 1, duration: sw.Elapsed);
                }

                if (!response.IsSuccessStatusCode)
                {
                    sw.Stop();
                    return ApiResult<JsonElement>.Fail(
                        ApiResultKind.ClientError,
                        $"API request rejected (HTTP {statusCode}). Check PlantId and request parameters.",
                        detail: Truncate(content, 200),
                        httpStatus: statusCode,
                        duration: sw.Elapsed);
                }

                // ─── Parse JSON ───
                if (string.IsNullOrWhiteSpace(content))
                {
                    sw.Stop();
                    return ApiResult<JsonElement>.Fail(
                        ApiResultKind.ParseError, "API returned empty response body.",
                        httpStatus: statusCode, duration: sw.Elapsed);
                }

                try
                {
                    var json = JsonDocument.Parse(content).RootElement;
                    sw.Stop();
                    return ApiResult<JsonElement>.Ok(json, "OK", sw.Elapsed);
                }
                catch (JsonException jex)
                {
                    sw.Stop();
                    return ApiResult<JsonElement>.Fail(
                        ApiResultKind.ParseError,
                        $"API response is not valid JSON — cloud API may have changed format.",
                        detail: $"{jex.Message}; Body starts: {Truncate(content, 100)}",
                        httpStatus: statusCode,
                        duration: sw.Elapsed);
                }
            }
            catch (TaskCanceledException)
            {
                Logger.LogWarning("{Brand} {Endpoint}: timeout after {Sec}s, attempt {Att}/{Max}",
                    BrandName, endpoint, timeoutSeconds, attempt, maxRetries + 1);
                lastError = $"Request timed out after {timeoutSeconds}s — cloud server too slow or unreachable.";
                if (attempt <= maxRetries)
                {
                    await Task.Delay(AdapterResilience.BackoffDelay(attempt));
                    continue;
                }
            }
            catch (HttpRequestException hre)
            {
                Logger.LogWarning(hre, "{Brand} {Endpoint}: network error, attempt {Att}/{Max}",
                    BrandName, endpoint, attempt, maxRetries + 1);
                lastError = $"Network error — cannot reach cloud server ({hre.Message}).";
                lastDetail = hre.InnerException?.Message;
                if (attempt <= maxRetries)
                {
                    await Task.Delay(AdapterResilience.BackoffDelay(attempt));
                    continue;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "{Brand} {Endpoint}: unexpected error", BrandName, endpoint);
                sw.Stop();
                return ApiResult<JsonElement>.Fail(
                    ApiResultKind.UnknownError,
                    $"Unexpected error — {ex.GetType().Name}: {ex.Message}",
                    detail: ex.StackTrace?.Split('\n').FirstOrDefault(),
                    retries: attempt - 1,
                    duration: sw.Elapsed);
            }
        }

        sw.Stop();
        return ApiResult<JsonElement>.Fail(
            lastHttpCode.HasValue && lastHttpCode >= 500 ? ApiResultKind.CloudServerError
            : lastDetail?.Contains("timeout", StringComparison.OrdinalIgnoreCase) == true ? ApiResultKind.Timeout
            : ApiResultKind.NetworkError,
            lastError,
            detail: lastDetail,
            httpStatus: lastHttpCode,
            retries: attempt - 1,
            duration: sw.Elapsed);
    }

    // ═══════════════════════════════════════════════════════════
    //  Credential pre-flight validation
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Validates that all required credential fields are non-empty.
    /// Returns null if all valid, or a failure ApiResult if any are missing.
    /// </summary>
    protected ApiResult<T>? ValidateRequired<T>(params (string? Value, string FieldName)[] checks)
    {
        var missing = checks.Where(c => string.IsNullOrWhiteSpace(c.Value))
                             .Select(c => c.FieldName).ToList();
        if (!missing.Any()) return null;
        return ApiResult<T>.Fail(
            ApiResultKind.InvalidCredentials,
            $"Missing required credential(s): {string.Join(", ", missing)}. Check installation settings.",
            detail: $"Missing: {string.Join(", ", missing)}");
    }

    // ═══════════════════════════════════════════════════════════
    //  Safe JSON helpers
    // ═══════════════════════════════════════════════════════════

    protected static string? SafeGetString(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    protected static decimal SafeGetDecimal(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return 0;
        if (!el.TryGetProperty(prop, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDecimal(out var d) ? d : 0,
            JsonValueKind.String => decimal.TryParse(v.GetString(), out var d) ? d : 0,
            _ => 0
        };
    }

    protected static decimal? SafeGetNullableDecimal(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDecimal(out var d) ? d : null,
            JsonValueKind.String => decimal.TryParse(v.GetString(), out var d) ? d : null,
            JsonValueKind.Null => null,
            _ => null
        };
    }

    protected static int? SafeGetInt(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var i) ? i : null,
            JsonValueKind.String => int.TryParse(v.GetString(), out var i) ? i : null,
            _ => null
        };
    }

    protected static long? SafeGetLong(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out var l) ? l : null,
            JsonValueKind.String => long.TryParse(v.GetString(), out var l) ? l : null,
            _ => null
        };
    }

    // ═══════════════════════════════════════════════════════════
    //  Pagination helper
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Fetches paginated results. Stops when page returns fewer items than pageSize
    /// or after maxPages.
    /// </summary>
    protected async Task<List<T>> FetchPaginatedAsync<T>(
        Func<int, Task<(List<T> Items, int TotalCount)>> fetchPage,
        int pageSize = 100,
        int maxPages = 10,
        string context = "")
    {
        var all = new List<T>();
        for (int page = 1; page <= maxPages; page++)
        {
            try
            {
                var (items, total) = await fetchPage(page);
                all.AddRange(items);
                if (items.Count < pageSize || all.Count >= total) break;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "{Brand}: pagination error on page {Page} ({Context})", BrandName, page, context);
                break;
            }
        }
        return all;
    }

    // ═══════════════════════════════════════════════════════════
    //  Utility
    // ═══════════════════════════════════════════════════════════

    protected static string Truncate(string? s, int len)
        => s == null ? "" : s.Length <= len ? s : s[..len] + "…";

    /// <summary>
    /// Determines if the API data indicates device is offline,
    /// vs the API call itself failing.
    /// </summary>
    protected static ApiResult<RealtimeData> WrapRealtimeResult(
        RealtimeData data, string brand, TimeSpan duration)
    {
        if (data.IsOnline)
            return ApiResult<RealtimeData>.Ok(data, "Online, data received", duration);

        return ApiResult<RealtimeData>.Offline(data,
            $"API connected successfully, but {brand} reports device is offline/sleeping. " +
            $"Power={data.CurrentPowerKw}kW, TodayEnergy={data.TodayEnergyKwh}kWh. " +
            "This is normal at night. If daytime, check inverter internet connection.",
            duration);
    }
}
