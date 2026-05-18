using System.Net.Http.Json;
using System.Text.Json;
using SolarApp.Models.Enums;
using SolarApp.Services.InverterApi.Common;
using SolarApp.Services.InverterApi.Models;

namespace SolarApp.Services.InverterApi.Adapters;

/// <summary>
/// Hoymiles S-Miles Cloud API adapter — V3 (post-Jan 2025).
///
/// Hoymiles migrated from global.hoymiles.com/platform/api/gateway to
/// neapi.hoymiles.com in January 2025. The old URL returns 404.
///
/// V3 Authentication flow:
///   1. POST /iam/pub/0/c/region_c  (detect user's region → get login_url)
///   2. POST {login_url}/iam/pub/0/c/login_c  (username + hashed password → token)
///
/// Password encoding: MD5(password) + "." + Base64(SHA256(password))
///
/// Credentials mapping:
///   Username = S-Miles Cloud email / phone
///   Password = S-Miles Cloud password (plain text — we hash it)
///   PlantId  = Station ID (sid)
/// </summary>
public class HoymilesAdapter : IInverterApiAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HoymilesAdapter> _logger;

    // V3 API base — replaced old global.hoymiles.com/platform/api/gateway
    private const string RegionApiBase = "https://euapi.hoymiles.com";
    private const string DefaultApiBase = "https://neapi.hoymiles.com";

    public InverterBrand Brand => InverterBrand.Hoymiles;

    // Static session cache
    private static readonly Dictionary<string, (string Token, string ApiBase, DateTime ExpiresAt)> _tokenCache = new();
    private static readonly object _tokenLock = new();
    private static readonly TimeSpan TokenTtl = TimeSpan.FromMinutes(30);

    public HoymilesAdapter(IHttpClientFactory httpClientFactory, ILogger<HoymilesAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // ══════════════════════════════════════════════════════════════
    //  Authentication
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Multi-layered login with 6 fallback strategies.
    /// Tries each in order until one succeeds, accumulates diagnostic log.
    ///
    /// Layer 1: Cache hit (instant)
    /// Layer 2: V3 region-aware login with hashed password (MD5.Base64SHA256)
    /// Layer 3: V3 login with plain password (same endpoint)
    /// Layer 4: V3 login with MD5-only password
    /// Layer 5: Direct V3 login on multiple regional bases (skip region detection)
    /// Layer 6: Legacy V0 login on multiple bases with body/header wrapper
    /// </summary>
    private async Task<(string? Token, string? ApiBase, string Error)> LoginAsync(ApiCredentials credentials)
    {
        var cacheKey = $"hoymiles|{credentials.Username}|{credentials.Password?.Length ?? 0}";

        // ─── Layer 1: Cache ───
        lock (_tokenLock)
        {
            if (_tokenCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.ExpiresAt)
                return (cached.Token, cached.ApiBase, "OK (cached)");
        }

        if (string.IsNullOrEmpty(credentials.Username) || string.IsNullOrEmpty(credentials.Password))
            return (null, null, "Username and password are required.");

        var diag = new List<string>();
        var password = credentials.Password;

        // Pre-compute password variants (used by multiple layers)
        var passwordVariants = ComputePasswordVariants(password);

        // ─── Layer 0: User-specified API endpoint override ───
        // If the installation has CloudApiBaseUrl set, try it FIRST.
        // Two modes:
        //   (a) Full URL  — contains "/login" or "/auth" — we POST there directly
        //       (operator already found the working endpoint via browser dev tools).
        //   (b) Base URL  — we try a list of common login paths against the base.
        // Each path × password-variant combo is one attempt; first success wins.
        if (!string.IsNullOrWhiteSpace(credentials.ApiBaseUrl))
        {
            var rawOverride = credentials.ApiBaseUrl.Trim().TrimEnd('/');
            var looksLikeFullLoginUrl = rawOverride.Contains("/login", StringComparison.OrdinalIgnoreCase)
                || rawOverride.Contains("/auth", StringComparison.OrdinalIgnoreCase);
            diag.Add($"L0:override={ShortName(rawOverride)}{(looksLikeFullLoginUrl ? "(full)" : "(base)")}");

            // Candidate paths to try when only the base is provided.
            // Ordered by likelihood for Hoymiles S-Miles Cloud Global tenants.
            string[] candidatePaths = looksLikeFullLoginUrl
                ? new[] { "" } // Full URL — use as-is
                : new[]
                {
                    "/platform/api/gateway/iam/auth_login",                 // V0 platform gateway — common on global
                    "/iam-api/web/iam/login",                               // Modern IAM web service
                    "/iam-api/v1/iam/login",                                // IAM v1
                    "/api/iam/auth/login",                                  // Generic API path
                    "/website/api/auth/login",                              // Website API path
                    "/iam/pub/0/c/login_c",                                 // V3 at root (works on neapi/euapi)
                    "/platform/api/gateway/iam/pub/0/c/login_c",            // V3 via gateway
                    "/pvm-iam/api/0/iam/auth_login",                        // V0 legacy
                };

            var passwordsToTry = new[]
            {
                ("hashed", passwordVariants["hashed"]),
                ("plain",  password),
                ("md5",    passwordVariants["md5"]),
            };

            (string? Token, string? ApiBase)? overrideHit = null;

            foreach (var candidatePath in candidatePaths)
            {
                var url = looksLikeFullLoginUrl ? rawOverride : rawOverride + candidatePath;

                foreach (var (variantName, variantValue) in passwordsToTry)
                {
                    var label = $"L0-{ShortName(url)}-{variantName}";
                    var r = await TryRawV3LoginAsync(credentials.Username, variantValue, url, label, diag);
                    if (r.Token != null)
                    {
                        // Derive the api-base for subsequent data calls. Strip the
                        // login-specific suffix so CallApiAsync can append data paths.
                        var derivedBase = url;
                        foreach (var suffix in candidatePaths)
                        {
                            if (!string.IsNullOrEmpty(suffix) && derivedBase.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                            {
                                derivedBase = derivedBase.Substring(0, derivedBase.Length - suffix.Length);
                                // Re-add /platform/api/gateway if that's where login was
                                if (suffix.Contains("/platform/api/gateway"))
                                    derivedBase += "/platform/api/gateway";
                                break;
                            }
                        }
                        overrideHit = (r.Token, derivedBase);
                        break;
                    }
                }
                if (overrideHit.HasValue) break;
            }

            if (overrideHit.HasValue)
                return CacheAndReturn(cacheKey, overrideHit.Value.Token!, overrideHit.Value.ApiBase!,
                    $"OK via L0 override | " + string.Join(" → ", diag));
        }

        // ─── Layer 2: V3 region-aware login (HASHED password) ───
        var (loginUrl, apiBase) = await DetectRegionAsync(credentials.Username, diag);

        var v3Result = await TryV3LoginAsync(credentials.Username, passwordVariants["hashed"], loginUrl, "L2-hashed", diag);
        if (v3Result.Token != null)
            return CacheAndReturn(cacheKey, v3Result.Token, apiBase, "OK via L2 (V3 hashed) | " + string.Join(" → ", diag));

        // ─── Layer 3: V3 login with PLAIN password ───
        var plainResult = await TryV3LoginAsync(credentials.Username, password, loginUrl, "L3-plain", diag);
        if (plainResult.Token != null)
            return CacheAndReturn(cacheKey, plainResult.Token, apiBase, "OK via L3 (V3 plain) | " + string.Join(" → ", diag));

        // ─── Layer 4: V3 login with MD5-only password ───
        var md5Result = await TryV3LoginAsync(credentials.Username, passwordVariants["md5"], loginUrl, "L4-md5", diag);
        if (md5Result.Token != null)
            return CacheAndReturn(cacheKey, md5Result.Token, apiBase, "OK via L4 (V3 md5) | " + string.Join(" → ", diag));

        // ─── Layer 5: Try other regional bases directly (skip region detection) ───
        // Order matters: try `global.hoymiles.com` first because it's the
        // S-Miles Cloud global instance used by accounts created via
        // global.hoymiles.com/website/login (many Thai customers / O&M accounts).
        // Removed naapi/apapi — those hostnames don't resolve (DNS-dead) and
        // were wasting ~30s per sync attempt in DNS timeout.
        var regionalBases = new[]
        {
            "https://global.hoymiles.com",
            "https://neapi.hoymiles.com",
            "https://euapi.hoymiles.com",
        };
        foreach (var baseUrl in regionalBases)
        {
            if (baseUrl == loginUrl) continue; // already tried
            var r = await TryV3LoginAsync(credentials.Username, passwordVariants["hashed"], baseUrl, $"L5-{ShortName(baseUrl)}", diag);
            if (r.Token != null)
            {
                // global.hoymiles.com serves data APIs under /platform/api/gateway;
                // other hosts serve them at root. Bake the prefix into apiBase so
                // CallApiAsync produces the right URL on subsequent data calls.
                var effectiveApiBase = baseUrl.Contains("global.hoymiles.com", StringComparison.OrdinalIgnoreCase)
                    ? $"{baseUrl}/platform/api/gateway"
                    : baseUrl;
                return CacheAndReturn(cacheKey, r.Token, effectiveApiBase, $"OK via L5 ({baseUrl}) | " + string.Join(" → ", diag));
            }
        }

        // ─── Layer 6: Legacy V0 login ───
        var legacyResult = await TryLegacyLoginAsync(credentials, diag);
        if (legacyResult.Token != null)
        {
            return CacheAndReturn(cacheKey, legacyResult.Token, legacyResult.ApiBase ?? DefaultApiBase,
                $"OK via L6 (legacy) | " + string.Join(" → ", diag));
        }

        // All layers failed — return aggregated diagnostic
        var summary = string.Join(" → ", diag);
        _logger.LogWarning("Hoymiles login failed all 6 layers: {Diag}", summary);
        return (null, null, $"Login failed (all 6 strategies). Trace: {Truncate(summary, 500)}");
    }

    /// <summary>
    /// Pre-compute all password hash variants once (avoid recomputing).
    /// </summary>
    private static Dictionary<string, string> ComputePasswordVariants(string password)
    {
        var pwBytes = System.Text.Encoding.UTF8.GetBytes(password);
        using var md5 = System.Security.Cryptography.MD5.Create();
        using var sha = System.Security.Cryptography.SHA256.Create();
        var md5Hex = BitConverter.ToString(md5.ComputeHash(pwBytes)).Replace("-", "").ToLowerInvariant();
        var sha256Base64 = Convert.ToBase64String(sha.ComputeHash(pwBytes));
        return new Dictionary<string, string>
        {
            ["plain"] = password,
            ["md5"] = md5Hex,
            ["sha256"] = sha256Base64,
            ["hashed"] = $"{md5Hex}.{sha256Base64}", // observed V3 format
        };
    }

    /// <summary>
    /// Layer 2 prep: detect user's region via /iam/pub/0/c/region_c.
    /// Returns (loginUrl, apiBase) — falls back to defaults on any error.
    /// </summary>
    private async Task<(string LoginUrl, string ApiBase)> DetectRegionAsync(string username, List<string> diag)
    {
        try
        {
            var client = CreateClient();
            var resp = await client.PostAsJsonAsync($"{RegionApiBase}/iam/pub/0/c/region_c",
                new { email = username });

            if (!resp.IsSuccessStatusCode)
            {
                diag.Add($"region:HTTP{(int)resp.StatusCode}");
                return (RegionApiBase, DefaultApiBase);
            }

            var content = await resp.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(content).RootElement;
            if (!json.TryGetProperty("data", out var data))
            {
                diag.Add("region:no-data");
                return (RegionApiBase, DefaultApiBase);
            }

            var loginUrl = ExtractAbsoluteUrl(data, "login_url", "loginUrl", "server_url") ?? RegionApiBase;
            var apiBase = ExtractAbsoluteUrl(data, "api_url", "apiUrl", "data_url") ?? DefaultApiBase;
            diag.Add($"region:OK→{ShortName(loginUrl)}");
            return (loginUrl, apiBase);
        }
        catch (Exception ex)
        {
            diag.Add($"region:err({ex.GetType().Name})");
            return (RegionApiBase, DefaultApiBase);
        }
    }

    /// <summary>
    /// Like TryV3LoginAsync but uses fullUrl exactly as-given (no path append).
    /// Used by the Layer-0 override which already knows the full login URL
    /// or is iterating through a list of candidate paths.
    ///
    /// Sends browser-like headers (User-Agent, Origin, Referer, Accept) so
    /// Hoymiles' web tier (which gates some endpoints behind these checks)
    /// accepts the request the same way it would from a real browser session.
    /// Mimics the GoodWe SEMS portal login pattern: when there's no public
    /// API, talk to the web portal endpoints with browser identity.
    ///
    /// Always returns — never throws.
    /// </summary>
    private async Task<(string? Token, int StatusCode)> TryRawV3LoginAsync(
        string username, string password, string fullUrl, string label, List<string> diag)
    {
        try
        {
            var client = CreateClient();
            var body = new { user_name = username, password };

            var req = new HttpRequestMessage(HttpMethod.Post, fullUrl)
            {
                Content = JsonContent.Create(body)
            };

            // Browser-style headers — Hoymiles web tier often rejects requests
            // missing these (returns HTML login page instead of JSON, or 403).
            req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            // Derive Origin + Referer from the URL so cross-origin checks pass
            if (Uri.TryCreate(fullUrl, UriKind.Absolute, out var uri))
            {
                var origin = $"{uri.Scheme}://{uri.Host}";
                req.Headers.TryAddWithoutValidation("Origin", origin);
                req.Headers.TryAddWithoutValidation("Referer", $"{origin}/website/login");
            }

            var resp = await client.SendAsync(req);

            if (!resp.IsSuccessStatusCode)
            {
                diag.Add($"{label}:HTTP{(int)resp.StatusCode}");
                return (null, (int)resp.StatusCode);
            }

            var content = await resp.Content.ReadAsStringAsync();
            JsonElement json;
            try { json = JsonDocument.Parse(content).RootElement; }
            catch
            {
                // Some web tiers return HTML on auth failure even with 200 status
                diag.Add($"{label}:not-json(len={content.Length})");
                return (null, (int)resp.StatusCode);
            }

            var status = SafeGetString(json, "status") ?? SafeGetInt(json, "status")?.ToString();
            if (status != "0" && status != "200" && status != "success" && status != null)
            {
                var msg = SafeGetString(json, "message") ?? SafeGetString(json, "msg") ?? "no-msg";
                diag.Add($"{label}:status={status}({Truncate(msg, 30)})");
                return (null, (int)resp.StatusCode);
            }

            var token = ExtractToken(json);
            if (string.IsNullOrEmpty(token))
            {
                // Web-portal endpoints may set auth via Set-Cookie instead of token field.
                // If we got 200 + no error status, try to use a cookie value as the token.
                if (resp.Headers.TryGetValues("Set-Cookie", out var cookies))
                {
                    var cookieToken = string.Join("; ", cookies);
                    if (!string.IsNullOrWhiteSpace(cookieToken))
                    {
                        diag.Add($"{label}:OK(cookie-auth)");
                        return (cookieToken, (int)resp.StatusCode);
                    }
                }
                diag.Add($"{label}:no-token-in-resp");
                return (null, (int)resp.StatusCode);
            }

            diag.Add($"{label}:OK");
            return (token, (int)resp.StatusCode);
        }
        catch (TaskCanceledException) { diag.Add($"{label}:timeout"); return (null, 0); }
        catch (HttpRequestException ex) { diag.Add($"{label}:net({Truncate(ex.Message, 30)})"); return (null, 0); }
        catch (Exception ex) { diag.Add($"{label}:err({ex.GetType().Name})"); return (null, 0); }
    }

    /// <summary>
    /// Try V3 login at given URL with given password variant.
    /// Records diagnostic in diag list. Always returns — never throws.
    /// For global.hoymiles.com the login path lives under /platform/api/gateway;
    /// for neapi/euapi it's at root. Path is auto-selected by host name.
    /// </summary>
    private async Task<(string? Token, int StatusCode)> TryV3LoginAsync(
        string username, string password, string baseUrl, string label, List<string> diag)
    {
        try
        {
            var client = CreateClient();
            var body = new { user_name = username, password };

            // global.hoymiles.com proxies the V3 IAM endpoints behind /platform/api/gateway.
            // Other regional bases serve V3 paths at root. Same handler, different prefix.
            var path = baseUrl.Contains("global.hoymiles.com", StringComparison.OrdinalIgnoreCase)
                ? "/platform/api/gateway/iam/pub/0/c/login_c"
                : "/iam/pub/0/c/login_c";
            var url = $"{baseUrl.TrimEnd('/')}{path}";
            var resp = await client.PostAsJsonAsync(url, body);

            if (!resp.IsSuccessStatusCode)
            {
                diag.Add($"{label}:HTTP{(int)resp.StatusCode}");
                return (null, (int)resp.StatusCode);
            }

            var content = await resp.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(content).RootElement;
            var status = SafeGetString(json, "status") ?? SafeGetInt(json, "status")?.ToString();

            if (status != "0" && status != "200" && status != "success")
            {
                var msg = SafeGetString(json, "message") ?? SafeGetString(json, "msg") ?? "no-msg";
                diag.Add($"{label}:status={status}({Truncate(msg, 30)})");
                return (null, (int)resp.StatusCode);
            }

            var token = ExtractToken(json);
            if (string.IsNullOrEmpty(token))
            {
                diag.Add($"{label}:no-token-in-resp");
                return (null, (int)resp.StatusCode);
            }

            diag.Add($"{label}:OK");
            return (token, (int)resp.StatusCode);
        }
        catch (TaskCanceledException) { diag.Add($"{label}:timeout"); return (null, 0); }
        catch (HttpRequestException ex) { diag.Add($"{label}:net({Truncate(ex.Message, 30)})"); return (null, 0); }
        catch (Exception ex) { diag.Add($"{label}:err({ex.GetType().Name})"); return (null, 0); }
    }

    /// <summary>
    /// Layer 6: Legacy V0 login. Last-resort fallback with old body/header
    /// wrapper format. Removed intl.hoymiles.com (DNS-dead) and the bare
    /// /platform/api/gateway global path (returns 404 — superseded by V3).
    /// </summary>
    private async Task<(string? Token, string? ApiBase)> TryLegacyLoginAsync(ApiCredentials credentials, List<string> diag)
    {
        var attempts = new[]
        {
            ("https://neapi.hoymiles.com", "/pvm-iam/api/0/iam/auth_login"),
            ("https://global.hoymiles.com", "/pvm-iam/api/0/iam/auth_login"),
        };

        foreach (var (baseUrl, path) in attempts)
        {
            try
            {
                var client = CreateClient();
                var body = new
                {
                    body = new { user_name = credentials.Username, password = credentials.Password },
                    header = new { language = "en" }
                };
                var resp = await client.PostAsJsonAsync($"{baseUrl}{path}", body);

                if (!resp.IsSuccessStatusCode)
                {
                    diag.Add($"L6-{ShortName(baseUrl)}:HTTP{(int)resp.StatusCode}");
                    continue;
                }

                var content = await resp.Content.ReadAsStringAsync();
                var json = JsonDocument.Parse(content).RootElement;
                var status = SafeGetString(json, "status") ?? SafeGetInt(json, "status")?.ToString();
                if (status != "0" && status != "200" && status != "success")
                {
                    diag.Add($"L6-{ShortName(baseUrl)}:status={status}");
                    continue;
                }

                var token = ExtractToken(json);
                if (string.IsNullOrEmpty(token))
                {
                    diag.Add($"L6-{ShortName(baseUrl)}:no-token");
                    continue;
                }

                diag.Add($"L6-{ShortName(baseUrl)}:OK");
                // Pick the right data-API base for downstream calls:
                //   neapi  → use as-is (V0 + data share same root)
                //   global → must include /platform/api/gateway prefix
                //   other  → fall back to neapi (DefaultApiBase)
                string apiBase;
                if (baseUrl.Contains("neapi", StringComparison.OrdinalIgnoreCase))
                    apiBase = baseUrl;
                else if (baseUrl.Contains("global.hoymiles.com", StringComparison.OrdinalIgnoreCase))
                    apiBase = $"{baseUrl}/platform/api/gateway";
                else
                    apiBase = DefaultApiBase;
                return (token, apiBase);
            }
            catch (Exception ex) { diag.Add($"L6-{ShortName(baseUrl)}:err({ex.GetType().Name})"); }
        }
        return (null, null);
    }

    /// <summary>
    /// Extract token from response JSON — handles multiple known field names.
    /// </summary>
    private static string? ExtractToken(JsonElement json)
    {
        // Try root-level first
        var token = SafeGetString(json, "token") ?? SafeGetString(json, "access_token")
                 ?? SafeGetString(json, "sid") ?? SafeGetString(json, "jwt");
        if (!string.IsNullOrEmpty(token)) return token;

        // Try data.* nesting
        if (json.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            token = SafeGetString(data, "token") ?? SafeGetString(data, "access_token")
                 ?? SafeGetString(data, "sid") ?? SafeGetString(data, "jwt");
            if (!string.IsNullOrEmpty(token)) return token;

            // Try data.data.* (double-nested observed in some legacy responses)
            if (data.TryGetProperty("data", out var innerData) && innerData.ValueKind == JsonValueKind.Object)
            {
                token = SafeGetString(innerData, "token") ?? SafeGetString(innerData, "access_token");
                if (!string.IsNullOrEmpty(token)) return token;
            }
        }
        return null;
    }

    /// <summary>
    /// Extract an absolute http(s) URL from any of the given JSON properties.
    /// Returns null if none are absolute URLs.
    /// </summary>
    private static string? ExtractAbsoluteUrl(JsonElement el, params string[] props)
    {
        foreach (var prop in props)
        {
            var v = SafeGetString(el, prop);
            if (!string.IsNullOrEmpty(v) && (v.StartsWith("http://") || v.StartsWith("https://")))
                return v.TrimEnd('/');
        }
        return null;
    }

    /// <summary>Short label for diagnostic logging.</summary>
    private static string ShortName(string url) =>
        url.Replace("https://", "").Replace("http://", "").Split('/')[0].Split('.')[0];

    /// <summary>Cache token and return success tuple.</summary>
    private (string Token, string ApiBase, string Message) CacheAndReturn(string cacheKey, string token, string apiBase, string message)
    {
        lock (_tokenLock) { _tokenCache[cacheKey] = (token, apiBase, DateTime.UtcNow.Add(TokenTtl)); }
        _logger.LogInformation("Hoymiles login: {Msg}", message);
        return (token, apiBase, message);
    }

    /// <summary>
    /// Multi-layer API call with format fallbacks + network retry.
    ///
    /// Layer 1: Flat body + Authorization header (V3 standard)
    /// Layer 2: Body/header wrapper + token in header (legacy)
    /// Layer 3: Flat body + Bearer prefix on Authorization
    /// Layer 4: Network retry with exponential backoff (1s, 2s, 4s)
    /// </summary>
    private async Task<JsonElement?> CallApiAsync(string token, string apiBase, string path, object requestBody)
    {
        var url = $"{apiBase.TrimEnd('/')}/{path.TrimStart('/')}";
        var attemptDiag = new List<string>();

        // Strategy 1: Flat body + Authorization: {token}
        var r1 = await TryCallAsync(url, requestBody, ("Authorization", token), attemptDiag, "v3-flat");
        if (r1.HasValue && IsSuccessStatus(r1.Value)) return ExtractData(r1.Value);

        // Strategy 2: Legacy body/header wrapper
        var legacyBody = new { body = requestBody, header = new { language = "en", token } };
        var r2 = await TryCallAsync(url, legacyBody, null, attemptDiag, "legacy-wrap");
        if (r2.HasValue && IsSuccessStatus(r2.Value)) return ExtractData(r2.Value);

        // Strategy 3: Authorization: Bearer {token}
        var r3 = await TryCallAsync(url, requestBody, ("Authorization", $"Bearer {token}"), attemptDiag, "bearer");
        if (r3.HasValue && IsSuccessStatus(r3.Value)) return ExtractData(r3.Value);

        // All strategies failed — check if it's a token issue
        var lastResp = r1 ?? r2 ?? r3;
        if (lastResp.HasValue)
        {
            var status = SafeGetString(lastResp.Value, "status") ?? SafeGetInt(lastResp.Value, "status")?.ToString();
            if (status is "100" or "401" or "token_expired")
            {
                lock (_tokenLock) { _tokenCache.Clear(); }
                _logger.LogWarning("Hoymiles {Path}: token expired (status={Status}), cache cleared", path, status);
                return null;
            }
        }

        _logger.LogWarning("Hoymiles {Path} all strategies failed: {Diag}", path, string.Join(" → ", attemptDiag));
        return null;
    }

    /// <summary>
    /// Single API call attempt with network retry (3 attempts, exponential backoff).
    /// Returns null only on terminal network failure; returns JSON even for non-2xx for caller to inspect.
    /// </summary>
    private async Task<JsonElement?> TryCallAsync(string url, object body, (string Name, string Value)? authHeader,
        List<string> diag, string label)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var client = CreateClient();
                if (authHeader.HasValue)
                {
                    client.DefaultRequestHeaders.Remove(authHeader.Value.Name);
                    client.DefaultRequestHeaders.Add(authHeader.Value.Name, authHeader.Value.Value);
                }

                var resp = await client.PostAsJsonAsync(url, body);
                var content = await resp.Content.ReadAsStringAsync();

                // 5xx → retry; 4xx/2xx → return immediately
                if ((int)resp.StatusCode >= 500 && (int)resp.StatusCode < 600 && attempt < maxAttempts)
                {
                    var backoff = (int)Math.Pow(2, attempt - 1) * 1000;
                    diag.Add($"{label}:HTTP{(int)resp.StatusCode}→retry{attempt}");
                    await Task.Delay(backoff);
                    continue;
                }

                if (string.IsNullOrEmpty(content)) { diag.Add($"{label}:empty"); return null; }

                try
                {
                    var json = JsonDocument.Parse(content).RootElement;
                    diag.Add($"{label}:HTTP{(int)resp.StatusCode}");
                    return json;
                }
                catch
                {
                    diag.Add($"{label}:invalid-json");
                    return null;
                }
            }
            catch (TaskCanceledException)
            {
                if (attempt < maxAttempts)
                {
                    diag.Add($"{label}:timeout-retry{attempt}");
                    await Task.Delay((int)Math.Pow(2, attempt - 1) * 1000);
                    continue;
                }
                diag.Add($"{label}:timeout");
                return null;
            }
            catch (HttpRequestException ex)
            {
                if (attempt < maxAttempts)
                {
                    diag.Add($"{label}:net-retry{attempt}");
                    await Task.Delay((int)Math.Pow(2, attempt - 1) * 1000);
                    continue;
                }
                diag.Add($"{label}:net({ex.Message.Substring(0, Math.Min(30, ex.Message.Length))})");
                return null;
            }
            catch (Exception ex)
            {
                diag.Add($"{label}:err({ex.GetType().Name})");
                return null;
            }
        }
        return null;
    }

    private static bool IsSuccessStatus(JsonElement json)
    {
        var s = SafeGetString(json, "status") ?? SafeGetInt(json, "status")?.ToString();
        return s is "0" or "200" or "success";
    }

    private static JsonElement? ExtractData(JsonElement json)
    {
        if (json.TryGetProperty("data", out var data)) return data;
        return json;
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient("default");
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 SolarApp/2.0");
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    // ══════════════════════════════════════════════════════════════
    //  IInverterApiAdapter — Legacy methods
    // ══════════════════════════════════════════════════════════════

    public async Task<(bool Success, string Message)> TestConnectionAsync(ApiCredentials credentials)
    {
        if (string.IsNullOrEmpty(credentials.Username))
            return (false, "Username (S-Miles Cloud email/phone) is required.");
        if (string.IsNullOrEmpty(credentials.Password))
            return (false, "Password (S-Miles Cloud password) is required.");

        var (token, apiBase, loginError) = await LoginAsync(credentials);
        if (string.IsNullOrEmpty(token))
            return (false, $"Hoymiles login failed: {loginError}");

        // Verify by fetching station list
        var data = await CallApiAsync(token, apiBase ?? DefaultApiBase, "pvmc/api/0/station/select_by_page_c",
            new { page = 1, page_size = 10 });
        if (data == null)
            return (false, "Hoymiles: logged in but station list call failed.");

        return (true, "Connected to Hoymiles S-Miles Cloud!");
    }

    public async Task<PlantInfo?> GetPlantInfoAsync(ApiCredentials credentials)
    {
        var (token, apiBase, _) = await LoginAsync(credentials);
        if (token == null) return null;

        var data = await CallApiAsync(token, apiBase ?? DefaultApiBase, "pvmc/api/0/station/select_by_page_c",
            new { page = 1, page_size = 50 });
        if (data == null || data.Value.ValueKind != JsonValueKind.Object) return null;

        // data.list is array of stations
        if (!data.Value.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            // Alternative: data might be direct array
            if (data.Value.ValueKind == JsonValueKind.Array)
                list = data.Value;
            else
                return null;
        }

        // Find the matching station by PlantId, or take first
        foreach (var station in list.EnumerateArray())
        {
            if (station.ValueKind != JsonValueKind.Object) continue;
            var sid = SafeGetString(station, "id")
                   ?? SafeGetString(station, "station_id")
                   ?? SafeGetString(station, "sid");

            if (!string.IsNullOrEmpty(credentials.PlantId)
                && !string.Equals(sid, credentials.PlantId, StringComparison.OrdinalIgnoreCase))
                continue;

            return new PlantInfo
            {
                PlantId = sid ?? "",
                PlantName = SafeGetString(station, "station_name")
                         ?? SafeGetString(station, "name") ?? "",
                CapacityKw = SafeGetDecimal(station, "installed_capacity")
                           + SafeGetDecimal(station, "capacity"),
                Address = SafeGetString(station, "address")
                       ?? SafeGetString(station, "location"),
                Latitude = SafeGetDouble(station, "latitude"),
                Longitude = SafeGetDouble(station, "longitude")
            };
        }
        return null;
    }

    public async Task<RealtimeData?> GetRealtimeDataAsync(ApiCredentials credentials)
    {
        if (string.IsNullOrEmpty(credentials.PlantId)) return null;
        var (token, apiBase, _) = await LoginAsync(credentials);
        if (token == null) return null;

        // Try station-level realtime data
        var data = await CallApiAsync(token, apiBase ?? DefaultApiBase, "pvmc/api/0/station/find_c",
            new { sid = long.TryParse(credentials.PlantId, out var sidLong) ? (object)sidLong : credentials.PlantId });
        if (data == null)
        {
            // Alternative endpoint
            data = await CallApiAsync(token, apiBase ?? DefaultApiBase, "pvmc/api/0/station_data/real_g_c",
                new { sid = credentials.PlantId });
        }
        if (data == null || data.Value.ValueKind != JsonValueKind.Object) return null;

        var d = data.Value;
        var currentPower = SafeGetDecimal(d, "real_power")
                        + SafeGetDecimal(d, "power")
                        + SafeGetDecimal(d, "last_data_power");
        var todayEnergy = SafeGetDecimal(d, "today_eq")
                       + SafeGetDecimal(d, "day_eq")
                       + SafeGetDecimal(d, "today_energy");
        var totalEnergy = SafeGetDecimal(d, "total_eq")
                       + SafeGetDecimal(d, "month_eq_total")
                       + SafeGetDecimal(d, "total_energy");
        var capacityW = SafeGetDecimal(d, "installed_capacity");

        // Online detection: power > 0 or explicit status field
        var statusVal = SafeGetInt(d, "status") ?? SafeGetInt(d, "connect_status") ?? -1;
        var isOnline = statusVal == 1 || currentPower > 0;

        // Power might be in watts from some endpoints
        if (currentPower > 500) currentPower /= 1000m; // likely watts
        if (todayEnergy > 10000) todayEnergy /= 1000m; // likely Wh
        if (totalEnergy > 10000000) totalEnergy /= 1000m; // likely Wh

        return new RealtimeData
        {
            Timestamp = DateTime.UtcNow,
            CurrentPowerKw = currentPower,
            TodayEnergyKwh = todayEnergy,
            TotalEnergyKwh = totalEnergy,
            IsOnline = isOnline
        };
    }

    public async Task<List<EnergyRecord>> GetHistoricalDataAsync(
        ApiCredentials credentials, DateTime from, DateTime to)
    {
        var records = new List<EnergyRecord>();
        if (string.IsNullOrEmpty(credentials.PlantId)) return records;
        var (token, apiBase, _) = await LoginAsync(credentials);
        if (token == null) return records;

        try
        {
            var data = await CallApiAsync(token, apiBase ?? DefaultApiBase, "pvmc/api/0/station/data/count_playback_power_by_day",
                new
                {
                    sid = credentials.PlantId,
                    start_date = from.ToString("yyyy-MM-dd"),
                    end_date = to.ToString("yyyy-MM-dd"),
                    type = 1 // daily
                });

            if (data == null) return records;

            // Try to find array in response
            JsonElement arr;
            if (data.Value.ValueKind == JsonValueKind.Array)
                arr = data.Value;
            else if (data.Value.TryGetProperty("list", out var l) && l.ValueKind == JsonValueKind.Array)
                arr = l;
            else if (data.Value.TryGetProperty("data", out var d2) && d2.ValueKind == JsonValueKind.Array)
                arr = d2;
            else
                return records;

            foreach (var item in arr.EnumerateArray())
            {
                try
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var dateStr = SafeGetString(item, "date")
                               ?? SafeGetString(item, "time")
                               ?? SafeGetString(item, "day");
                    if (dateStr == null || !DateTime.TryParse(dateStr, out var date)) continue;

                    var energy = SafeGetDecimal(item, "eq")
                              + SafeGetDecimal(item, "energy")
                              + SafeGetDecimal(item, "day_eq");
                    if (energy > 10000) energy /= 1000m; // Wh → kWh

                    records.Add(new EnergyRecord
                    {
                        Timestamp = date,
                        EnergyGenerationKwh = energy,
                        GridExportKwh = SafeGetDecimal(item, "grid_sell_eq"),
                        GridImportKwh = SafeGetDecimal(item, "grid_buy_eq"),
                        SelfConsumptionKwh = SafeGetDecimal(item, "self_use_eq"),
                    });
                }
                catch (Exception itemEx)
                {
                    _logger.LogWarning(itemEx, "Hoymiles: skipping malformed history record");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hoymiles GetHistoricalData failed");
        }
        return records;
    }

    public async Task<List<AlarmRecord>> GetAlarmsAsync(
        ApiCredentials credentials, DateTime from, DateTime to)
    {
        var alarms = new List<AlarmRecord>();
        if (string.IsNullOrEmpty(credentials.PlantId)) return alarms;
        var (token, apiBase, _) = await LoginAsync(credentials);
        if (token == null) return alarms;

        try
        {
            var data = await CallApiAsync(token, apiBase ?? DefaultApiBase, "pvmc/api/0/alarm/select_by_page",
                new
                {
                    sid = credentials.PlantId,
                    page = 1,
                    page_size = 100,
                    start_date = from.ToString("yyyy-MM-dd"),
                    end_date = to.ToString("yyyy-MM-dd")
                });

            if (data == null) return alarms;

            JsonElement arr;
            if (data.Value.ValueKind == JsonValueKind.Array) arr = data.Value;
            else if (data.Value.TryGetProperty("list", out var l)) arr = l;
            else return alarms;

            foreach (var item in arr.EnumerateArray())
            {
                try
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    alarms.Add(new AlarmRecord
                    {
                        AlarmId = SafeGetString(item, "alarm_id") ?? SafeGetString(item, "id") ?? "",
                        AlarmName = SafeGetString(item, "alarm_name")
                                 ?? SafeGetString(item, "alarm_code")
                                 ?? SafeGetString(item, "name") ?? "",
                        Severity = SafeGetInt(item, "alarm_level") ?? SafeGetInt(item, "level") ?? 3,
                        OccurredAt = DateTime.TryParse(SafeGetString(item, "alarm_time")
                            ?? SafeGetString(item, "start_time"), out var dt) ? dt : DateTime.UtcNow,
                        ClearedAt = DateTime.TryParse(SafeGetString(item, "end_time")
                            ?? SafeGetString(item, "clear_time"), out var ct) ? ct : null,
                        DeviceName = SafeGetString(item, "mi_sn")
                                  ?? SafeGetString(item, "device_sn")
                    });
                }
                catch (Exception itemEx)
                {
                    _logger.LogWarning(itemEx, "Hoymiles: skipping malformed alarm");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hoymiles GetAlarms failed");
        }
        return alarms;
    }

    public async Task<List<DeviceInfo>> GetDevicesAsync(ApiCredentials credentials)
    {
        var devices = new List<DeviceInfo>();
        if (string.IsNullOrEmpty(credentials.PlantId)) return devices;
        var (token, apiBase, _) = await LoginAsync(credentials);
        if (token == null) return devices;

        try
        {
            var data = await CallApiAsync(token, apiBase ?? DefaultApiBase, "pvmc/api/0/device/select_by_page",
                new { sid = credentials.PlantId, page = 1, page_size = 100 });
            if (data == null) return devices;

            JsonElement arr;
            if (data.Value.ValueKind == JsonValueKind.Array) arr = data.Value;
            else if (data.Value.TryGetProperty("list", out var l)) arr = l;
            else return devices;

            foreach (var item in arr.EnumerateArray())
            {
                try
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    devices.Add(new DeviceInfo
                    {
                        DeviceId = SafeGetString(item, "device_id")
                                ?? SafeGetString(item, "mi_sn")
                                ?? SafeGetString(item, "sn") ?? "",
                        DeviceName = SafeGetString(item, "device_name")
                                  ?? SafeGetString(item, "mi_name") ?? "",
                        DeviceType = SafeGetString(item, "device_type") ?? "Microinverter",
                        Model = SafeGetString(item, "model")
                             ?? SafeGetString(item, "mi_model")
                             ?? SafeGetString(item, "product_model"),
                        SerialNumber = SafeGetString(item, "mi_sn")
                                    ?? SafeGetString(item, "sn"),
                        FirmwareVersion = SafeGetString(item, "firmware_version")
                                       ?? SafeGetString(item, "fw_version"),
                        IsOnline = (SafeGetInt(item, "status") ?? 0) == 1,
                        RatedPowerKw = SafeGetNullableDecimal(item, "rated_power")
                    });
                }
                catch (Exception itemEx)
                {
                    _logger.LogWarning(itemEx, "Hoymiles: skipping malformed device");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hoymiles GetDevices failed");
        }
        return devices;
    }

    // ══════════════════════════════════════════════════════════════
    //  ApiResult<T> methods
    // ══════════════════════════════════════════════════════════════

    public async Task<ApiResult<bool>> TestConnectionResultAsync(ApiCredentials credentials, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var (ok, msg) = await TestConnectionAsync(credentials);
            sw.Stop();
            return ok ? ApiResult<bool>.Ok(true, msg, sw.Elapsed)
                      : ApiResultClassifier.ClassifyErrorMessage<bool>(msg, "Hoymiles", sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<bool>(ex, "Hoymiles", sw.Elapsed); }
    }

    public async Task<ApiResult<PlantInfo>> GetPlantInfoResultAsync(ApiCredentials credentials, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var info = await GetPlantInfoAsync(credentials);
            sw.Stop();
            return info != null
                ? ApiResult<PlantInfo>.Ok(info, "Plant info retrieved", sw.Elapsed)
                : ApiResult<PlantInfo>.Fail(ApiResultKind.PlantNotFound,
                    $"Station '{credentials.PlantId}' not found on Hoymiles account.", duration: sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<PlantInfo>(ex, "Hoymiles", sw.Elapsed); }
    }

    public async Task<ApiResult<RealtimeData>> GetRealtimeResultAsync(ApiCredentials credentials, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var data = await GetRealtimeDataAsync(credentials);
            sw.Stop();
            if (data == null)
                return ApiResult<RealtimeData>.Fail(ApiResultKind.CloudServerError,
                    "Hoymiles API returned no data.", duration: sw.Elapsed);
            return data.IsOnline
                ? ApiResult<RealtimeData>.Ok(data, "Device online", sw.Elapsed)
                : ApiResult<RealtimeData>.Offline(data,
                    "Hoymiles API OK, but microinverter reports offline. Normal at night/cloudy.",
                    sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<RealtimeData>(ex, "Hoymiles", sw.Elapsed); }
    }

    public async Task<ApiResult<List<EnergyRecord>>> GetHistoricalResultAsync(ApiCredentials credentials, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var list = await GetHistoricalDataAsync(credentials, from, to);
            sw.Stop();
            return ApiResult<List<EnergyRecord>>.Ok(list, $"{list.Count} record(s)", sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<List<EnergyRecord>>(ex, "Hoymiles", sw.Elapsed); }
    }

    public async Task<ApiResult<List<AlarmRecord>>> GetAlarmsResultAsync(ApiCredentials credentials, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var list = await GetAlarmsAsync(credentials, from, to);
            sw.Stop();
            return ApiResult<List<AlarmRecord>>.Ok(list, $"{list.Count} alarm(s)", sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<List<AlarmRecord>>(ex, "Hoymiles", sw.Elapsed); }
    }

    public async Task<ApiResult<List<DeviceInfo>>> GetDevicesResultAsync(ApiCredentials credentials, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var list = await GetDevicesAsync(credentials);
            sw.Stop();
            return ApiResult<List<DeviceInfo>>.Ok(list, $"{list.Count} device(s)", sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<List<DeviceInfo>>(ex, "Hoymiles", sw.Elapsed); }
    }

    // ══════════════════════════════════════════════════════════════
    //  Safe JSON helpers
    // ══════════════════════════════════════════════════════════════

    private static string? SafeGetString(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            _ => null
        };
    }

    private static decimal SafeGetDecimal(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return 0;
        if (!el.TryGetProperty(prop, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String
            && decimal.TryParse(v.GetString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var n2)) return n2;
        return 0;
    }

    private static decimal? SafeGetNullableDecimal(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n)) return n;
        return null;
    }

    private static int? SafeGetInt(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var n2)) return n2;
        return null;
    }

    private static double? SafeGetDouble(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String
            && double.TryParse(v.GetString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var n2)) return n2;
        return null;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
