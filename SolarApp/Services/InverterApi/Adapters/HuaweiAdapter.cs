using System.Net.Http.Json;
using System.Text.Json;
using SolarApp.Models.Enums;
using SolarApp.Services.InverterApi.Common;
using SolarApp.Services.InverterApi.Models;

namespace SolarApp.Services.InverterApi.Adapters;

/// <summary>
/// Huawei FusionSolar Northbound API adapter.
///
/// Credentials mapping:
///   Username = Northbound API username
///   Password = Northbound API password (systemCode)
///   PlantId  = Station Code (from FusionSolar console)
///
/// Rate limit: ~1 req/min enforced strictly by Huawei.
/// </summary>
public class HuaweiAdapter : IInverterApiAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HuaweiAdapter> _logger;
    private const string BaseUrl = "https://sg5.fusionsolar.huawei.com/thirdData";

    // Static station-list cache across instances (keyed by credentials fingerprint).
    // Huawei NBI has a hard 1-req/minute rate limit, so repeatedly calling
    // /getStationList (from TestConnection → PlantInfo → ...) quickly trips it.
    // This cache is intentionally short (90s) — longer than typical UI test flow,
    // shorter than the scheduled sync interval.
    private static readonly Dictionary<string, (DateTime Expiry, string RawBody)> _stationListCache = new();
    private static readonly object _cacheLock = new();
    private static readonly TimeSpan StationListTtl = TimeSpan.FromSeconds(90);
    private static DateTime _lastStationCacheSweepUtc = DateTime.MinValue;

    // Opportunistic cleanup so the static dict doesn't grow unbounded when
    // credentials rotate. Called whenever the cache lock is already held.
    private static void EvictExpired_NoLock()
    {
        var now = DateTime.UtcNow;
        if (now - _lastStationCacheSweepUtc < TimeSpan.FromMinutes(5)) return;
        _lastStationCacheSweepUtc = now;
        var stale = _stationListCache
            .Where(kv => kv.Value.Expiry <= now)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var k in stale) _stationListCache.Remove(k);
    }

    public InverterBrand Brand => InverterBrand.Huawei;

    public HuaweiAdapter(IHttpClientFactory httpClientFactory, ILogger<HuaweiAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Maps Huawei NBI station-level real_health_state to our normalised
    /// HealthState + IsOnline pair.
    /// Reference: FusionSolar Northbound Interface Reference, getStationRealKpi —
    /// real_health_state: 1 = disconnected, 2 = faulty, 3 = healthy.
    ///
    /// This used to read 2 as healthy and 3 as "offline". Every Huawei site
    /// on the account then spent a week logged as offline while producing
    /// daily — none was ever recorded ONLINE — and the moment the daytime
    /// silence check started working it raised "Configuration Issue" on all
    /// of them. The code at 1 ("empirically grid loss or comms loss" in the
    /// sync job) fits "disconnected", not "faulty".
    ///
    /// A station with energy produced today, in daylight, is treated as
    /// running whatever the code says, so a document mismatch can never
    /// again turn a working plant into an outage.
    /// </summary>
    private static (HealthState Health, bool IsOnline) MapHuaweiHealth(decimal raw, decimal dayPowerKwh = 0)
    {
        var code = (int)Math.Round(raw);
        var mapped = code switch
        {
            3 => (HealthState.Healthy,      true),
            2 => (HealthState.Faulty,       true),   // communicating, reporting a fault
            1 => (HealthState.Disconnected, false),
            0 => (HealthState.Disconnected, false),
            _ => (HealthState.Unknown,      false),
        };
        if (!mapped.Item2 && dayPowerKwh > 0)
        {
            var hour = SolarApp.Services.SiteTime.Now.Hour;
            if (hour >= 7 && hour < 18) return (HealthState.Healthy, true);
        }
        return mapped;
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(ApiCredentials credentials)
    {
        if (string.IsNullOrEmpty(credentials.Username))
            return (false, "Username (Northbound API user) is required");
        if (string.IsNullOrEmpty(credentials.Password))
            return (false, "Password (systemCode) is required");

        // Friendly rate-gate awareness: if a sync just happened, login itself
        // will burn the budget. Tell the user to wait instead of producing a
        // confusing "Login failed: rate gate busy" error.
        var accountKey = HuaweiRateGate.AccountKey(credentials.Username);
        var preWait = HuaweiRateGate.GetRequiredWait(accountKey);
        if (preWait > TimeSpan.FromSeconds(15))
        {
            return (false,
                $"Huawei rate gate is busy — last sync used the per-account budget. " +
                $"Wait ~{preWait.TotalSeconds:0}s and retry. " +
                "(Huawei NBI permits ~1 request/minute per account; this is normal right after a sync cycle.)");
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            var (token, loginError) = await LoginWithDetailAsync(client, credentials);
            if (string.IsNullOrEmpty(token))
                return (false, $"Huawei login failed: {loginError}");

            // Verify token by listing stations
            client.DefaultRequestHeaders.Add("XSRF-TOKEN", token);
            // Huawei API requires pageSize >= 50 on /getStationList (enforced server-side)
            var response = await client.PostAsJsonAsync($"{BaseUrl}/getStationList",
                new { pageNo = 1, pageSize = 50 });
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return (false, $"Huawei HTTP {(int)response.StatusCode}: {Truncate(body, 300)}");

            var json = JsonDocument.Parse(body).RootElement;
            var failCode = SafeGetInt(json, "failCode");
            if (failCode.HasValue && failCode.Value != 0)
            {
                var msg = SafeGetString(json, "message") ?? SafeGetString(json, "data");
                return (false, $"Huawei error code={failCode}: {msg}");
            }

            return (true, "Connected to Huawei FusionSolar!");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Huawei TestConnection failed");
            return (false, $"Huawei error: {ex.Message}");
        }
    }

    public async Task<PlantInfo?> GetPlantInfoAsync(ApiCredentials credentials)
    {
        _lastPlantLookupError = null;
        try
        {
            var cacheKey = $"{credentials.Username}|{credentials.Password}";
            string? body = null;

            // Try cache first (avoids Huawei 1-req/min rate limit on rapid re-calls)
            lock (_cacheLock)
            {
                if (_stationListCache.TryGetValue(cacheKey, out var cached)
                    && cached.Expiry > DateTime.UtcNow)
                    body = cached.RawBody;
            }

            if (body == null)
            {
                var client = _httpClientFactory.CreateClient();
                var (token, loginErr) = await LoginWithDetailAsync(client, credentials);
                if (token == null)
                {
                    _lastPlantLookupError = $"Login failed: {loginErr}. " +
                        "(Huawei NBI has 1-req/min rate limit — if you just ran Test Connection, wait 60 seconds.)";
                    return null;
                }

                client.DefaultRequestHeaders.Add("XSRF-TOKEN", token);
                var response = await client.PostAsJsonAsync($"{BaseUrl}/getStationList",
                    new { pageNo = 1, pageSize = 50 });
                body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    _lastPlantLookupError = $"getStationList HTTP {(int)response.StatusCode}: {Truncate(body, 300)}";
                    return null;
                }

                // Don't cache rate-limited or error responses
                try
                {
                    var fc = SafeGetInt(JsonDocument.Parse(body).RootElement, "failCode");
                    if (!fc.HasValue || fc.Value == 0)
                    {
                        lock (_cacheLock)
                        {
                            _stationListCache[cacheKey] = (DateTime.UtcNow.Add(StationListTtl), body);
                            EvictExpired_NoLock();
                        }
                    }
                }
                catch { /* ignore parse issues here, handled below */ }
            }

            // Check failCode even on 200 — Huawei returns rate-limit 407 with HTTP 200
            try
            {
                var fcCheck = JsonDocument.Parse(body).RootElement;
                var fc = SafeGetInt(fcCheck, "failCode");
                if (fc.HasValue && fc.Value != 0)
                {
                    var apiMsg = SafeGetString(fcCheck, "message") ?? SafeGetString(fcCheck, "data") ?? "Unknown";
                    _lastPlantLookupError = $"Huawei error failCode={fc.Value}: {apiMsg}. " +
                        (fc.Value == 407 ? "(Rate limit — Huawei allows 1 request/minute. Wait 60s and retry.)"
                         : fc.Value == 305 ? "(Token expired — retry)"
                         : fc.Value == 401 ? "(Unauthorized — check Northbound credentials)"
                         : "");
                    return null;
                }
            }
            catch { /* fall through to main parse below */ }

            JsonElement? json;
            try { json = JsonDocument.Parse(body).RootElement; }
            catch (Exception jsonEx)
            {
                _lastPlantLookupError = $"Failed to parse Huawei response: {jsonEx.Message}. Body: {Truncate(body, 200)}";
                return null;
            }

            // Huawei NBI returns TWO known shapes:
            //   Shape A (older):  {data: [...]}
            //   Shape B (current, v6): {data: {list: [...], total: ..., pageSize: ...}}
            JsonElement stationsElement = default;
            if (json.Value.TryGetProperty("data", out var data))
            {
                if (data.ValueKind == JsonValueKind.Array)
                    stationsElement = data;
                else if (data.ValueKind == JsonValueKind.Object
                         && data.TryGetProperty("list", out var inner)
                         && inner.ValueKind == JsonValueKind.Array)
                    stationsElement = inner;
            }
            if (stationsElement.ValueKind != JsonValueKind.Array)
            {
                _lastPlantLookupError = "Huawei returned no station list. " +
                    "Either your Northbound account has no stations assigned, or the response shape changed.";
                return null;
            }

            var stations = stationsElement.EnumerateArray()
                .Where(s => s.ValueKind == JsonValueKind.Object)
                .ToList();
            if (stations.Count == 0)
            {
                _lastPlantLookupError = "Your Huawei Northbound account has zero stations assigned. " +
                    "Ask your Huawei Northbound admin to grant access to at least one station.";
                return null;
            }

            // Match PlantId against stationCode (primary) and plantCode (fallback).
            // Try both exact match AND with 'NE=' prefix auto-added, since Huawei
            // station codes have format 'NE=123456' but users often copy just the
            // number from the browser URL.
            JsonElement? match = null;
            string? actualStationCode = null;
            if (!string.IsNullOrEmpty(credentials.PlantId))
            {
                var configured = credentials.PlantId.Trim();
                var configuredWithPrefix = configured.StartsWith("NE=", StringComparison.OrdinalIgnoreCase)
                    ? configured
                    : "NE=" + configured;

                foreach (var s in stations)
                {
                    var code = SafeGetString(s, "stationCode")
                            ?? SafeGetString(s, "plantCode");
                    if (code == null) continue;

                    if (string.Equals(code, configured, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(code, configuredWithPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        match = s;
                        actualStationCode = code;
                        break;
                    }
                }
            }

            if (match == null)
            {
                var available = string.Join(", ", stations.Take(10).Select(s =>
                    $"{SafeGetString(s, "stationCode") ?? "?"} ({SafeGetString(s, "stationName") ?? "?"})"));
                _logger.LogWarning(
                    "Huawei: configured PlantId '{Configured}' not found in account. Available: {Available}",
                    credentials.PlantId, available);
                _lastPlantLookupError = string.IsNullOrEmpty(credentials.PlantId)
                    ? $"PlantId/Station Code is empty. Available stations on this account: {available}"
                    : $"Configured PlantId '{credentials.PlantId}' is NOT in this Huawei account. " +
                      $"Huawei station codes start with 'NE=' — if you copied the number from the browser URL, " +
                      $"use the full code. Available: {available}";
                return null;
            }

            _lastPlantLookupError = null;
            // If user had bare number, remember the full form for the realtime call
            if (!string.Equals(actualStationCode, credentials.PlantId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "Huawei: auto-expanded PlantId '{From}' → '{To}' (match on stationCode)",
                    credentials.PlantId, actualStationCode);
            }
            return new PlantInfo
            {
                PlantId = actualStationCode ?? "",
                PlantName = SafeGetString(match.Value, "stationName")
                           ?? SafeGetString(match.Value, "plantName") ?? "",
                // Huawei NBI reports station capacity in MEGAwatts, not kW.
                // Storing it raw produced absurd values (a 10 kW site came
                // back as "0.011 kWp", i.e. 11 watts) which then fed into
                // Performance Ratio, specific yield and the sync sanity
                // ceiling — all silently wrong.  Convert MW → kW.
                CapacityKw = SafeGetDecimal(match.Value, "capacity") * 1000m,
                Address = SafeGetString(match.Value, "stationAddr")
                         ?? SafeGetString(match.Value, "plantAddress")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Huawei GetPlantInfo failed");
            return null;
        }
    }

    // Cache error detail from last call for ApiResult translation
    private string? _lastRealtimeError;
    private int _lastRealtimeHttpCode;
    private string? _lastPlantLookupError;

    public Task<RealtimeData?> GetRealtimeDataAsync(ApiCredentials credentials)
    {
        // Huawei NBI: 1 req/min per account. The retry-after-62s pattern used
        // to live here, but it blocked the entire sync loop for >1 minute on
        // every rate-limit hit. The HuaweiRateGate now handles spacing globally
        // — if the window is busy, the call returns RateLimited fast and the
        // next sync cycle picks it up.
        return GetRealtimeDataInternalAsync(credentials);
    }

    private async Task<RealtimeData?> GetRealtimeDataInternalAsync(ApiCredentials credentials)
    {
        _lastRealtimeError = null;
        _lastRealtimeHttpCode = 0;

        if (string.IsNullOrEmpty(credentials.PlantId))
        {
            _lastRealtimeError = "PlantId (Station Code) is empty in installation settings.";
            return null;
        }

        // ── Rate gate: skip fast if the per-account 1 req/min window is busy ──
        var accountKey = HuaweiRateGate.AccountKey(credentials.Username);
        var (defer, wait) = HuaweiRateGate.Decide(accountKey);
        if (defer)
        {
            _lastRealtimeError = $"[RATE LIMITED] Skipped — Huawei NBI 1 req/min window busy " +
                $"(next allowed in {wait.TotalSeconds:0.0}s). Will retry next sync cycle.";
            return null;
        }
        if (wait > TimeSpan.Zero) await Task.Delay(wait);

        try
        {
            var client = _httpClientFactory.CreateClient();
            var (token, loginError) = await LoginWithDetailAsync(client, credentials);
            if (token == null)
            {
                _lastRealtimeError = $"Login failed: {loginError}";
                return null;
            }

            // Huawei station codes have the 'NE=' prefix. Auto-add if user stored
            // just the number (matching the PlantInfo lookup behaviour).
            var stationCode = credentials.PlantId!.Trim();
            if (!stationCode.StartsWith("NE=", StringComparison.OrdinalIgnoreCase))
                stationCode = "NE=" + stationCode;

            client.DefaultRequestHeaders.Add("XSRF-TOKEN", token);
            HuaweiRateGate.MarkCalled(accountKey);
            var response = await client.PostAsJsonAsync($"{BaseUrl}/getStationRealKpi",
                new { stationCodes = stationCode });
            _lastRealtimeHttpCode = (int)response.StatusCode;

            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _lastRealtimeError = $"getStationRealKpi HTTP {_lastRealtimeHttpCode}: {Truncate(body, 400)}";
                return null;
            }

            JsonElement json;
            try { json = JsonDocument.Parse(body).RootElement; }
            catch (Exception je)
            {
                _lastRealtimeError = $"Invalid JSON from Huawei: {je.Message}. Body: {Truncate(body, 200)}";
                return null;
            }

            // Huawei may return success=false with a failCode
            var failCode = SafeGetInt(json, "failCode");
            if (failCode.HasValue && failCode.Value != 0)
            {
                // Invalidate cached token on auth-expired codes so next call re-logins
                if (failCode.Value == 305 || failCode.Value == 401)
                {
                    var cacheKey = $"{credentials.Username}|{credentials.Password?.Length ?? 0}";
                    InvalidateToken(cacheKey);
                }

                // Real 407 from server — back off harder so we stop self-poking the rate limit
                if (failCode.Value == 407)
                    HuaweiRateGate.RecordRateLimitResponse(accountKey);

                var msg = SafeGetString(json, "message") ?? SafeGetString(json, "data") ?? "Unknown";
                _lastRealtimeError = $"Huawei error failCode={failCode.Value}: {msg}. " +
                    (failCode.Value == 407 ? "(Rate limit — Huawei NBI allows 1 request/minute. Wait and retry.)"
                     : failCode.Value == 305 ? "(Token expired — will auto-retry)"
                     : failCode.Value == 401 ? "(Unauthorized — check Northbound API credentials)"
                     : failCode.Value == 20002 ? "(PlantId/StationCode not found in this account)"
                     : "");
                return null;
            }

            if (!json.TryGetProperty("data", out var dataArr))
            {
                _lastRealtimeError = $"Unexpected response shape (no 'data' key). Body: {Truncate(body, 300)}";
                return null;
            }

            // data:null with success:true means: API OK, but no realtime data available
            // for this station (device offline, night, or freshly commissioned).
            // This is NOT an API error — report device as offline with zero values.
            if (dataArr.ValueKind == JsonValueKind.Null)
            {
                _logger.LogInformation(
                    "Huawei station {StationCode}: API success but data=null — treating as device offline",
                    credentials.PlantId);
                return new RealtimeData
                {
                    Timestamp = DateTime.UtcNow,
                    CurrentPowerKw = 0,
                    TodayEnergyKwh = 0,
                    TotalEnergyKwh = 0,
                    IsOnline = false
                };
            }

            if (dataArr.ValueKind != JsonValueKind.Array)
            {
                _lastRealtimeError = $"Unexpected 'data' type ({dataArr.ValueKind}). Body: {Truncate(body, 300)}";
                return null;
            }

            var first = dataArr.EnumerateArray().FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Object)
            {
                _lastRealtimeError = $"No data returned for PlantId '{credentials.PlantId}'. " +
                    "Check that this Station Code is valid and accessible by this account.";
                return null;
            }

            if (!first.TryGetProperty("dataItemMap", out var kpi)
                || kpi.ValueKind != JsonValueKind.Object)
            {
                _lastRealtimeError = "Response missing 'dataItemMap'. Body: " + Truncate(body, 300);
                return null;
            }

            var health = SafeGetDecimal(kpi, "real_health_state");

            // Huawei NBI station-level KPI semantics:
            //   day_power   = today's accumulated energy (kWh) — NOT instantaneous power
            //   total_power = lifetime accumulated energy (kWh)
            //   real_health_state:
            //     0 = disconnect (no cloud comm at all)
            //     1 = faulty  (Grid Loss / Insulation / OverTemp — STILL needs alert!)
            //     2 = healthy (the only "good" state)
            //     3 = offline (expected at night / device sleeping)
            //
            // IMPORTANT: We deliberately SKIP device-level active_power fetch here
            // because Huawei NBI enforces 1-request-per-minute rate limit. Extra
            // calls /getDevList + /getDevRealKpi would exhaust the quota.
            // CurrentPowerKw is left at 0 — Dashboard can derive approximate power
            // from day_power delta between sync cycles.
            var (mappedHealth, isOnline) = MapHuaweiHealth(health, SafeGetDecimal(kpi, "day_power"));
            return new RealtimeData
            {
                Timestamp = DateTime.UtcNow,
                CurrentPowerKw = 0,
                TodayEnergyKwh = SafeGetDecimal(kpi, "day_power"),
                TotalEnergyKwh = SafeGetDecimal(kpi, "total_power"),
                IsOnline = isOnline,
                Health = mappedHealth,
                FaultReason = mappedHealth == HealthState.Faulty
                    ? $"Huawei reports station faulty (real_health_state={health:0}). Open FusionSolar to view the specific alarm — the actual alarm code/name will be ingested via getAlarmList on the next sync round and surfaced as its own alert."
                    : null
            };
        }
        catch (TaskCanceledException)
        {
            _lastRealtimeError = "Request timed out — Huawei cloud did not respond in 30s.";
            return null;
        }
        catch (HttpRequestException ex)
        {
            _lastRealtimeError = $"Network error: {ex.Message}";
            return null;
        }
        catch (Exception ex)
        {
            _lastRealtimeError = $"{ex.GetType().Name}: {ex.Message}";
            _logger.LogError(ex, "Huawei GetRealtimeData failed");
            return null;
        }
    }

    /// <summary>
    /// Outcome classification for batch realtime fetch — lets the caller distinguish
    /// "we deferred to respect rate limit" from "the API actually rejected us".
    /// </summary>
    public enum BatchOutcome
    {
        Success,          // API returned 0+ stations; check per-station data
        Deferred,         // Rate gate busy, no call made — retry next cycle
        AuthFailed,       // 305/401 or login failed — credentials likely stale
        RateLimitedByServer, // Real 407 from Huawei
        ServerError,      // 5xx, parse failure, network error
    }

    public class HuaweiBatchRealtimeResult
    {
        public BatchOutcome Outcome { get; set; }
        public string? ErrorDetail { get; set; }
        public Dictionary<string, RealtimeData?> Stations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fetches realtime KPI for MULTIPLE station codes in a single Huawei API call.
    /// Huawei's getStationRealKpi accepts comma-separated stationCodes, returning
    /// an array with one entry per station. This drastically reduces API calls when
    /// multiple installations share the same Northbound account.
    /// </summary>
    public async Task<HuaweiBatchRealtimeResult> GetBatchRealtimeDataAsync(
        ApiCredentials credentials, List<string> plantIds)
    {
        var result = new HuaweiBatchRealtimeResult();
        foreach (var pid in plantIds) result.Stations[pid] = null;

        if (plantIds.Count == 0)
        {
            result.Outcome = BatchOutcome.Success;
            return result;
        }

        var accountKey = HuaweiRateGate.AccountKey(credentials.Username);
        var (defer, wait) = HuaweiRateGate.Decide(accountKey);
        if (defer)
        {
            _logger.LogInformation(
                "Huawei batch realtime: deferred {Count} stations — rate gate busy ({Wait:0.0}s)",
                plantIds.Count, wait.TotalSeconds);
            result.Outcome = BatchOutcome.Deferred;
            result.ErrorDetail = $"Rate gate busy, retry in {wait.TotalSeconds:0.0}s";
            return result;
        }
        if (wait > TimeSpan.Zero) await Task.Delay(wait);

        try
        {
            var client = _httpClientFactory.CreateClient();
            var (token, loginError) = await LoginWithDetailAsync(client, credentials);
            if (token == null)
            {
                _logger.LogWarning("Huawei batch realtime: login failed: {Err}", loginError);
                result.Outcome = BatchOutcome.AuthFailed;
                result.ErrorDetail = loginError;
                return result;
            }

            // Normalize all station codes to NE= prefix (uppercase) and join
            var normalizedCodes = plantIds.Select(p =>
            {
                var code = p.Trim();
                return code.StartsWith("NE=", StringComparison.OrdinalIgnoreCase)
                    ? "NE=" + code[3..]   // canonicalize prefix to uppercase
                    : "NE=" + code;
            }).ToList();
            var combinedCodes = string.Join(",", normalizedCodes);

            client.DefaultRequestHeaders.Add("XSRF-TOKEN", token);
            HuaweiRateGate.MarkCalled(accountKey);
            var response = await client.PostAsJsonAsync($"{BaseUrl}/getStationRealKpi",
                new { stationCodes = combinedCodes });

            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Huawei batch realtime HTTP {Code}", (int)response.StatusCode);
                result.Outcome = BatchOutcome.ServerError;
                result.ErrorDetail = $"HTTP {(int)response.StatusCode}";
                return result;
            }

            JsonElement json;
            try { json = JsonDocument.Parse(body).RootElement; }
            catch (Exception je)
            {
                result.Outcome = BatchOutcome.ServerError;
                result.ErrorDetail = $"Parse error: {je.Message}";
                return result;
            }

            var failCode = SafeGetInt(json, "failCode");
            if (failCode.HasValue && failCode.Value != 0)
            {
                var msg = SafeGetString(json, "message") ?? "";
                if (failCode.Value == 407)
                {
                    HuaweiRateGate.RecordRateLimitResponse(accountKey);
                    result.Outcome = BatchOutcome.RateLimitedByServer;
                }
                else if (failCode.Value == 305 || failCode.Value == 401)
                {
                    InvalidateToken($"{credentials.Username}|{credentials.Password?.Length ?? 0}");
                    result.Outcome = BatchOutcome.AuthFailed;
                }
                else
                {
                    result.Outcome = BatchOutcome.ServerError;
                }
                result.ErrorDetail = $"failCode={failCode.Value}: {msg}";
                _logger.LogWarning("Huawei batch realtime failCode={Code} {Msg}", failCode.Value, msg);
                return result;
            }

            // Success path — populate per-station data
            result.Outcome = BatchOutcome.Success;

            if (!json.TryGetProperty("data", out var dataArr)) return result;
            if (dataArr.ValueKind == JsonValueKind.Null) return result;
            if (dataArr.ValueKind != JsonValueKind.Array) return result;

            // Build lookup from normalized code → original PlantId
            var codeToPlantId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < plantIds.Count; i++)
                codeToPlantId[normalizedCodes[i]] = plantIds[i];

            foreach (var item in dataArr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var stationCode = SafeGetString(item, "stationCode") ?? "";
                if (!codeToPlantId.TryGetValue(stationCode, out var originalPlantId))
                {
                    // Try matching without NE= prefix (case-insensitive)
                    var stripped = stationCode.StartsWith("NE=", StringComparison.OrdinalIgnoreCase)
                        ? stationCode[3..] : stationCode;
                    originalPlantId = plantIds.FirstOrDefault(p =>
                        p.Trim().Equals(stripped, StringComparison.OrdinalIgnoreCase)
                        || p.Trim().Equals(stationCode, StringComparison.OrdinalIgnoreCase));
                    if (originalPlantId == null) continue;
                }

                if (!item.TryGetProperty("dataItemMap", out var kpi)
                    || kpi.ValueKind != JsonValueKind.Object) continue;

                var health = SafeGetDecimal(kpi, "real_health_state");
                var (mappedHealth, isOnline) = MapHuaweiHealth(health, SafeGetDecimal(kpi, "day_power"));
                result.Stations[originalPlantId] = new RealtimeData
                {
                    Timestamp = DateTime.UtcNow,
                    CurrentPowerKw = 0,
                    TodayEnergyKwh = SafeGetDecimal(kpi, "day_power"),
                    TotalEnergyKwh = SafeGetDecimal(kpi, "total_power"),
                    IsOnline = isOnline,
                    Health = mappedHealth,
                    FaultReason = mappedHealth == HealthState.Faulty
                        ? $"Huawei reports station faulty (real_health_state={health:0}). Open FusionSolar to view the specific alarm — the actual alarm code/name will be ingested via getAlarmList on the next sync round and surfaced as its own alert."
                        : null
                };
            }

            _logger.LogInformation(
                "Huawei batch realtime: {Success}/{Total} stations returned data in 1 API call",
                result.Stations.Count(r => r.Value != null), plantIds.Count);
        }
        catch (TaskCanceledException)
        {
            result.Outcome = BatchOutcome.ServerError;
            result.ErrorDetail = "Request timed out";
        }
        catch (HttpRequestException hre)
        {
            result.Outcome = BatchOutcome.ServerError;
            result.ErrorDetail = $"Network error: {hre.Message}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Huawei GetBatchRealtimeData failed");
            result.Outcome = BatchOutcome.ServerError;
            result.ErrorDetail = $"{ex.GetType().Name}: {ex.Message}";
        }
        return result;
    }

    public async Task<List<EnergyRecord>> GetHistoricalDataAsync(
        ApiCredentials credentials, DateTime from, DateTime to)
    {
        var records = new List<EnergyRecord>();
        if (string.IsNullOrEmpty(credentials.PlantId)) return records;

        var accountKey = HuaweiRateGate.AccountKey(credentials.Username);

        try
        {
            var client = _httpClientFactory.CreateClient();
            var token = await LoginAsync(client, credentials);
            if (token == null)
            {
                _logger.LogWarning("Huawei GetHistoricalData: login returned null token");
                return records;
            }
            client.DefaultRequestHeaders.Add("XSRF-TOKEN", token);

            // Use getKpiStationMonth — returns 30 daily-aggregate rows in ONE
            // call (collectTime = any timestamp within the target month).
            // Previous version used getKpiStationDay, which returns 24
            // HOURLY rows for a single day — wrong granularity AND ate the
            // 1-req/min rate budget at 365 calls per backfill (would take
            // 6 hours).  Month iteration: 12 calls per year, completes in
            // 12 minutes even at strict rate limit.
            //
            // Reference: Huawei FusionSolar NBI v6 docs §4.2.4
            //   /getKpiStationMonth → dataItemMap.day_power (kWh per day)

            var cur = new DateTime(from.Year, from.Month, 1);
            var stop = new DateTime(to.Year, to.Month, 1).AddMonths(1);
            while (cur < stop)
            {
                var (defer, wait) = HuaweiRateGate.Decide(accountKey);
                if (defer)
                {
                    _logger.LogInformation(
                        "Huawei historical: deferring remaining months starting {Date} — rate gate busy ({Wait:0.0}s)",
                        cur, wait.TotalSeconds);
                    break;
                }
                if (wait > TimeSpan.Zero) await Task.Delay(wait);

                try
                {
                    var collectTime = new DateTimeOffset(cur, TimeSpan.Zero).ToUnixTimeMilliseconds();
                    HuaweiRateGate.MarkCalled(accountKey);
                    var response = await client.PostAsJsonAsync($"{BaseUrl}/getKpiStationMonth",
                        new { stationCodes = credentials.PlantId, collectTime });

                    if (!response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        _logger.LogWarning(
                            "Huawei getKpiStationMonth HTTP {Status} for {Date} body={Body}",
                            (int)response.StatusCode, cur, Truncate(body, 200));
                        cur = cur.AddMonths(1);
                        continue;
                    }

                    var json = await ReadJsonAsync(response);
                    if (json == null)
                    {
                        cur = cur.AddMonths(1);
                        continue;
                    }

                    // failCode 407 = rate limit; extend cooldown and retry next month later
                    var fail = SafeGetInt(json.Value, "failCode");
                    if (fail == 407)
                    {
                        HuaweiRateGate.RecordRateLimitResponse(accountKey);
                        _logger.LogInformation("Huawei getKpiStationMonth 407 rate-limit for {Date}", cur);
                        await Task.Delay(2000);
                        cur = cur.AddMonths(1);
                        continue;
                    }
                    if (fail.HasValue && fail.Value != 0)
                    {
                        var msg = SafeGetString(json.Value, "message")
                                ?? SafeGetString(json.Value, "data");
                        _logger.LogWarning("Huawei getKpiStationMonth failCode={Fail} message={Msg} for {Date}",
                            fail, msg, cur);
                        cur = cur.AddMonths(1);
                        continue;
                    }

                    if (json.Value.TryGetProperty("data", out var dataArr)
                        && dataArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in dataArr.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.Object) continue;
                            if (!item.TryGetProperty("dataItemMap", out var kpi)
                                || kpi.ValueKind != JsonValueKind.Object) continue;

                            var ct = SafeGetLong(item, "collectTime");
                            if (ct == null) continue;

                            // First non-zero across known kWh aliases.
                            var energy = SafeGetDecimal(kpi, "day_power");
                            if (energy == 0m) energy = SafeGetDecimal(kpi, "inverter_power");
                            if (energy == 0m) energy = SafeGetDecimal(kpi, "energy");

                            records.Add(new EnergyRecord
                            {
                                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(ct.Value).UtcDateTime,
                                EnergyGenerationKwh = energy,
                                GridExportKwh = SafeGetDecimal(kpi, "ongrid_power"),
                                SelfConsumptionKwh = SafeGetDecimal(kpi, "selfuse_power"),
                            });
                        }
                    }
                }
                catch (Exception exMonth)
                {
                    _logger.LogWarning(exMonth, "Huawei month {Date} skipped", cur);
                }

                cur = cur.AddMonths(1);
                await Task.Delay(500);
            }

            _logger.LogInformation(
                "Huawei GetHistoricalData: collected {Count} day-records for {Sid} {From} → {To}",
                records.Count, credentials.PlantId, from.Date, to.Date);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Huawei GetHistoricalData failed");
        }
        return records;
    }

    /// <summary>
    /// Fetches alarms for MULTIPLE stations in one /getAlarmList call.
    /// Returns a dictionary keyed by the original PlantId.
    /// </summary>
    public async Task<Dictionary<string, List<AlarmRecord>>> GetBatchAlarmsAsync(
        ApiCredentials credentials, List<string> plantIds, DateTime from, DateTime to)
    {
        var byStation = new Dictionary<string, List<AlarmRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pid in plantIds) byStation[pid] = new List<AlarmRecord>();

        if (plantIds.Count == 0) return byStation;

        var accountKey = HuaweiRateGate.AccountKey(credentials.Username);
        var (defer, wait) = HuaweiRateGate.Decide(accountKey);
        if (defer) return byStation;
        if (wait > TimeSpan.Zero) await Task.Delay(wait);

        try
        {
            var client = _httpClientFactory.CreateClient();
            var token = await LoginAsync(client, credentials);
            if (token == null) return byStation;

            var normalizedCodes = plantIds.Select(p =>
            {
                var code = p.Trim();
                return code.StartsWith("NE=", StringComparison.OrdinalIgnoreCase)
                    ? "NE=" + code[3..] : "NE=" + code;
            }).ToList();
            var combinedCodes = string.Join(",", normalizedCodes);

            client.DefaultRequestHeaders.Add("XSRF-TOKEN", token);
            var beginTime = new DateTimeOffset(from).ToUnixTimeMilliseconds();
            var endTime = new DateTimeOffset(to).ToUnixTimeMilliseconds();
            HuaweiRateGate.MarkCalled(accountKey);
            var response = await client.PostAsJsonAsync($"{BaseUrl}/getAlarmList",
                new { stationCodes = combinedCodes, beginTime, endTime, pageNo = 1, pageSize = 200 });
            if (!response.IsSuccessStatusCode) return byStation;

            var json = await ReadJsonAsync(response);
            if (json == null) return byStation;

            var failCode = SafeGetInt(json.Value, "failCode");
            if (failCode.HasValue && failCode.Value == 407)
            {
                HuaweiRateGate.RecordRateLimitResponse(accountKey);
                return byStation;
            }

            if (!json.Value.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object) return byStation;
            if (!data.TryGetProperty("list", out var list)
                || list.ValueKind != JsonValueKind.Array) return byStation;

            // Lookup from station code → original plantId
            var codeToPlantId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < plantIds.Count; i++)
                codeToPlantId[normalizedCodes[i]] = plantIds[i];

            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var stationCode = SafeGetString(item, "stationCode") ?? "";
                if (!codeToPlantId.TryGetValue(stationCode, out var originalPlantId))
                {
                    var stripped = stationCode.StartsWith("NE=", StringComparison.OrdinalIgnoreCase)
                        ? stationCode[3..] : stationCode;
                    originalPlantId = plantIds.FirstOrDefault(p =>
                        p.Trim().Equals(stripped, StringComparison.OrdinalIgnoreCase)
                        || p.Trim().Equals(stationCode, StringComparison.OrdinalIgnoreCase));
                    if (originalPlantId == null) continue;
                }

                var raiseTime = SafeGetLong(item, "raiseTime");
                byStation[originalPlantId].Add(new AlarmRecord
                {
                    AlarmId = SafeGetString(item, "alarmId") ?? "",
                    AlarmName = SafeGetString(item, "alarmName") ?? "",
                    // FusionSolar NBI names the level field "lev"; reading only
                    // "severity" meant every alarm fell to the ?? 3 default, so a
                    // critical inverter fault arrived labelled Medium.
                    Severity = SafeGetInt(item, "severity") ?? SafeGetInt(item, "lev") ?? 3,
                    OccurredAt = raiseTime.HasValue
                        ? DateTimeOffset.FromUnixTimeMilliseconds(raiseTime.Value).UtcDateTime
                        : DateTime.UtcNow,
                    DeviceName = SafeGetString(item, "devName"),
                    AlarmCode = SafeGetString(item, "causeId") ?? SafeGetString(item, "alarmType"),
                    Description = SafeGetString(item, "alarmCause")
                });
            }

            _logger.LogInformation(
                "Huawei batch alarms: {Total} alarms across {Stations} stations in 1 API call",
                byStation.Sum(x => x.Value.Count), plantIds.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Huawei GetBatchAlarms failed");
        }
        return byStation;
    }

    public async Task<List<AlarmRecord>> GetAlarmsAsync(
        ApiCredentials credentials, DateTime from, DateTime to)
    {
        var alarms = new List<AlarmRecord>();
        if (string.IsNullOrEmpty(credentials.PlantId)) return alarms;

        // Rate gate: skip silently if the window is busy. Alarms are non-critical;
        // missing one cycle is fine.
        var accountKey = HuaweiRateGate.AccountKey(credentials.Username);
        var (defer, wait) = HuaweiRateGate.Decide(accountKey);
        if (defer) return alarms;
        if (wait > TimeSpan.Zero) await Task.Delay(wait);

        try
        {
            var client = _httpClientFactory.CreateClient();
            var token = await LoginAsync(client, credentials);
            if (token == null) return alarms;

            client.DefaultRequestHeaders.Add("XSRF-TOKEN", token);
            var beginTime = new DateTimeOffset(from).ToUnixTimeMilliseconds();
            var endTime = new DateTimeOffset(to).ToUnixTimeMilliseconds();
            HuaweiRateGate.MarkCalled(accountKey);
            var response = await client.PostAsJsonAsync($"{BaseUrl}/getAlarmList",
                new { stationCodes = credentials.PlantId, beginTime, endTime, pageNo = 1, pageSize = 100 });
            if (!response.IsSuccessStatusCode) return alarms;

            var json = await ReadJsonAsync(response);
            if (json == null) return alarms;

            // Detect 407 in body even on HTTP 200 — extend cooldown
            var alarmFailCode = SafeGetInt(json.Value, "failCode");
            if (alarmFailCode.HasValue && alarmFailCode.Value == 407)
            {
                HuaweiRateGate.RecordRateLimitResponse(accountKey);
                return alarms;
            }

            if (!json.Value.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object) return alarms;

            if (!data.TryGetProperty("list", out var list)
                || list.ValueKind != JsonValueKind.Array) return alarms;

            foreach (var item in list.EnumerateArray())
            {
                try
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;

                    var raiseTime = SafeGetLong(item, "raiseTime");

                    alarms.Add(new AlarmRecord
                    {
                        AlarmId = SafeGetString(item, "alarmId") ?? "",
                        AlarmName = SafeGetString(item, "alarmName") ?? "",
                        // See GetBatchAlarms — the level field is "lev", not "severity".
                        Severity = SafeGetInt(item, "severity") ?? SafeGetInt(item, "lev") ?? 3,
                        OccurredAt = raiseTime.HasValue
                            ? DateTimeOffset.FromUnixTimeMilliseconds(raiseTime.Value).UtcDateTime
                            : DateTime.UtcNow,
                        DeviceName = SafeGetString(item, "devName"),
                        AlarmCode = SafeGetString(item, "causeId") ?? SafeGetString(item, "alarmType"),
                        Description = SafeGetString(item, "alarmCause")
                    });
                }
                catch (Exception itemEx)
                {
                    _logger.LogWarning(itemEx, "Huawei: skipping malformed alarm item");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Huawei GetAlarms failed");
        }
        return alarms;
    }

    public async Task<List<DeviceInfo>> GetDevicesAsync(ApiCredentials credentials)
    {
        _lastDevicesError = null;
        var devices = new List<DeviceInfo>();
        if (string.IsNullOrEmpty(credentials.PlantId))
        {
            _lastDevicesError = "PlantId is empty";
            return devices;
        }

        var accountKey = HuaweiRateGate.AccountKey(credentials.Username);
        var (defer, wait) = HuaweiRateGate.Decide(accountKey);
        if (defer)
        {
            _lastDevicesError = $"Rate gate busy ({wait.TotalSeconds:0.0}s). Try again later.";
            return devices;
        }
        if (wait > TimeSpan.Zero) await Task.Delay(wait);

        try
        {
            var client = _httpClientFactory.CreateClient();
            var token = await LoginAsync(client, credentials);
            if (token == null)
            {
                _lastDevicesError = "Huawei login failed (getDevList needs fresh token)";
                return devices;
            }

            // Normalize station code (users often paste '56946102', Huawei wants 'NE=56946102')
            var stationCode = credentials.PlantId!.Trim();
            if (!stationCode.StartsWith("NE=", StringComparison.OrdinalIgnoreCase))
                stationCode = "NE=" + stationCode;

            client.DefaultRequestHeaders.Add("XSRF-TOKEN", token);
            HuaweiRateGate.MarkCalled(accountKey);
            var response = await client.PostAsJsonAsync($"{BaseUrl}/getDevList",
                new { stationCodes = stationCode });

            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _lastDevicesError = $"/getDevList HTTP {(int)response.StatusCode}: {Truncate(body, 300)}";
                return devices;
            }

            JsonElement json;
            try { json = JsonDocument.Parse(body).RootElement; }
            catch (Exception je)
            {
                _lastDevicesError = $"/getDevList returned non-JSON: {je.Message}";
                return devices;
            }

            // Check for Huawei failCode error in body even when HTTP 200
            var failCode = SafeGetInt(json, "failCode");
            if (failCode.HasValue && failCode.Value != 0)
            {
                if (failCode.Value == 407)
                    HuaweiRateGate.RecordRateLimitResponse(accountKey);

                var errMsg = SafeGetString(json, "message") ?? SafeGetString(json, "data") ?? "Unknown";
                _lastDevicesError = failCode.Value switch
                {
                    407 => $"Rate limited (failCode=407). Retry in 1 min.",
                    305 or 401 => $"Auth expired (failCode={failCode}). Token cache will refresh.",
                    20002 => $"Station '{stationCode}' not accessible by this Northbound account.",
                    _ => $"Huawei failCode={failCode}: {errMsg}"
                };
                return devices;
            }

            if (!json.TryGetProperty("data", out var dataArr)
                || dataArr.ValueKind != JsonValueKind.Array)
            {
                _lastDevicesError = $"/getDevList: no data array. Body: {Truncate(body, 300)}";
                return devices;
            }

            foreach (var item in dataArr.EnumerateArray())
            {
                try
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;

                    var devTypeId = SafeGetInt(item, "devTypeId") ?? 0;
                    devices.Add(new DeviceInfo
                    {
                        DeviceId = SafeGetString(item, "id") ?? SafeGetString(item, "devDn") ?? "",
                        DeviceName = SafeGetString(item, "devName") ?? "",
                        DeviceType = MapHuaweiDevType(devTypeId),
                        Model = SafeGetString(item, "invType") ?? SafeGetString(item, "model"),
                        SerialNumber = SafeGetString(item, "esnCode"),
                        FirmwareVersion = SafeGetString(item, "softwareVersion"),
                        IsOnline = SafeGetInt(item, "devStatus") == 1,
                        RatedPowerKw = SafeGetNullableDecimal(item, "invRatedPower")
                    });
                }
                catch (Exception itemEx)
                {
                    _logger.LogWarning(itemEx, "Huawei: skipping malformed device item");
                }
            }

            if (devices.Count == 0)
            {
                _lastDevicesError = $"/getDevList returned empty array. Station '{stationCode}' may have no devices registered, " +
                    "or your Northbound account lacks device-level permission. Body: " + Truncate(body, 300);
            }
        }
        catch (Exception ex)
        {
            _lastDevicesError = $"{ex.GetType().Name}: {ex.Message}";
            _logger.LogError(ex, "Huawei GetDevices failed");
        }
        return devices;
    }

    private string? _lastDevicesError;
    internal string? LastDevicesError => _lastDevicesError;

    private static string MapHuaweiDevType(int devTypeId) => devTypeId switch
    {
        1 => "Inverter",
        10 => "Smart Power Sensor (Meter)",
        16 => "EMI / Datalogger",
        17 => "Smart Logger",
        37 => "MERC Optimizer",         // Huawei MERC power optimizer
        38 => "Optimizer",
        39 => "Battery",
        41 => "PCS",
        46 => "Smart Dongle",
        47 => "Backup Box",
        62 => "Battery Pack",
        _ => $"Device (type {devTypeId})"
    };

    private static decimal? SafeGetNullableDecimal(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String
            && decimal.TryParse(v.GetString(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var n2)) return n2;
        return null;
    }

    // ══════════════════════════════════════════════════════════════
    //  Authentication
    //
    //  Huawei NBI enforces approximately 1 login/minute (failCode=407
    //  ACCESS_FREQUENCY) AND also throttles back-to-back data calls from
    //  the same account. We cache the XSRF-TOKEN in a static process-wide
    //  dictionary keyed by username, with a 15-min TTL. This means:
    //
    //    - Scheduled sync (6 API calls × N installations) uses 1 login per
    //      account per cycle instead of 6×N logins
    //    - Manual syncs piggyback on the cached token from the last schedule
    //    - Expired or rejected tokens (failCode=305 USER_MUST_RELOGIN) are
    //      evicted automatically so next call re-authenticates
    //
    //  The cache is static so it survives adapter instance recreation
    //  (adapters are Scoped but requests are short-lived).
    // ══════════════════════════════════════════════════════════════

    private static readonly Dictionary<string, (string Token, DateTime ExpiresAt)> _tokenCache = new();
    private static readonly object _tokenCacheLock = new();
    private static readonly TimeSpan TokenTtl = TimeSpan.FromMinutes(15);
    private static DateTime _lastTokenCacheSweepUtc = DateTime.MinValue;

    private static void EvictExpiredTokens_NoLock()
    {
        var now = DateTime.UtcNow;
        if (now - _lastTokenCacheSweepUtc < TimeSpan.FromMinutes(5)) return;
        _lastTokenCacheSweepUtc = now;
        var stale = _tokenCache
            .Where(kv => kv.Value.ExpiresAt <= now)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var k in stale) _tokenCache.Remove(k);
    }

    private static void InvalidateToken(string cacheKey)
    {
        lock (_tokenCacheLock) { _tokenCache.Remove(cacheKey); }
    }

    /// <summary>
    /// Public hook so credential edits can purge the cached XSRF token.
    /// Without this, after a credential rotation the old token sticks for up
    /// to 15 minutes and gets rejected with 305/401 on every sync until the
    /// reactive InvalidateToken-on-error path kicks in.
    /// </summary>
    public static void InvalidateTokenForCredentials(string? username, string? password)
    {
        if (string.IsNullOrEmpty(username)) return;
        var cacheKey = $"{username}|{password?.Length ?? 0}";
        InvalidateToken(cacheKey);
    }

    private async Task<string?> LoginAsync(HttpClient client, ApiCredentials credentials)
    {
        var (token, _) = await LoginWithDetailAsync(client, credentials);
        return token;
    }

    private async Task<(string? Token, string Error)> LoginWithDetailAsync(HttpClient client, ApiCredentials credentials)
    {
        var cacheKey = $"{credentials.Username}|{credentials.Password?.Length ?? 0}";

        // ── Check cache first ──
        lock (_tokenCacheLock)
        {
            if (_tokenCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.ExpiresAt)
                return (cached.Token, "OK (cached)");
        }

        // ── Rate gate ── login itself counts toward Huawei's 1 req/min budget.
        // If we'd block too long, return whatever we last cached (even if expired)
        // rather than firing a real login that's just going to 407.
        var accountKey = HuaweiRateGate.AccountKey(credentials.Username);
        var (defer, wait) = HuaweiRateGate.Decide(accountKey);
        if (defer)
        {
            lock (_tokenCacheLock)
            {
                if (_tokenCache.TryGetValue(cacheKey, out var stale))
                    return (stale.Token, "OK (stale cached, rate gate busy)");
            }
            return (null, $"Rate gate busy ({wait.TotalSeconds:0.0}s) and no cached token available.");
        }
        if (wait > TimeSpan.Zero) await Task.Delay(wait);

        try
        {
            HuaweiRateGate.MarkCalled(accountKey);
            var response = await client.PostAsJsonAsync($"{BaseUrl}/login",
                new { userName = credentials.Username, systemCode = credentials.Password });

            // Token is in XSRF-TOKEN header
            if (response.Headers.TryGetValues("XSRF-TOKEN", out var tokens))
            {
                var token = tokens.FirstOrDefault();
                if (!string.IsNullOrEmpty(token))
                {
                    lock (_tokenCacheLock)
                    {
                        _tokenCache[cacheKey] = (token, DateTime.UtcNow.Add(TokenTtl));
                        EvictExpiredTokens_NoLock();
                    }
                    return (token, "OK");
                }
            }

            // Fallback: check response body
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return (null, $"HTTP {(int)response.StatusCode}: {Truncate(body, 300)}");

            try
            {
                var json = JsonDocument.Parse(body).RootElement;
                var failCode = SafeGetInt(json, "failCode");
                if (failCode.HasValue && failCode.Value != 0)
                {
                    var msg = SafeGetString(json, "message") ?? SafeGetString(json, "data") ?? "Unknown";

                    // 407 = rate limit. If we have a cached token from recent sync,
                    // use it — don't throw away a working session just because we
                    // couldn't re-login this minute. Also extend the cooldown so
                    // subsequent calls back off harder.
                    if (failCode.Value == 407)
                    {
                        HuaweiRateGate.RecordRateLimitResponse(accountKey);
                        lock (_tokenCacheLock)
                        {
                            if (_tokenCache.TryGetValue(cacheKey, out var cached))
                                return (cached.Token, "OK (cached, login rate-limited)");
                        }
                    }

                    return (null, $"Login failed code={failCode}: {msg}");
                }
            }
            catch (Exception parseEx)
            {
                _logger.LogWarning(parseEx, "Huawei: login body not JSON");
            }

            return (null, $"No token in response. Body: {Truncate(body, 300)}");
        }
        catch (HttpRequestException ex)
        {
            return (null, $"Network error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (null, $"Exception: {ex.Message}");
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  Safe JSON helpers (never throw on null/wrong-type)
    // ══════════════════════════════════════════════════════════════

    private static async Task<JsonElement?> ReadJsonAsync(HttpResponseMessage response)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(body)) return null;
            return JsonDocument.Parse(body).RootElement;
        }
        catch
        {
            return null;
        }
    }

    private static JsonElement? FindFirstArrayItem(JsonElement root, params string[] path)
    {
        var cur = root;
        foreach (var key in path)
        {
            if (cur.ValueKind != JsonValueKind.Object) return null;
            if (!cur.TryGetProperty(key, out var next)) return null;
            cur = next;
        }
        if (cur.ValueKind != JsonValueKind.Array) return null;
        var first = cur.EnumerateArray().FirstOrDefault();
        return first.ValueKind == JsonValueKind.Undefined ? null : first;
    }

    private static string? SafeGetString(JsonElement el, string prop)
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

    private static decimal SafeGetDecimal(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return 0;
        if (!el.TryGetProperty(prop, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(),
            System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d2))
            return d2;
        return 0;
    }

    private static int? SafeGetInt(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var n2)) return n2;
        return null;
    }

    private static long? SafeGetLong(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var n2)) return n2;
        return null;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";

    private static decimal GetDecimal(JsonElement el, string prop) => SafeGetDecimal(el, prop);

    /// <summary>
    /// Fetches total instantaneous active power (kW) by summing all inverter
    /// devices in the plant. Returns 0 if devices not fetchable (non-fatal).
    /// </summary>
    private async Task<decimal> FetchStationActivePowerKwAsync(HttpClient client, string stationCode)
    {
        // Step 1: list devices (one call, rate-limited)
        var devResponse = await client.PostAsJsonAsync($"{BaseUrl}/getDevList",
            new { stationCodes = stationCode });
        if (!devResponse.IsSuccessStatusCode) return 0;
        var devJson = await ReadJsonAsync(devResponse);
        if (devJson == null || !devJson.Value.TryGetProperty("data", out var devArr)
            || devArr.ValueKind != JsonValueKind.Array) return 0;

        // Only inverters (devTypeId = 1) can report active_power
        var devIds = devArr.EnumerateArray()
            .Where(d => d.ValueKind == JsonValueKind.Object && SafeGetInt(d, "devTypeId") == 1)
            .Select(d => SafeGetLong(d, "id"))
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList();

        if (!devIds.Any()) return 0;

        // Step 2: fetch device KPI in a single batch
        var kpiResponse = await client.PostAsJsonAsync($"{BaseUrl}/getDevRealKpi",
            new { devIds = string.Join(",", devIds), devTypeId = 1 });
        if (!kpiResponse.IsSuccessStatusCode) return 0;
        var kpiJson = await ReadJsonAsync(kpiResponse);
        if (kpiJson == null || !kpiJson.Value.TryGetProperty("data", out var kpiArr)
            || kpiArr.ValueKind != JsonValueKind.Array) return 0;

        decimal totalKw = 0;
        foreach (var item in kpiArr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("dataItemMap", out var dim) || dim.ValueKind != JsonValueKind.Object) continue;
            totalKw += SafeGetDecimal(dim, "active_power");
        }
        return totalKw;
    }

    // ═══════════════════════════════════════════════════════════
    //  New ApiResult<T> methods — typed, classified responses
    // ═══════════════════════════════════════════════════════════

    public async Task<ApiResult<bool>> TestConnectionResultAsync(ApiCredentials credentials, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (string.IsNullOrEmpty(credentials.Username))
            return ApiResult<bool>.Fail(ApiResultKind.InvalidCredentials,
                "Username (Northbound API user) is required.", duration: sw.Elapsed);
        if (string.IsNullOrEmpty(credentials.Password))
            return ApiResult<bool>.Fail(ApiResultKind.InvalidCredentials,
                "Password (systemCode) is required.", duration: sw.Elapsed);

        try
        {
            var (ok, msg) = await TestConnectionAsync(credentials);
            sw.Stop();
            if (ok) return ApiResult<bool>.Ok(true, msg, sw.Elapsed);
            return ApiResultClassifier.ClassifyErrorMessage<bool>(msg, "Huawei", sw.Elapsed);
        }
        catch (TaskCanceledException)
        {
            sw.Stop();
            return ApiResult<bool>.Fail(ApiResultKind.Timeout,
                "Huawei API timeout during connection test.", duration: sw.Elapsed);
        }
        catch (HttpRequestException hre)
        {
            sw.Stop();
            return ApiResult<bool>.Fail(ApiResultKind.NetworkError,
                $"Network error: {hre.Message}", detail: hre.InnerException?.Message,
                duration: sw.Elapsed);
        }
    }

    public async Task<ApiResult<PlantInfo>> GetPlantInfoResultAsync(ApiCredentials credentials, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var info = await GetPlantInfoAsync(credentials);
            sw.Stop();
            if (info != null)
                return ApiResult<PlantInfo>.Ok(info, "Plant info retrieved", sw.Elapsed);

            // Surface the detailed error (which includes the list of valid stations
            // on the account) instead of a generic "not found".
            var msg = _lastPlantLookupError
                ?? $"Plant '{credentials.PlantId}' not found on Huawei account.";
            return ApiResult<PlantInfo>.Fail(ApiResultKind.PlantNotFound, msg, duration: sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<PlantInfo>(ex, "Huawei", sw.Elapsed); }
    }

    public async Task<ApiResult<RealtimeData>> GetRealtimeResultAsync(ApiCredentials credentials, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var data = await GetRealtimeDataAsync(credentials);
            sw.Stop();
            if (data == null)
            {
                // Classify based on captured error detail.
                // Note: login errors use "code=NNN", getStationRealKpi uses "failCode=NNN".
                // Also check for ACCESS_FREQUENCY which is Huawei's rate-limit label.
                var err = _lastRealtimeError ?? "Huawei API returned no data.";
                var kind = ApiResultKind.CloudServerError;
                if (err.Contains("407") || err.Contains("Rate limit")
                    || err.Contains("ACCESS_FREQUENCY", StringComparison.OrdinalIgnoreCase)
                    || err.Contains("frequency is too high", StringComparison.OrdinalIgnoreCase))
                    kind = ApiResultKind.RateLimited;
                else if (err.Contains("305") || err.Contains("401")
                         || err.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
                         || err.Contains("USER_MUST_RELOGIN", StringComparison.OrdinalIgnoreCase))
                    kind = ApiResultKind.AuthenticationFailed;
                else if (err.Contains("20002") || err.Contains("not found", StringComparison.OrdinalIgnoreCase)
                         || err.Contains("STATION_NOT_EXISTED", StringComparison.OrdinalIgnoreCase))
                    kind = ApiResultKind.PlantNotFound;
                else if (err.Contains("timed out", StringComparison.OrdinalIgnoreCase))
                    kind = ApiResultKind.Timeout;
                else if (err.Contains("Network error", StringComparison.OrdinalIgnoreCase))
                    kind = ApiResultKind.NetworkError;
                else if (err.Contains("Invalid JSON", StringComparison.OrdinalIgnoreCase))
                    kind = ApiResultKind.ParseError;
                else if (err.Contains("PlantId", StringComparison.OrdinalIgnoreCase)
                         && err.Contains("empty", StringComparison.OrdinalIgnoreCase))
                    kind = ApiResultKind.InvalidCredentials;

                return ApiResult<RealtimeData>.Fail(kind, err,
                    httpStatus: _lastRealtimeHttpCode == 0 ? null : _lastRealtimeHttpCode,
                    duration: sw.Elapsed);
            }
            return data.IsOnline
                ? ApiResult<RealtimeData>.Ok(data, "Device online", sw.Elapsed)
                : ApiResult<RealtimeData>.Offline(data,
                    "Huawei API connected OK, but station reports offline/sleeping. Check inverter WiFi at site.",
                    sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<RealtimeData>(ex, "Huawei", sw.Elapsed); }
    }

    public async Task<ApiResult<List<EnergyRecord>>> GetHistoricalResultAsync(ApiCredentials credentials, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var list = await GetHistoricalDataAsync(credentials, from, to);
            sw.Stop();
            return ApiResult<List<EnergyRecord>>.Ok(list, $"{list.Count} record(s) fetched", sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<List<EnergyRecord>>(ex, "Huawei", sw.Elapsed); }
    }

    public async Task<ApiResult<List<AlarmRecord>>> GetAlarmsResultAsync(ApiCredentials credentials, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var list = await GetAlarmsAsync(credentials, from, to);
            sw.Stop();
            return ApiResult<List<AlarmRecord>>.Ok(list, $"{list.Count} alarm(s) fetched", sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<List<AlarmRecord>>(ex, "Huawei", sw.Elapsed); }
    }

    public async Task<ApiResult<List<DeviceInfo>>> GetDevicesResultAsync(ApiCredentials credentials, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var list = await GetDevicesAsync(credentials);
            sw.Stop();
            if (list.Count > 0)
                return ApiResult<List<DeviceInfo>>.Ok(list, $"{list.Count} device(s) fetched", sw.Elapsed);
            // Surface the detailed reason when empty so callers can tell the user
            var detail = _lastDevicesError ?? "Empty device list (reason unknown)";
            var kind = detail.Contains("Rate limited") ? ApiResultKind.RateLimited
                     : detail.Contains("Auth expired") || detail.Contains("login failed", StringComparison.OrdinalIgnoreCase)
                        ? ApiResultKind.AuthenticationFailed
                     : detail.Contains("not accessible") ? ApiResultKind.PlantNotFound
                     : detail.Contains("no device") || detail.Contains("no devices") ? ApiResultKind.DeviceOffline
                     : ApiResultKind.CloudServerError;
            return ApiResult<List<DeviceInfo>>.Fail(kind, detail, duration: sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<List<DeviceInfo>>(ex, "Huawei", sw.Elapsed); }
    }

}
