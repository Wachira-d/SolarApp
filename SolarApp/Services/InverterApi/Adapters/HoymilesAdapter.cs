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
/// V3 Authentication flow (verified against the live web client and the
/// reference TypeScript port at github.com/Eistee82/ioBroker.hoymiles):
///   1. POST /iam/pub/3/auth/pre-insp  body { u: username }
///      → { a: salt-hex, n: nonce-hex, v: version }
///   2. ch = argon2id(password, salt = hexDecode(a),
///                    t=3, m=32768 KiB, p=1, hashLen=32, version=0x13) → hex
///      (Only when v == 3. For v == 1 or 2 fall back to the legacy
///      md5(pwd) + "." + base64(sha256(pwd)) format.)
///   3. POST /iam/pub/3/auth/login  body { u: username, ch, n }
///      → token.  Note `n` is echoed back unchanged from step 1.
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

        // ─── Layer 0a: V3 pre-insp + argon2id login ───
        // Production flow as used by global.hoymiles.com:
        //   1. POST /iam/pub/3/auth/pre-insp { u } → { a, n, v }
        //   2. ch = argon2id(pwd, hexDecode(a))      (when v == 3)
        //   3. POST /iam/pub/3/auth/login { u, ch, n }
        // Try this FIRST against neapi/euapi/global gateway (and any
        // user-supplied override) since it's the only formula the live
        // server actually accepts.
        var chBases = new List<string>();
        if (!string.IsNullOrWhiteSpace(credentials.ApiBaseUrl))
            chBases.Add(credentials.ApiBaseUrl.TrimEnd('/'));
        chBases.Add("https://neapi.hoymiles.com");
        chBases.Add("https://euapi.hoymiles.com");
        chBases.Add("https://global.hoymiles.com/platform/api/gateway");
        foreach (var chBase in chBases.Distinct())
        {
            var chResult = await TryV3PreInspLoginAsync(credentials.Username, password, chBase, $"L0a-{ShortName(chBase)}", diag);
            if (chResult.Token != null)
                return CacheAndReturn(cacheKey, chResult.Token, chBase, $"OK via V3-preinsp | " + string.Join(" → ", diag));
        }

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
            // /iam/pub/3/auth/login is the CURRENT production path (verified via
            // browser DevTools on global.hoymiles.com — login button POSTs here).
            string[] candidatePaths = looksLikeFullLoginUrl
                ? new[] { "" } // Full URL — use as-is
                : new[]
                {
                    "/iam/pub/3/auth/login",                                // V3 NEW (current production)
                    "/platform/api/gateway/iam/pub/3/auth/login",           // V3 NEW via gateway
                    "/platform/api/gateway/iam/auth_login",                 // V0 platform gateway
                    "/iam-api/web/iam/login",                               // Modern IAM web service
                    "/iam-api/v1/iam/login",                                // IAM v1
                    "/api/iam/auth/login",                                  // Generic API path
                    "/website/api/auth/login",                              // Website API path
                    "/iam/pub/0/c/login_c",                                 // V3 OLD (deprecated, kept for back-compat)
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
                    var label = $"L0-{PathLabel(url)}-{variantName}";
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
    /// V3 production login: pre-insp → argon2id (or md5.sha256) → login.
    ///
    /// Step 1  POST {base}/iam/pub/3/auth/pre-insp  body { u }
    ///         → { a: salt-hex, n: nonce-hex, v: 1|2|3 }
    /// Step 2  Compute ch by version:
    ///           v == 3 →  argon2id(pwd, hexDecode(a),
    ///                              t=3, m=32768, p=1, hashLen=32, ver=0x13).Hex()
    ///           v == 1|2 → md5Hex(pwd) + "." + base64(sha256(pwd))
    /// Step 3  POST {base}/iam/pub/3/auth/login  body { u, ch, n }
    ///
    /// Always returns — never throws.
    /// </summary>
    private async Task<(string? Token, int StatusCode)> TryV3PreInspLoginAsync(
        string username, string password, string apiBase, string label, List<string> diag)
    {
        var preInspUrl = $"{apiBase.TrimEnd('/')}/iam/pub/3/auth/pre-insp";
        var loginUrl = $"{apiBase.TrimEnd('/')}/iam/pub/3/auth/login";

        // ── Step 1: pre-insp ───────────────────────────────────────────
        string? saltHex; string? nonce; int version;
        try
        {
            var client = CreateClient();
            var req = BuildHoymilesRequest(HttpMethod.Post, preInspUrl, new { u = username });
            var resp = await client.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                diag.Add($"{label}-preinsp:HTTP{(int)resp.StatusCode}");
                return (null, (int)resp.StatusCode);
            }

            var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
            var status = SafeGetString(json, "status") ?? SafeGetInt(json, "status")?.ToString();
            if (status != "0" && status != "200" && status != "success")
            {
                var msg = SafeGetString(json, "message") ?? SafeGetString(json, "msg") ?? "no-msg";
                diag.Add($"{label}-preinsp:status={status}({Truncate(msg, 20)})");
                return (null, (int)resp.StatusCode);
            }

            if (!json.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                diag.Add($"{label}-preinsp:no-data");
                return (null, (int)resp.StatusCode);
            }

            saltHex = SafeGetString(data, "a");
            nonce = SafeGetString(data, "n");
            version = SafeGetInt(data, "v") ?? 3;

            if (string.IsNullOrEmpty(nonce))
            {
                diag.Add($"{label}-preinsp:missing-n");
                return (null, (int)resp.StatusCode);
            }
            if (version == 3 && string.IsNullOrEmpty(saltHex))
            {
                diag.Add($"{label}-preinsp:missing-a(v3)");
                return (null, (int)resp.StatusCode);
            }
        }
        catch (TaskCanceledException) { diag.Add($"{label}-preinsp:timeout"); return (null, 0); }
        catch (HttpRequestException ex) { diag.Add($"{label}-preinsp:net({Truncate(ex.Message, 20)})"); return (null, 0); }
        catch (Exception ex) { diag.Add($"{label}-preinsp:err({ex.GetType().Name})"); return (null, 0); }

        // ── Step 2: derive ch ──────────────────────────────────────────
        string ch;
        try
        {
            ch = version switch
            {
                3 => EncryptPwdV3(password, saltHex!),
                1 or 2 => EncryptPwdLegacy(password),
                _ => throw new InvalidOperationException($"unsupported pre-insp v={version}"),
            };
        }
        catch (Exception ex)
        {
            diag.Add($"{label}-hash:err({ex.GetType().Name})");
            return (null, 0);
        }

        // ── Step 3: login ──────────────────────────────────────────────
        try
        {
            var client = CreateClient();
            var req = BuildHoymilesRequest(HttpMethod.Post, loginUrl, new { u = username, ch, n = nonce });
            var resp = await client.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                diag.Add($"{label}-login(v{version}):HTTP{(int)resp.StatusCode}");
                return (null, (int)resp.StatusCode);
            }

            var content = await resp.Content.ReadAsStringAsync();
            JsonElement json;
            try { json = JsonDocument.Parse(content).RootElement; }
            catch { diag.Add($"{label}-login(v{version}):not-json"); return (null, (int)resp.StatusCode); }

            var status = SafeGetString(json, "status") ?? SafeGetInt(json, "status")?.ToString();
            if (status != "0" && status != "200" && status != "success")
            {
                var msg = SafeGetString(json, "message") ?? SafeGetString(json, "msg") ?? "no-msg";
                diag.Add($"{label}-login(v{version}):status={status}({Truncate(msg, 20)})");
                return (null, (int)resp.StatusCode);
            }

            var token = ExtractToken(json);
            if (string.IsNullOrEmpty(token))
            {
                diag.Add($"{label}-login(v{version}):no-token");
                return (null, (int)resp.StatusCode);
            }

            diag.Add($"{label}-login(v{version}):OK");
            return (token, (int)resp.StatusCode);
        }
        catch (TaskCanceledException) { diag.Add($"{label}-login(v{version}):timeout"); return (null, 0); }
        catch (HttpRequestException ex) { diag.Add($"{label}-login(v{version}):net({Truncate(ex.Message, 20)})"); return (null, 0); }
        catch (Exception ex) { diag.Add($"{label}-login(v{version}):err({ex.GetType().Name})"); return (null, 0); }
    }

    /// <summary>
    /// argon2id wrapper matching the S-Miles Cloud spec (verified against
    /// captured live traffic: produces byte-identical ch hash).
    /// Parameters: t=3, m=32 MiB, p=1, hashLen=32, version=0x13.
    /// </summary>
    private static string EncryptPwdV3(string password, string saltHex)
    {
        var salt = Convert.FromHexString(saltHex);
        using var argon = new Konscious.Security.Cryptography.Argon2id(
            System.Text.Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = 1,
            Iterations = 3,
            MemorySize = 32 * 1024, // KiB
        };
        var hash = argon.GetBytes(32);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Legacy v1/v2 hash: md5(pwd) + "." + base64(sha256(pwd)).</summary>
    private static string EncryptPwdLegacy(string password)
    {
        var pwBytes = System.Text.Encoding.UTF8.GetBytes(password);
        var md5Hex = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(pwBytes)).ToLowerInvariant();
        var sha256B64 = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(pwBytes));
        return $"{md5Hex}.{sha256B64}";
    }

    /// <summary>
    /// Build a Hoymiles-flavoured HTTP request: JSON body + browser headers
    /// (Origin / Referer / Accept-Language / User-Agent / language) so the
    /// neapi tier accepts it the same way as the global.hoymiles.com web UI.
    /// </summary>
    private static HttpRequestMessage BuildHoymilesRequest(HttpMethod method, string url, object body)
    {
        var req = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        req.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/147.0.0.0 Safari/537.36");
        req.Headers.TryAddWithoutValidation("Origin", "https://global.hoymiles.com");
        req.Headers.TryAddWithoutValidation("Referer", "https://global.hoymiles.com/");
        req.Headers.TryAddWithoutValidation("language", "en-us");
        return req;
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

            // Origin / Referer matter for Hoymiles API gateway. When user logs in
            // at global.hoymiles.com/website/login, the browser POSTs to
            // neapi.hoymiles.com but with Origin = https://global.hoymiles.com
            // (the page origin, NOT the API host). The server validates that.
            //
            // We mimic the browser:
            //   - If target host is an *.hoymiles.com API server (neapi/euapi/etc),
            //     use https://global.hoymiles.com as Origin / Referer.
            //   - Otherwise derive from the URL host (for fully-custom endpoints).
            if (Uri.TryCreate(fullUrl, UriKind.Absolute, out var uri))
            {
                var isHoymilesApi = uri.Host.EndsWith(".hoymiles.com", StringComparison.OrdinalIgnoreCase)
                    && !uri.Host.Equals("global.hoymiles.com", StringComparison.OrdinalIgnoreCase)
                    && !uri.Host.Equals("world.hoymiles.com", StringComparison.OrdinalIgnoreCase);
                var origin = isHoymilesApi
                    ? "https://global.hoymiles.com"
                    : $"{uri.Scheme}://{uri.Host}";
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
        // Production V3 login path as of mid-2024 = /iam/pub/3/auth/login
        // (verified via browser DevTools on global.hoymiles.com).
        // global.hoymiles.com proxies IAM endpoints behind /platform/api/gateway.
        // Other regional bases (neapi/euapi) serve them at root.
        var path = baseUrl.Contains("global.hoymiles.com", StringComparison.OrdinalIgnoreCase)
            ? "/platform/api/gateway/iam/pub/3/auth/login"
            : "/iam/pub/3/auth/login";
        var url = $"{baseUrl.TrimEnd('/')}{path}";

        // Delegate to TryRawV3LoginAsync so this layer also gets the browser
        // headers + cookie fallback + non-JSON tolerance.
        return await TryRawV3LoginAsync(username, password, url, label, diag);
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
    // Compact host label for diagnostic strings (e.g. "neapi", "global").
    private static string ShortName(string url) =>
        url.Replace("https://", "").Replace("http://", "").Split('/')[0].Split('.')[0];

    // Compact host+path label for diagnostic strings — preserves enough of
    // the path to tell candidate URLs apart (e.g. "neapi:/iam/pub/3/auth/login").
    private static string PathLabel(string url)
    {
        var noProto = url.Replace("https://", "").Replace("http://", "");
        var slash = noProto.IndexOf('/');
        if (slash < 0) return noProto;
        var host = noProto[..slash].Split('.')[0];
        var path = noProto[slash..];
        if (path.Length > 35) path = path[..32] + "...";
        return $"{host}:{path}";
    }

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
                var req = BuildHoymilesRequest(HttpMethod.Post, url, body);
                if (authHeader.HasValue)
                    req.Headers.TryAddWithoutValidation(authHeader.Value.Name, authHeader.Value.Value);

                var resp = await client.SendAsync(req);
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
        var data = await CallApiAsync(token, apiBase ?? DefaultApiBase, "pvm/api/0/station/select_by_page",
            new { page = 1, page_size = 10 });
        if (data == null)
            return (false, "Hoymiles: logged in but station list call failed.");

        return (true, "Connected to Hoymiles S-Miles Cloud!");
    }

    public async Task<PlantInfo?> GetPlantInfoAsync(ApiCredentials credentials)
    {
        var (token, apiBase, _) = await LoginAsync(credentials);
        if (token == null) return null;

        var data = await CallApiAsync(token, apiBase ?? DefaultApiBase, "pvm/api/0/station/select_by_page",
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

        // Try station realtime data (new V3 path used by global.hoymiles.com).
        var sidObj = long.TryParse(credentials.PlantId, out var sidLong) ? (object)sidLong : credentials.PlantId;
        var data = await CallApiAsync(token, apiBase ?? DefaultApiBase,
            "pvm-data/api/0/station/data/count_station_real_data", new { sid = sidObj });
        if (data == null)
        {
            // Fallback: station/find returns aggregates including realtime power
            data = await CallApiAsync(token, apiBase ?? DefaultApiBase, "pvm/api/0/station/find",
                new { id = sidObj });
        }
        if (data == null || data.Value.ValueKind != JsonValueKind.Object) return null;

        var d = data.Value;
        // Hoymiles returns the same metric under different field names across
        // endpoints / firmware versions.  Fall back through the list and take
        // the FIRST non-zero value — never sum.  The old code did
        //     SafeGetDecimal(...) + SafeGetDecimal(...) + SafeGetDecimal(...)
        // which silently summed multiple fields when more than one was
        // populated, producing 2× / 3× inflated kWh on installations where
        // Hoymiles happened to return both `today_eq` and `day_eq`.
        decimal FirstNonZero(params string[] keys)
        {
            foreach (var k in keys)
            {
                var v = SafeGetDecimal(d, k);
                if (v != 0m) return v;
            }
            return 0m;
        }

        var currentPower = FirstNonZero("real_power", "power", "last_data_power");
        var todayEnergy  = FirstNonZero("today_eq", "day_eq", "today_energy");
        var totalEnergy  = FirstNonZero("total_eq", "month_eq_total", "total_energy");
        var capacityW = SafeGetDecimal(d, "installed_capacity");

        // Online detection: power > 0 or explicit status field
        var statusVal = SafeGetInt(d, "status") ?? SafeGetInt(d, "connect_status") ?? -1;
        var isOnline = statusVal == 1 || currentPower > 0;

        // Unit normalisation.  Hoymiles' V2 endpoint reports power in watts
        // and energy in Wh; V3 reports kW and kWh.  No flag tells us which —
        // sniff by magnitude.  Thresholds picked well above any plausible
        // kW/kWh value but well below the same value in W/Wh.
        if (currentPower > 500)        currentPower /= 1000m; // ≥500 kW = impossible residential → was Watts
        if (todayEnergy > 10000)       todayEnergy  /= 1000m; // ≥10,000 kWh/day impossible → was Wh
        if (totalEnergy > 10000000)    totalEnergy  /= 1000m; // ≥10M kWh lifetime impossible → was Wh

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
        var baseUrl = apiBase ?? DefaultApiBase;

        // Hoymiles' V3 cloud has two endpoints that return daily kWh: one
        // per-month (count_eq_by_day_of_month, the FusionSolar-style chart
        // backing the calendar view) and one per-range (count_eq_by_day,
        // covering arbitrary windows).  We try both because:
        //   * count_eq_by_day_of_month is the most reliable — every JS
        //     bundle calls it and the response is straight kWh values.
        //   * count_eq_by_day exists on some tenants but not others.
        //   * count_playback_power_by_day is W-scale data; we keep it as
        //     last-resort fallback in case the others reject.
        //
        // Each endpoint can return one of three response shapes:
        //   1. data: [ {date, eq}, ... ]                (flat list)
        //   2. data: { list: [ {date, eq}, ... ] }      (paginated wrapper)
        //   3. data: { x: ["d1","d2"], y: [v1, v2] }   (parallel arrays —
        //      chart format).  We parse all three.

        // ── Try 1: per-month chunked, count_eq_by_day_of_month ──────────
        var cur = new DateTime(from.Year, from.Month, 1);
        var stop = new DateTime(to.Year, to.Month, 1).AddMonths(1);
        while (cur < stop)
        {
            var monthRecords = await FetchHoymilesMonthAsync(
                token, baseUrl, credentials.PlantId!, cur.Year, cur.Month);
            records.AddRange(monthRecords.Where(r => r.Timestamp >= from.Date && r.Timestamp <= to.Date));
            cur = cur.AddMonths(1);
        }

        if (records.Count == 0)
        {
            // ── Try 2: range, count_eq_by_day ───────────────────────────
            _logger.LogInformation(
                "Hoymiles per-month historical returned empty for {Sid} {From} → {To}; trying range endpoint",
                credentials.PlantId, from.Date, to.Date);
            records.AddRange(await FetchHoymilesRangeAsync(
                token, baseUrl, credentials.PlantId!, from, to,
                "pvm-data/api/0/station/data/count_eq_by_day"));
        }

        if (records.Count == 0)
        {
            // ── Try 3: original playback_power_by_day ───────────────────
            _logger.LogInformation(
                "Hoymiles count_eq_by_day returned empty for {Sid}; trying count_playback_power_by_day",
                credentials.PlantId);
            records.AddRange(await FetchHoymilesRangeAsync(
                token, baseUrl, credentials.PlantId!, from, to,
                "pvm-data/api/0/station/data/count_playback_power_by_day"));
        }

        if (records.Count == 0)
        {
            _logger.LogWarning(
                "Hoymiles GetHistoricalData: ALL endpoints returned 0 records for {Sid} {From} → {To}. " +
                "Check station ownership, API region, or whether the cloud actually has historical data for this window.",
                credentials.PlantId, from.Date, to.Date);
        }
        else
        {
            _logger.LogInformation(
                "Hoymiles GetHistoricalData: collected {Count} day-records for {Sid}",
                records.Count, credentials.PlantId);
        }

        // De-duplicate (in case multiple endpoints returned overlapping dates)
        return records
            .GroupBy(r => r.Timestamp.Date)
            .Select(g => g.OrderByDescending(r => r.EnergyGenerationKwh).First())
            .OrderBy(r => r.Timestamp)
            .ToList();
    }

    /// <summary>
    /// Fetches a single calendar month of daily kWh from Hoymiles via
    /// pvm-data/api/0/station/data/count_eq_by_day_of_month.  Returns one
    /// EnergyRecord per day-of-the-month with non-zero production.
    ///
    /// Body shape comes from reverse-engineering the production S-Miles JS
    /// bundle (account.hoymiles.com main bundle):
    ///     request(url, { data: { sid, date, pb_ver: 1 } })
    /// where date is any yyyy-MM-dd inside the target month.  Earlier
    /// version sent {sid, year, month} which the cloud silently rejects
    /// with an empty response.
    /// </summary>
    private async Task<List<EnergyRecord>> FetchHoymilesMonthAsync(
        string token, string baseUrl, string sid, int year, int month)
    {
        var records = new List<EnergyRecord>();
        var dateStr = $"{year:D4}-{month:D2}-01";
        try
        {
            var data = await CallApiAsync(token, baseUrl,
                "pvm-data/api/0/station/data/count_eq_by_day_of_month",
                new { sid, date = dateStr, pb_ver = 1 });
            if (data == null)
            {
                _logger.LogDebug("Hoymiles count_eq_by_day_of_month {Sid} {Date}: null response", sid, dateStr);
                return records;
            }
            var before = records.Count;
            ParseHoymilesEnergyResponse(data.Value, year, month, records);
            _logger.LogDebug("Hoymiles count_eq_by_day_of_month {Sid} {Date}: parsed {Count} rows",
                sid, dateStr, records.Count - before);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hoymiles count_eq_by_day_of_month failed for {Sid} {Date}", sid, dateStr);
        }
        return records;
    }

    private async Task<List<EnergyRecord>> FetchHoymilesRangeAsync(
        string token, string baseUrl, string sid, DateTime from, DateTime to, string path)
    {
        var records = new List<EnergyRecord>();
        try
        {
            // S-Miles JS bundle body for count_eq_by_day / count_playback_power_by_day:
            //     { sid, date, pb_ver: 1 }     ← date is the END date
            // Some endpoints also accept startDate/endDate; try the
            // single-date shape first (matches the live bundle) and fall
            // back to range shape if that returned empty.
            var data = await CallApiAsync(token, baseUrl, path,
                new { sid, date = to.ToString("yyyy-MM-dd"), pb_ver = 1 });
            if (data != null) ParseHoymilesEnergyResponse(data.Value, anchorYear: 0, anchorMonth: 0, records);

            if (records.Count == 0)
            {
                var data2 = await CallApiAsync(token, baseUrl, path,
                    new
                    {
                        sid,
                        start_date = from.ToString("yyyy-MM-dd"),
                        end_date = to.ToString("yyyy-MM-dd"),
                        pb_ver = 1,
                    });
                if (data2 != null) ParseHoymilesEnergyResponse(data2.Value, anchorYear: 0, anchorMonth: 0, records);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hoymiles {Path} failed for {Sid}", path, sid);
        }
        return records;
    }

    /// <summary>
    /// Parses the three Hoymiles daily-kWh response shapes (flat array,
    /// list-wrapper, parallel x/y arrays) and appends EnergyRecord rows.
    /// anchorYear/Month are used to reconstruct full dates when the chart-
    /// style response only carries day-of-month strings.
    /// </summary>
    private static void ParseHoymilesEnergyResponse(
        JsonElement root, int anchorYear, int anchorMonth, List<EnergyRecord> sink)
    {
        decimal Pick(JsonElement el, params string[] keys)
        {
            foreach (var k in keys)
            {
                var v = SafeGetDecimal(el, k);
                if (v != 0m) return v;
            }
            return 0m;
        }

        // Shape A: top-level array of {date, eq}
        if (root.ValueKind == JsonValueKind.Array)
        {
            AppendFromObjectArray(root, sink);
            return;
        }
        if (root.ValueKind != JsonValueKind.Object) return;

        // Shape B: { list: [...] }
        if (root.TryGetProperty("list", out var listEl) && listEl.ValueKind == JsonValueKind.Array)
        {
            AppendFromObjectArray(listEl, sink);
            return;
        }
        // Shape B2: { data: [...] } – CallApiAsync usually unwraps "data"
        // already, but some endpoints nest it twice.
        if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
        {
            AppendFromObjectArray(dataEl, sink);
            return;
        }

        // Shape C: chart-style parallel arrays { x: [...], y: [...] } —
        // also { xData / yData } or { date: [...], eq: [...] }.
        JsonElement xs = default, ys = default;
        foreach (var (xKey, yKey) in new[] {
            ("x", "y"), ("xAxis", "yAxis"), ("xData", "yData"),
            ("dates", "values"), ("date", "eq"),
        })
        {
            if (root.TryGetProperty(xKey, out var xv) && xv.ValueKind == JsonValueKind.Array
                && root.TryGetProperty(yKey, out var yv) && yv.ValueKind == JsonValueKind.Array)
            {
                xs = xv; ys = yv; break;
            }
        }
        if (xs.ValueKind == JsonValueKind.Array && ys.ValueKind == JsonValueKind.Array)
        {
            var xArr = xs.EnumerateArray().ToArray();
            var yArr = ys.EnumerateArray().ToArray();
            var n = Math.Min(xArr.Length, yArr.Length);
            for (int i = 0; i < n; i++)
            {
                DateTime? date = null;
                if (xArr[i].ValueKind == JsonValueKind.String)
                {
                    var s = xArr[i].GetString();
                    if (DateTime.TryParse(s, out var d)) date = d;
                    else if (int.TryParse(s, out var dom) && anchorYear > 0 && anchorMonth > 0
                             && dom >= 1 && dom <= 31)
                        date = new DateTime(anchorYear, anchorMonth, Math.Min(dom, DateTime.DaysInMonth(anchorYear, anchorMonth)));
                }
                else if (xArr[i].ValueKind == JsonValueKind.Number && xArr[i].TryGetInt32(out var dayN)
                         && anchorYear > 0 && anchorMonth > 0 && dayN >= 1 && dayN <= 31)
                {
                    date = new DateTime(anchorYear, anchorMonth, Math.Min(dayN, DateTime.DaysInMonth(anchorYear, anchorMonth)));
                }
                if (date == null) continue;

                decimal val = 0;
                if (yArr[i].ValueKind == JsonValueKind.Number && yArr[i].TryGetDecimal(out var dn)) val = dn;
                else if (yArr[i].ValueKind == JsonValueKind.String
                         && decimal.TryParse(yArr[i].GetString(), System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var dp)) val = dp;

                if (val > 10000m) val /= 1000m;        // Wh → kWh
                sink.Add(new EnergyRecord { Timestamp = date.Value, EnergyGenerationKwh = val });
            }
            return;
        }

        // Shape D: { yData: { generation: [...], feedin: [...] } } —
        // multi-series chart.
        if (root.TryGetProperty("yData", out var yData) && yData.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("xData", out var xData) && xData.ValueKind == JsonValueKind.Array)
        {
            JsonElement? gen = null;
            foreach (var key in new[] { "generation", "eq", "energy", "pv", "production" })
                if (yData.TryGetProperty(key, out var g) && g.ValueKind == JsonValueKind.Array) { gen = g; break; }
            if (gen != null)
            {
                var xArr = xData.EnumerateArray().ToArray();
                var yArr = gen.Value.EnumerateArray().ToArray();
                var n = Math.Min(xArr.Length, yArr.Length);
                for (int i = 0; i < n; i++)
                {
                    if (xArr[i].ValueKind != JsonValueKind.String) continue;
                    if (!DateTime.TryParse(xArr[i].GetString(), out var date)) continue;
                    if (!yArr[i].TryGetDecimal(out var val)) continue;
                    if (val > 10000m) val /= 1000m;
                    sink.Add(new EnergyRecord { Timestamp = date, EnergyGenerationKwh = val });
                }
            }
        }
    }

    /// <summary>
    /// Adds rows from a JSON array of objects, picking first-non-zero field
    /// across known kWh aliases (eq / energy / day_eq).
    /// </summary>
    private static void AppendFromObjectArray(JsonElement arr, List<EnergyRecord> sink)
    {
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var dateStr = SafeGetString(item, "date")
                       ?? SafeGetString(item, "time")
                       ?? SafeGetString(item, "day");
            if (dateStr == null || !DateTime.TryParse(dateStr, out var date)) continue;

            var energy = SafeGetDecimal(item, "eq");
            if (energy == 0m) energy = SafeGetDecimal(item, "energy");
            if (energy == 0m) energy = SafeGetDecimal(item, "day_eq");
            if (energy == 0m) energy = SafeGetDecimal(item, "value");
            if (energy > 10000m) energy /= 1000m;

            sink.Add(new EnergyRecord
            {
                Timestamp = date,
                EnergyGenerationKwh = energy,
                GridExportKwh = SafeGetDecimal(item, "grid_sell_eq"),
                GridImportKwh = SafeGetDecimal(item, "grid_buy_eq"),
                SelfConsumptionKwh = SafeGetDecimal(item, "self_use_eq"),
            });
        }
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
            var data = await CallApiAsync(token, apiBase ?? DefaultApiBase, "operations/warning/history",
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
            var sidArg = long.TryParse(credentials.PlantId, out var sidL) ? (object)sidL : credentials.PlantId;

            // Try each (path × body-shape) combo until one returns a non-empty
            // result. JS bundle uses `sid` for some endpoints and `id` for
            // others; for `dev/dtu/select_all` the param is `station_id`.
            var attempts = new (string Path, object Body)[]
            {
                ("pvm/api/0/dev/micro/select_by_station",    new { sid = sidArg }),
                ("pvm/api/0/dev/micro/select_by_station",    new { id  = sidArg }),
                ("pvm/api/0/dev/inverter/select_by_station", new { sid = sidArg }),
                ("pvm/api/0/dev/inverter/select_by_station", new { id  = sidArg }),
                ("pvm/api/0/dev/dtu/select_all",             new { station_id = sidArg }),
            };

            JsonElement? data = null;
            var trace = new List<string>();
            foreach (var (path, body) in attempts)
            {
                var bodyKey = body.GetType().GetProperties()[0].Name;
                var r = await CallApiAsync(token, apiBase ?? DefaultApiBase, path, body);
                if (r == null) { trace.Add($"{path}({bodyKey}):null"); continue; }
                if (r.Value.ValueKind == JsonValueKind.Array)
                {
                    var len = r.Value.GetArrayLength();
                    trace.Add($"{path}({bodyKey}):arr[{len}]");
                    if (len > 0) { data = r; break; }
                    continue;
                }
                if (r.Value.ValueKind == JsonValueKind.Object)
                {
                    if (r.Value.TryGetProperty("list", out var l) && l.ValueKind == JsonValueKind.Array)
                    {
                        trace.Add($"{path}({bodyKey}):list[{l.GetArrayLength()}]");
                        if (l.GetArrayLength() > 0) { data = r; break; }
                        continue;
                    }
                    if (r.Value.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array)
                    {
                        trace.Add($"{path}({bodyKey}):data[{d.GetArrayLength()}]");
                        if (d.GetArrayLength() > 0) { data = r; break; }
                        continue;
                    }
                    trace.Add($"{path}({bodyKey}):obj-no-list");
                    continue;
                }
                trace.Add($"{path}({bodyKey}):kind={r.Value.ValueKind}");
            }
            if (data == null)
            {
                _logger.LogWarning("Hoymiles GetDevices: no device-list endpoint returned a non-empty array. Trace: {Trace}", string.Join(" → ", trace));
                return devices;
            }
            _logger.LogInformation("Hoymiles GetDevices trace: {Trace}", string.Join(" → ", trace));

            JsonElement arr;
            if (data.Value.ValueKind == JsonValueKind.Array) arr = data.Value;
            else if (data.Value.TryGetProperty("list", out var l)) arr = l;
            else if (data.Value.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array) arr = d;
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
