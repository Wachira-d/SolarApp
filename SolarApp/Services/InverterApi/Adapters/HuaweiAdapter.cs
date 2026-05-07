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

    public InverterBrand Brand => InverterBrand.Huawei;

    public HuaweiAdapter(IHttpClientFactory httpClientFactory, ILogger<HuaweiAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(ApiCredentials credentials)
    {
        if (string.IsNullOrEmpty(credentials.Username))
            return (false, "Username (Northbound API user) is required");
        if (string.IsNullOrEmpty(credentials.Password))
            return (false, "Password (systemCode) is required");

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
                CapacityKw = SafeGetDecimal(match.Value, "capacity"),
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

    public async Task<RealtimeData?> GetRealtimeDataAsync(ApiCredentials credentials)
    {
        _lastRealtimeError = null;
        _lastRealtimeHttpCode = 0;

        if (string.IsNullOrEmpty(credentials.PlantId))
        {
            _lastRealtimeError = "PlantId (Station Code) is empty in installation settings.";
            return null;
        }

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
            //   real_health_state: 0=disconnect, 1=faulty, 2=healthy, 3=offline
            //
            // IMPORTANT: We deliberately SKIP device-level active_power fetch here
            // because Huawei NBI enforces 1-request-per-minute rate limit. Extra
            // calls /getDevList + /getDevRealKpi would exhaust the quota.
            // CurrentPowerKw is left at 0 — Dashboard can derive approximate power
            // from day_power delta between sync cycles.
            return new RealtimeData
            {
                Timestamp = DateTime.UtcNow,
                CurrentPowerKw = 0,
                TodayEnergyKwh = SafeGetDecimal(kpi, "day_power"),
                TotalEnergyKwh = SafeGetDecimal(kpi, "total_power"),
                IsOnline = health >= 1
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

    public async Task<List<EnergyRecord>> GetHistoricalDataAsync(
        ApiCredentials credentials, DateTime from, DateTime to)
    {
        var records = new List<EnergyRecord>();
        if (string.IsNullOrEmpty(credentials.PlantId)) return records;

        try
        {
            var client = _httpClientFactory.CreateClient();
            var token = await LoginAsync(client, credentials);
            if (token == null) return records;

            client.DefaultRequestHeaders.Add("XSRF-TOKEN", token);

            // Iterate each day in range
            var cur = from.Date;
            while (cur <= to.Date)
            {
                try
                {
                    var collectTime = new DateTimeOffset(cur).ToUnixTimeMilliseconds();
                    var response = await client.PostAsJsonAsync($"{BaseUrl}/getKpiStationDay",
                        new { stationCodes = credentials.PlantId, collectTime });

                    if (response.IsSuccessStatusCode)
                    {
                        var json = await ReadJsonAsync(response);
                        if (json.HasValue
                            && json.Value.TryGetProperty("data", out var dataArr)
                            && dataArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in dataArr.EnumerateArray())
                            {
                                if (item.ValueKind != JsonValueKind.Object) continue;
                                if (!item.TryGetProperty("dataItemMap", out var kpi)
                                    || kpi.ValueKind != JsonValueKind.Object) continue;

                                var ct = SafeGetLong(item, "collectTime");
                                if (ct == null) continue;

                                records.Add(new EnergyRecord
                                {
                                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(ct.Value).UtcDateTime,
                                    EnergyGenerationKwh = SafeGetDecimal(kpi, "inverter_power"),
                                    GridExportKwh = SafeGetDecimal(kpi, "ongrid_power"),
                                    SelfConsumptionKwh = SafeGetDecimal(kpi, "selfuse_power")
                                });
                            }
                        }
                    }
                }
                catch (Exception exDay)
                {
                    _logger.LogWarning(exDay, "Huawei day {Date} skipped", cur);
                }

                cur = cur.AddDays(1);
                // Huawei rate limit: 1 req/min. Use shorter delay since this is historical
                await Task.Delay(500);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Huawei GetHistoricalData failed");
        }
        return records;
    }

    public async Task<List<AlarmRecord>> GetAlarmsAsync(
        ApiCredentials credentials, DateTime from, DateTime to)
    {
        var alarms = new List<AlarmRecord>();
        if (string.IsNullOrEmpty(credentials.PlantId)) return alarms;

        try
        {
            var client = _httpClientFactory.CreateClient();
            var token = await LoginAsync(client, credentials);
            if (token == null) return alarms;

            client.DefaultRequestHeaders.Add("XSRF-TOKEN", token);
            var beginTime = new DateTimeOffset(from).ToUnixTimeMilliseconds();
            var endTime = new DateTimeOffset(to).ToUnixTimeMilliseconds();
            var response = await client.PostAsJsonAsync($"{BaseUrl}/getAlarmList",
                new { stationCodes = credentials.PlantId, beginTime, endTime, pageNo = 1, pageSize = 100 });
            if (!response.IsSuccessStatusCode) return alarms;

            var json = await ReadJsonAsync(response);
            if (json == null) return alarms;

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
                        Severity = SafeGetInt(item, "severity") ?? 3,
                        OccurredAt = raiseTime.HasValue
                            ? DateTimeOffset.FromUnixTimeMilliseconds(raiseTime.Value).UtcDateTime
                            : DateTime.UtcNow,
                        DeviceName = SafeGetString(item, "devName")
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

    private static void InvalidateToken(string cacheKey)
    {
        lock (_tokenCacheLock) { _tokenCache.Remove(cacheKey); }
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

        try
        {
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
                    // couldn't re-login this minute.
                    if (failCode.Value == 407)
                    {
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
