using System.Net;

namespace SolarApp.Services.InverterApi.Common;

/// <summary>
/// A DelegatingHandler that transparently retries transient HTTP failures
/// for EVERY client resolved from IHttpClientFactory — so all seven
/// inverter adapters get retry + backoff without each re-implementing it
/// (Deye / FoxESS / GoodWe / Sigenergy previously had none).
///
/// What it retries (transient only):
///   • Network errors (HttpRequestException) — DNS blip, connection reset.
///   • Timeouts (TaskCanceledException NOT tied to the caller's token).
///   • HTTP 5xx (server errors).
/// What it does NOT retry:
///   • 4xx including 429 — a 401/400/404 is a real answer; a 429 needs
///     backoff-by-spacing, not a rapid resend that amplifies the overload.
///   • Requests the caller cancelled.
///
/// Idempotency: the inverter clouds are read-mostly and even their POST
/// "login"/"query" endpoints are effectively idempotent (a repeated login
/// returns a fresh token; a repeated query returns the same data), so
/// retrying a POST is safe here.  Request content is buffered once so the
/// body can be resent, and a fresh request message is cloned per attempt
/// (an HttpRequestMessage can only be sent once).
/// </summary>
public sealed class ResilientHttpHandler : DelegatingHandler
{
    private readonly int _maxAttempts;
    private readonly ILogger<ResilientHttpHandler> _logger;

    public ResilientHttpHandler(ILogger<ResilientHttpHandler> logger, int maxAttempts = 3)
    {
        _logger = logger;
        _maxAttempts = Math.Max(1, maxAttempts);
    }

    // Vendor cloud host suffixes whose POST endpoints are read/login calls
    // and safe to retry.  Everything else (notably api.line.me, whose POST
    // pushes a message and MUST NOT be duplicated) only retries idempotent
    // GET/HEAD.  GETs are always safe to retry regardless of host.
    private static readonly string[] RetryablePostHosts =
    {
        "hoymiles.com", "huawei.com", "semsportal.com", "soliscloud.com",
        "deyecloud.com", "foxesscloud.com", "sigencloud.com", "solarman.cn",
    };

    private static bool MayRetry(HttpRequestMessage request)
    {
        if (request.Method == HttpMethod.Get || request.Method == HttpMethod.Head)
            return true; // idempotent by definition
        var host = request.RequestUri?.Host;
        if (string.IsNullOrEmpty(host)) return false;
        // Non-GET (POST/PUT): only for the inverter vendor clouds, whose
        // "login"/"query" POSTs are effectively idempotent.
        foreach (var suffix in RetryablePostHosts)
            if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Non-retryable requests (e.g. LINE push POSTs) go straight through.
        if (!MayRetry(request))
            return await base.SendAsync(request, cancellationToken);

        // Buffer the body once so each cloned attempt can resend it.
        if (request.Content != null)
        {
            try { await request.Content.LoadIntoBufferAsync(); }
            catch { /* streaming/non-bufferable content — fall through, single attempt */ }
        }

        Exception? lastException = null;
        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            HttpResponseMessage? response = null;
            try
            {
                var attemptRequest = attempt == 1 ? request : await CloneAsync(request);
                response = await base.SendAsync(attemptRequest, cancellationToken);

                // Success or a definitive non-retryable answer → return as-is.
                if (!IsRetryableStatus(response.StatusCode) || attempt == _maxAttempts)
                    return response;

                _logger.LogDebug(
                    "ResilientHttp: {Method} {Host} → HTTP {Status}, retry {Attempt}/{Max}",
                    request.Method, request.RequestUri?.Host, (int)response.StatusCode, attempt, _maxAttempts);
                response.Dispose(); // discard before retrying
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Caller cancelled — propagate, never retry.
                throw;
            }
            catch (TaskCanceledException tce)
            {
                // Timeout (HttpClient.Timeout fired, not the caller's token).
                lastException = tce;
                if (attempt == _maxAttempts)
                    throw;
                _logger.LogDebug("ResilientHttp: {Host} timeout, retry {Attempt}/{Max}",
                    request.RequestUri?.Host, attempt, _maxAttempts);
            }
            catch (HttpRequestException hre)
            {
                lastException = hre;
                if (attempt == _maxAttempts)
                    throw;
                _logger.LogDebug("ResilientHttp: {Host} network error ({Msg}), retry {Attempt}/{Max}",
                    request.RequestUri?.Host, hre.Message, attempt, _maxAttempts);
            }

            // Exponential backoff with a small jitter-free cap: 0.5s, 1s, 2s…
            var delayMs = (int)(500 * Math.Pow(2, attempt - 1));
            delayMs = Math.Min(delayMs, 5000);
            await Task.Delay(delayMs, cancellationToken);
        }

        // Unreachable in practice (loop returns/throws), but satisfy the compiler.
        throw lastException ?? new HttpRequestException("ResilientHttp: exhausted retries");
    }

    // Retry 5xx only.  429 (rate limited) is deliberately NOT retried here:
    // the vendor windows are per-minute, so a rapid retry just amplifies the
    // overload — rate spacing (NextSyncDueAt / HuaweiRateGate) is the right
    // tool for that, and the adapter still sees the 429 to react to.
    private static bool IsRetryableStatus(HttpStatusCode status) =>
        (int)status >= 500 && (int)status < 600;

    /// <summary>Clone a request (method, uri, version, headers, buffered content) for a retry.</summary>
    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage req)
    {
        var clone = new HttpRequestMessage(req.Method, req.RequestUri) { Version = req.Version };

        // Copy the (already-buffered) content by reading its bytes.
        if (req.Content != null)
        {
            var bytes = await req.Content.ReadAsByteArrayAsync();
            var newContent = new ByteArrayContent(bytes);
            foreach (var h in req.Content.Headers)
                newContent.Headers.TryAddWithoutValidation(h.Key, h.Value);
            clone.Content = newContent;
        }

        foreach (var h in req.Headers)
            clone.Headers.TryAddWithoutValidation(h.Key, h.Value);

        // Preserve options (e.g. anything the pipeline stashed).
        foreach (var opt in req.Options)
            ((IDictionary<string, object?>)clone.Options)[opt.Key] = opt.Value;

        return clone;
    }
}
