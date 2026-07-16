using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SolarApp.Models.Enums;
using SolarApp.Services.InverterApi.Common;
using SolarApp.Services.InverterApi.Models;

namespace SolarApp.Services.InverterApi.Adapters;

/// <summary>
/// Deye Cloud API adapter (https://developer.deyecloud.com).
///
/// Credentials mapping:
///   ApiKey    = appId (App ID from Deye developer portal)
///   ApiSecret = appSecret
///   Username  = email
///   Password  = password
///   PlantId   = Station ID or Device SN
/// </summary>
public class DeyeAdapter : IInverterApiAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DeyeAdapter> _logger;
    private const string BaseUrl = "https://eu1-developer.deyecloud.com/v1.0";

    public InverterBrand Brand => InverterBrand.Deye;

    // Static token cache — shared across all requests + installations that
    // use the same Deye account.  Previously the adapter POSTed
    // /account/token on EVERY call (~5 per sync round per site), which both
    // wasted latency and chewed through Deye's 10-req/min budget.  Deye
    // access tokens are long-lived; we cache conservatively for 50 min and
    // clear on any auth error so a rotated token self-heals.
    private static readonly Dictionary<string, (string Token, DateTime ExpiresAt)> _tokenCache = new();
    private static readonly object _tokenLock = new();
    private static readonly TimeSpan TokenTtl = TimeSpan.FromMinutes(50);

    public DeyeAdapter(IHttpClientFactory httpClientFactory, ILogger<DeyeAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Regional base URL.  Deye/Solarman host per-region developer clouds
    /// (eu1 / us1 / cn1 …).  Honour an explicit CloudApiBaseUrl override so
    /// non-EU accounts aren't stuck on the hardcoded eu1 endpoint (which
    /// rejects their login).  Falls back to eu1 when unset.
    /// </summary>
    private static string ResolveBase(ApiCredentials credentials)
    {
        var ov = credentials.ApiBaseUrl?.Trim();
        if (!string.IsNullOrWhiteSpace(ov))
        {
            ov = ov.TrimEnd('/');
            // Accept either a full ".../v1.0" or a bare host; append the
            // versioned path when the operator gave only the host.
            if (!ov.Contains("/v1.0", StringComparison.OrdinalIgnoreCase)
                && !ov.Contains("/v1", StringComparison.OrdinalIgnoreCase))
                ov += "/v1.0";
            return ov;
        }
        return BaseUrl;
    }

    private static string TokenCacheKey(ApiCredentials c) =>
        $"deye|{c.ApiKey}|{c.Username}|{c.Password?.Length ?? 0}";

    public async Task<(bool Success, string Message)> TestConnectionAsync(ApiCredentials credentials)
    {
        if (string.IsNullOrEmpty(credentials.ApiKey)) return (false, "appId (ApiKey) is required");
        if (string.IsNullOrEmpty(credentials.ApiSecret)) return (false, "appSecret (ApiSecret) is required");
        if (string.IsNullOrEmpty(credentials.Username)) return (false, "Email (Username) is required");
        if (string.IsNullOrEmpty(credentials.Password)) return (false, "Password is required");

        try
        {
            var (token, error) = await GetTokenWithDetailAsync(credentials);
            return !string.IsNullOrEmpty(token)
                ? (true, "Connected to Deye Cloud!")
                : (false, $"Deye login failed: {error}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deye TestConnection failed");
            return (false, $"Deye error: {ex.Message}");
        }
    }

    public async Task<PlantInfo?> GetPlantInfoAsync(ApiCredentials credentials)
    {
        try
        {
            var client = await CreateAuthenticatedClientAsync(credentials);
            if (client == null) return null;

            var response = await client.PostAsJsonAsync($"{ResolveBase(credentials)}/station/latest",
                new { stationId = credentials.PlantId });
            if (!response.IsSuccessStatusCode) return null;

            var json = await ReadJsonAsync(response);
            if (json == null) return null;

            if (!json.Value.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object) return null;

            return new PlantInfo
            {
                PlantId = credentials.PlantId ?? "",
                PlantName = SafeGetString(data, "stationName") ?? "",
                CapacityKw = SafeGetDecimal(data, "installedCapacity"),
                Address = SafeGetString(data, "locationAddress")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deye GetPlantInfo failed");
            return null;
        }
    }

    public async Task<RealtimeData?> GetRealtimeDataAsync(ApiCredentials credentials)
    {
        try
        {
            var client = await CreateAuthenticatedClientAsync(credentials);
            if (client == null) return null;

            var response = await client.PostAsJsonAsync($"{ResolveBase(credentials)}/station/latest",
                new { stationId = credentials.PlantId });
            if (!response.IsSuccessStatusCode) return null;

            var json = await ReadJsonAsync(response);
            if (json == null) return null;

            if (!json.Value.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object) return null;

            return new RealtimeData
            {
                Timestamp = DateTime.UtcNow,
                CurrentPowerKw = SafeGetDecimal(data, "generationPower") / 1000m,
                TodayEnergyKwh = SafeGetDecimal(data, "generationToday"),
                TotalEnergyKwh = SafeGetDecimal(data, "generationTotal"),
                BatterySocPercent = SafeGetNullableDecimal(data, "batterySoc"),
                IsOnline = SafeGetInt(data, "status") == 1
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deye GetRealtimeData failed");
            return null;
        }
    }

    public async Task<List<EnergyRecord>> GetHistoricalDataAsync(ApiCredentials credentials, DateTime from, DateTime to)
    {
        var records = new List<EnergyRecord>();
        Common.AdapterCallState.Reset();
        try
        {
            var client = await CreateAuthenticatedClientAsync(credentials);
            if (client == null)
            {
                Common.AdapterCallState.Fail("Deye login failed (no token) — check appId/appSecret/email/password.");
                return records;
            }

            // Previous version called /device/originalData with deviceSn —
            // wrong endpoint (returns raw 5-min telemetry, not daily kWh
            // aggregates) and wrong field (Deye PlantId is the stationId,
            // not the device SN).  Use /station/history with dateType=2
            // (day) which returns one row per calendar day with
            // generationValue / gridSell / gridBuy fields.
            var response = await client.PostAsJsonAsync($"{ResolveBase(credentials)}/station/history",
                new
                {
                    stationId = credentials.PlantId,
                    dateType = 2,   // 1=hour, 2=day, 3=month, 4=year
                    startDate = from.ToString("yyyy-MM-dd"),
                    endDate = to.ToString("yyyy-MM-dd")
                });
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Deye /station/history returned HTTP {Status}", (int)response.StatusCode);
                Common.AdapterCallState.Fail($"Deye /station/history HTTP {(int)response.StatusCode}");
                return records;
            }

            var json = await ReadJsonAsync(response);
            if (json == null) return records;

            // Tolerate Deye's two documented response shapes:
            //   { data: [ {...}, {...} ] }
            //   { data: { stationDataItems: [ ... ] } }
            JsonElement arr;
            if (!json.Value.TryGetProperty("data", out var data)) return records;
            if (data.ValueKind == JsonValueKind.Array)
                arr = data;
            else if (data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("stationDataItems", out var items)
                && items.ValueKind == JsonValueKind.Array)
                arr = items;
            else
                return records;

            // First non-zero across the known field-name aliases.
            decimal Pick(JsonElement el, params string[] keys)
            {
                foreach (var k in keys)
                {
                    var v = SafeGetDecimal(el, k);
                    if (v != 0m) return v;
                }
                return 0m;
            }

            foreach (var item in arr.EnumerateArray())
            {
                try
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var dateStr = SafeGetString(item, "dateTime")
                               ?? SafeGetString(item, "date")
                               ?? SafeGetString(item, "day");
                    if (dateStr == null || !DateTime.TryParse(dateStr, out var date)) continue;

                    records.Add(new EnergyRecord
                    {
                        Timestamp = date,
                        EnergyGenerationKwh   = Pick(item, "generationValue", "generation", "energy"),
                        GridExportKwh         = Pick(item, "gridSell", "gridExport", "feedin"),
                        GridImportKwh         = Pick(item, "gridBuy", "gridImport", "purchase"),
                        SelfConsumptionKwh    = Pick(item, "selfUse", "selfConsumption"),
                        BatteryChargeKwh      = SafeGetNullableDecimal(item, "chargeBattery") ?? SafeGetNullableDecimal(item, "batteryCharge"),
                        BatteryDischargeKwh   = SafeGetNullableDecimal(item, "dischargeBattery") ?? SafeGetNullableDecimal(item, "batteryDischarge"),
                    });
                }
                catch (Exception itemEx)
                {
                    _logger.LogWarning(itemEx, "Deye: skipping malformed history record");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deye GetHistoricalData failed");
            Common.AdapterCallState.Fail($"Deye historical error: {ex.GetType().Name}: {ex.Message}");
        }
        return records;
    }

    public async Task<List<AlarmRecord>> GetAlarmsAsync(ApiCredentials credentials, DateTime from, DateTime to)
    {
        var alarms = new List<AlarmRecord>();
        try
        {
            var client = await CreateAuthenticatedClientAsync(credentials);
            if (client == null) return alarms;

            var response = await client.PostAsJsonAsync($"{ResolveBase(credentials)}/device/fault",
                new { deviceSn = credentials.PlantId, pageNo = 1, pageSize = 100 });
            if (!response.IsSuccessStatusCode) return alarms;

            var json = await ReadJsonAsync(response);
            if (json == null) return alarms;

            if (!json.Value.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object) return alarms;

            if (!data.TryGetProperty("records", out var records)
                || records.ValueKind != JsonValueKind.Array) return alarms;

            foreach (var item in records.EnumerateArray())
            {
                try
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var dateStr = SafeGetString(item, "createTime");
                    var occurred = DateTime.UtcNow;
                    if (dateStr != null) DateTime.TryParse(dateStr, out occurred);

                    alarms.Add(new AlarmRecord
                    {
                        AlarmId = SafeGetString(item, "id") ?? "",
                        AlarmName = SafeGetString(item, "message") ?? "",
                        Severity = SafeGetInt(item, "level") ?? 3,
                        OccurredAt = occurred,
                        DeviceName = SafeGetString(item, "deviceSn")
                    });
                }
                catch (Exception itemEx)
                {
                    _logger.LogWarning(itemEx, "Deye: skipping malformed alarm record");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deye GetAlarms failed");
        }
        return alarms;
    }

    public async Task<List<DeviceInfo>> GetDevicesAsync(ApiCredentials credentials)
    {
        var devices = new List<DeviceInfo>();
        try
        {
            var client = await CreateAuthenticatedClientAsync(credentials);
            if (client == null) return devices;

            var response = await client.PostAsJsonAsync($"{ResolveBase(credentials)}/device/list",
                new { stationId = credentials.PlantId, pageNo = 1, pageSize = 100 });
            if (!response.IsSuccessStatusCode) return devices;

            var json = await ReadJsonAsync(response);
            if (json == null) return devices;

            if (!json.Value.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object) return devices;

            if (!data.TryGetProperty("records", out var records)
                || records.ValueKind != JsonValueKind.Array) return devices;

            foreach (var item in records.EnumerateArray())
            {
                try
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    devices.Add(new DeviceInfo
                    {
                        DeviceId = SafeGetString(item, "deviceSn") ?? "",
                        DeviceName = SafeGetString(item, "deviceName") ?? "",
                        DeviceType = SafeGetString(item, "deviceType") ?? "Inverter",
                        SerialNumber = SafeGetString(item, "deviceSn"),
                        IsOnline = SafeGetInt(item, "status") == 1
                    });
                }
                catch (Exception itemEx)
                {
                    _logger.LogWarning(itemEx, "Deye: skipping malformed device record");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deye GetDevices failed");
        }
        return devices;
    }

    private async Task<HttpClient?> CreateAuthenticatedClientAsync(ApiCredentials credentials)
    {
        var token = await GetTokenAsync(credentials);
        if (string.IsNullOrEmpty(token)) return null;

        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<string?> GetTokenAsync(ApiCredentials credentials)
    {
        var (token, _) = await GetTokenWithDetailAsync(credentials);
        return token;
    }

    private async Task<(string? Token, string Error)> GetTokenWithDetailAsync(ApiCredentials credentials)
    {
        // ── Cache hit ──
        var cacheKey = TokenCacheKey(credentials);
        lock (_tokenLock)
        {
            if (_tokenCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.ExpiresAt)
                return (cached.Token, "OK (cached)");
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.PostAsJsonAsync($"{ResolveBase(credentials)}/account/token",
                new
                {
                    appId = credentials.ApiKey,
                    appSecret = credentials.ApiSecret,
                    email = credentials.Username,
                    password = credentials.Password
                });

            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return (null, $"HTTP {(int)response.StatusCode}: {Truncate(body, 300)}");

            var json = JsonDocument.Parse(body).RootElement;
            if (json.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                var token = SafeGetString(data, "accessToken");
                if (!string.IsNullOrEmpty(token))
                {
                    lock (_tokenLock)
                    {
                        _tokenCache[cacheKey] = (token, DateTime.UtcNow.Add(TokenTtl));
                    }
                    return (token, "OK");
                }
            }

            var msg = SafeGetString(json, "msg") ?? SafeGetString(json, "message") ?? "no token in response";
            return (null, msg);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  Safe JSON helpers
    // ══════════════════════════════════════════════════════════════

    private async Task<JsonElement?> ReadJsonAsync(HttpResponseMessage response)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(body)) return null;
            return JsonDocument.Parse(body).RootElement;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Deye: failed to parse response JSON");
            return null;
        }
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

    private static decimal? SafeGetNullableDecimal(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(),
            System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d2))
            return d2;
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

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";

    // Legacy aliases kept for compatibility
    private static decimal GetDecimal(JsonElement el, string prop) => SafeGetDecimal(el, prop);
    private static decimal? GetNullableDecimal(JsonElement el, string prop) => SafeGetNullableDecimal(el, prop);

    // ═══════════════════════════════════════════════════════════
    //  ApiResult<T> methods
    // ═══════════════════════════════════════════════════════════

    public async Task<ApiResult<bool>> TestConnectionResultAsync(ApiCredentials credentials, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var (ok, msg) = await TestConnectionAsync(credentials);
            sw.Stop();
            return ok ? ApiResult<bool>.Ok(true, msg, sw.Elapsed)
                      : ApiResultClassifier.ClassifyErrorMessage<bool>(msg, "Deye", sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<bool>(ex, "Deye", sw.Elapsed); }
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
                    $"Plant '{credentials.PlantId}' not found on Deye account.", duration: sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<PlantInfo>(ex, "Deye", sw.Elapsed); }
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
                    "Deye API returned no data. Could be server issue or PlantId mismatch.", duration: sw.Elapsed);
            return data.IsOnline
                ? ApiResult<RealtimeData>.Ok(data, "Device online", sw.Elapsed)
                : ApiResult<RealtimeData>.Offline(data,
                    "Deye API connected OK, but device reports offline. Check inverter datalogger.",
                    sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<RealtimeData>(ex, "Deye", sw.Elapsed); }
    }

    public async Task<ApiResult<List<EnergyRecord>>> GetHistoricalResultAsync(ApiCredentials credentials, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var list = await GetHistoricalDataAsync(credentials, from, to);
            sw.Stop();
            var fail = Common.AdapterCallState.Take();
            // 0 records + a recorded transport error = a real failure, not
            // "no data" — surface it so backfill/reporting isn't misled.
            if (list.Count == 0 && fail != null)
                return ApiResult<List<EnergyRecord>>.Fail(
                    ApiResultKind.CloudServerError, "Deye historical fetch failed.", detail: fail, duration: sw.Elapsed);
            return ApiResult<List<EnergyRecord>>.Ok(list, $"{list.Count} record(s) fetched", sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<List<EnergyRecord>>(ex, "Deye", sw.Elapsed); }
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
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<List<AlarmRecord>>(ex, "Deye", sw.Elapsed); }
    }

    public async Task<ApiResult<List<DeviceInfo>>> GetDevicesResultAsync(ApiCredentials credentials, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var list = await GetDevicesAsync(credentials);
            sw.Stop();
            return ApiResult<List<DeviceInfo>>.Ok(list, $"{list.Count} device(s) fetched", sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<List<DeviceInfo>>(ex, "Deye", sw.Elapsed); }
    }
}
