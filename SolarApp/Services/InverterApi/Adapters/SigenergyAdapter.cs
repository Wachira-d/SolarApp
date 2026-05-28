using System.Net.Http.Json;
using System.Text.Json;
using SolarApp.Models.Enums;
using SolarApp.Services.InverterApi.Common;
using SolarApp.Services.InverterApi.Models;

namespace SolarApp.Services.InverterApi.Adapters;

/// <summary>
/// Sigenergy Cloud (SigenCloud) API adapter.
///
/// Sigenergy uses a region-based OAuth2 API:
///   Base URL: https://api-{region}.sigencloud.com
///   Regions: eu, apac, us, cn
///
/// Credentials mapping:
///   Username = mySigen app email / phone
///   Password = mySigen app password
///   ApiKey   = Region code (eu / apac / us / cn) — defaults to "apac"
///
/// Auth: POST /auth/oauth/token → returns access_token + refresh_token.
/// Rate limit: 1 request per endpoint per 5 minutes (strict).
/// </summary>
public class SigenergyAdapter : IInverterApiAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SigenergyAdapter> _logger;

    public InverterBrand Brand => InverterBrand.Sigenergy;

    private static readonly Dictionary<string, (string Token, DateTime ExpiresAt)> _tokenCache = new();
    private static readonly object _tokenLock = new();
    private static readonly TimeSpan TokenTtl = TimeSpan.FromMinutes(55);

    private static readonly Dictionary<string, string> RegionUrls = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eu"]   = "https://api-eu.sigencloud.com",
        ["apac"] = "https://api-apac.sigencloud.com",
        ["us"]   = "https://api-us.sigencloud.com",
        ["cn"]   = "https://api-cn.sigencloud.com",
    };

    public SigenergyAdapter(IHttpClientFactory httpClientFactory, ILogger<SigenergyAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private string GetBaseUrl(ApiCredentials creds)
    {
        var region = (creds.ApiKey ?? "apac").Trim().ToLowerInvariant();
        return RegionUrls.TryGetValue(region, out var url) ? url : RegionUrls["apac"];
    }

    private async Task<(string? Token, string Error)> LoginAsync(ApiCredentials credentials)
    {
        var baseUrl = GetBaseUrl(credentials);
        var cacheKey = $"sigen|{baseUrl}|{credentials.Username}|{credentials.Password?.Length ?? 0}";
        lock (_tokenLock)
        {
            if (_tokenCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.ExpiresAt)
                return (cached.Token, "OK (cached)");
        }

        try
        {
            var client = CreateClient();
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = credentials.Username ?? "",
                ["password"] = credentials.Password ?? "",
            });

            var response = await client.PostAsync($"{baseUrl}/auth/oauth/token", form);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return (null, $"HTTP {(int)response.StatusCode}: {Truncate(content, 300)}");

            var json = JsonDocument.Parse(content).RootElement;
            var token = SafeGetString(json, "access_token");
            if (string.IsNullOrEmpty(token))
                return (null, $"Login OK but no access_token found. Response: {Truncate(content, 300)}");

            lock (_tokenLock) { _tokenCache[cacheKey] = (token, DateTime.UtcNow.Add(TokenTtl)); }
            return (token, "OK");
        }
        catch (TaskCanceledException) { return (null, "Login timeout — SigenCloud did not respond."); }
        catch (HttpRequestException ex) { return (null, $"Network error: {ex.Message}"); }
        catch (Exception ex) { return (null, $"Login error: {ex.Message}"); }
    }

    private async Task<JsonElement?> GetAsync(string token, string url)
    {
        try
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode == 401)
                    lock (_tokenLock) { _tokenCache.Clear(); }
                _logger.LogWarning("Sigenergy GET {Url}: HTTP {Code}", url, (int)response.StatusCode);
                return null;
            }
            var content = await response.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(content).RootElement;
            if (json.TryGetProperty("data", out var data)) return data;
            return json;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sigenergy GET {Url} failed", url);
            return null;
        }
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient("default");
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    // ══════════════════════════════════════════════════════════════
    //  IInverterApiAdapter
    // ══════════════════════════════════════════════════════════════

    public async Task<(bool Success, string Message)> TestConnectionAsync(ApiCredentials credentials)
    {
        if (string.IsNullOrEmpty(credentials.Username))
            return (false, "Username (mySigen app email) is required.");
        if (string.IsNullOrEmpty(credentials.Password))
            return (false, "Password (mySigen app password) is required.");

        var (token, err) = await LoginAsync(credentials);
        if (string.IsNullOrEmpty(token))
            return (false, $"Sigenergy login failed: {err}");

        var baseUrl = GetBaseUrl(credentials);
        var data = await GetAsync(token, $"{baseUrl}/openapi/systems");
        if (data == null)
            return (false, "Logged in but failed to list systems. Check region setting (ApiKey = eu/apac/us/cn).");

        return (true, $"Connected to SigenCloud ({(credentials.ApiKey ?? "apac").ToUpperInvariant()} region)!");
    }

    public async Task<PlantInfo?> GetPlantInfoAsync(ApiCredentials credentials)
    {
        var (token, _) = await LoginAsync(credentials);
        if (token == null) return null;
        var baseUrl = GetBaseUrl(credentials);
        var data = await GetAsync(token, $"{baseUrl}/openapi/systems");
        if (data == null) return null;

        JsonElement list;
        if (data.Value.ValueKind == JsonValueKind.Array) list = data.Value;
        else if (data.Value.TryGetProperty("list", out var l)) list = l;
        else return null;

        foreach (var sys in list.EnumerateArray())
        {
            if (sys.ValueKind != JsonValueKind.Object) continue;
            var sysId = SafeGetString(sys, "systemId") ?? SafeGetString(sys, "id") ?? "";

            if (!string.IsNullOrEmpty(credentials.PlantId)
                && !sysId.Equals(credentials.PlantId, StringComparison.OrdinalIgnoreCase))
                continue;

            return new PlantInfo
            {
                PlantId = sysId,
                PlantName = SafeGetString(sys, "systemName") ?? SafeGetString(sys, "name") ?? "",
                // First non-zero — NEVER sum two field-name aliases for the
                // same metric (would double the capacity).
                CapacityKw = SafeGetDecimal(sys, "pvCapacity") is var pvCap and not 0
                    ? pvCap
                    : SafeGetDecimal(sys, "capacity"),
                Address = SafeGetString(sys, "address"),
                Latitude = SafeGetDouble(sys, "latitude"),
                Longitude = SafeGetDouble(sys, "longitude"),
            };
        }
        return null;
    }

    public async Task<RealtimeData?> GetRealtimeDataAsync(ApiCredentials credentials)
    {
        if (string.IsNullOrEmpty(credentials.PlantId)) return null;
        var (token, _) = await LoginAsync(credentials);
        if (token == null) return null;
        var baseUrl = GetBaseUrl(credentials);

        var data = await GetAsync(token, $"{baseUrl}/openapi/systems/{credentials.PlantId}/energyFlow");
        if (data == null || data.Value.ValueKind != JsonValueKind.Object) return null;
        var d = data.Value;

        // Sigenergy returns the same metric under camelCase / snake_case
        // depending on firmware; fall back to the first non-zero value
        // instead of summing them (which doubles the reading whenever both
        // forms are populated — the bug responsible for HoymilesAdapter's
        // 2000-kWh-on-a-5kW-system incident).
        decimal FirstNonZero(params string[] keys)
        {
            foreach (var k in keys)
            {
                var v = SafeGetDecimal(d, k);
                if (v != 0m) return v;
            }
            return 0m;
        }

        var pvPower   = FirstNonZero("pvPower", "pv_power");
        var gridPower = FirstNonZero("gridPower", "grid_power");
        var battSoc   = SafeGetNullableDecimal(d, "batterySoc") ?? SafeGetNullableDecimal(d, "soc");
        var loadPower = FirstNonZero("loadPower", "home_load");
        var battPower = FirstNonZero("batteryPower", "battery_power");

        if (pvPower > 500) pvPower /= 1000m;
        if (Math.Abs(gridPower) > 500) gridPower /= 1000m;

        var todayKwh = FirstNonZero("dailyPvEnergy", "daily_pv_energy", "todayEnergy");
        var totalKwh = FirstNonZero("totalPvEnergy", "total_pv_energy", "totalEnergy");

        return new RealtimeData
        {
            Timestamp = DateTime.UtcNow,
            CurrentPowerKw = pvPower,
            TodayEnergyKwh = todayKwh,
            TotalEnergyKwh = totalKwh,
            GridExportKw = gridPower < 0 ? Math.Abs(gridPower) : 0,
            GridImportKw = gridPower > 0 ? gridPower : 0,
            BatterySocPercent = battSoc,
            BatteryPowerKw = battPower,
            IsOnline = pvPower > 0 || battSoc.HasValue,
            Health = pvPower > 0 || battSoc.HasValue ? HealthState.Healthy : HealthState.OfflineNight,
        };
    }

    public async Task<List<EnergyRecord>> GetHistoricalDataAsync(
        ApiCredentials credentials, DateTime from, DateTime to)
    {
        var records = new List<EnergyRecord>();
        if (string.IsNullOrEmpty(credentials.PlantId)) return records;
        var (token, _) = await LoginAsync(credentials);
        if (token == null) return records;
        var baseUrl = GetBaseUrl(credentials);

        try
        {
            var url = $"{baseUrl}/openapi/systems/{credentials.PlantId}/statistics/daily" +
                      $"?startDate={from:yyyy-MM-dd}&endDate={to:yyyy-MM-dd}";
            var data = await GetAsync(token, url);
            if (data == null) return records;

            JsonElement arr;
            if (data.Value.ValueKind == JsonValueKind.Array) arr = data.Value;
            else if (data.Value.TryGetProperty("list", out var l)) arr = l;
            else return records;

            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var dateStr = SafeGetString(item, "date") ?? SafeGetString(item, "time");
                if (!DateTime.TryParse(dateStr, out var date)) continue;

                // First non-zero — never sum aliases (would double the daily kWh).
                decimal Pick(string a, string b)
                {
                    var v = SafeGetDecimal(item, a);
                    return v != 0m ? v : SafeGetDecimal(item, b);
                }
                records.Add(new EnergyRecord
                {
                    Timestamp = date,
                    EnergyGenerationKwh = Pick("pvEnergy", "pv_energy"),
                    GridExportKwh       = Pick("gridExportEnergy", "export"),
                    GridImportKwh       = Pick("gridImportEnergy", "import"),
                    SelfConsumptionKwh  = Pick("selfConsumption", "self_use"),
                    BatteryChargeKwh    = SafeGetDecimal(item, "batteryChargeEnergy"),
                    BatteryDischargeKwh = SafeGetDecimal(item, "batteryDischargeEnergy"),
                });
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Sigenergy GetHistoricalData failed"); }
        return records;
    }

    public async Task<List<AlarmRecord>> GetAlarmsAsync(
        ApiCredentials credentials, DateTime from, DateTime to)
    {
        var alarms = new List<AlarmRecord>();
        if (string.IsNullOrEmpty(credentials.PlantId)) return alarms;
        var (token, _) = await LoginAsync(credentials);
        if (token == null) return alarms;
        var baseUrl = GetBaseUrl(credentials);

        try
        {
            var url = $"{baseUrl}/openapi/systems/{credentials.PlantId}/alarms" +
                      $"?startDate={from:yyyy-MM-dd}&endDate={to:yyyy-MM-dd}&page=1&size=100";
            var data = await GetAsync(token, url);
            if (data == null) return alarms;

            JsonElement arr;
            if (data.Value.ValueKind == JsonValueKind.Array) arr = data.Value;
            else if (data.Value.TryGetProperty("records", out var r)) arr = r;
            else if (data.Value.TryGetProperty("list", out var l)) arr = l;
            else return alarms;

            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                alarms.Add(new AlarmRecord
                {
                    AlarmId = SafeGetString(item, "alarmId") ?? SafeGetString(item, "id") ?? "",
                    AlarmName = SafeGetString(item, "alarmName") ?? SafeGetString(item, "name") ?? "",
                    Severity = SafeGetInt(item, "level") ?? SafeGetInt(item, "severity") ?? 3,
                    OccurredAt = DateTime.TryParse(SafeGetString(item, "startTime") ?? SafeGetString(item, "time"),
                        out var dt) ? dt : DateTime.UtcNow,
                    ClearedAt = DateTime.TryParse(SafeGetString(item, "endTime"), out var ct) ? ct : null,
                    DeviceName = SafeGetString(item, "deviceName") ?? SafeGetString(item, "sn"),
                });
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Sigenergy GetAlarms failed"); }
        return alarms;
    }

    public async Task<List<DeviceInfo>> GetDevicesAsync(ApiCredentials credentials)
    {
        var devices = new List<DeviceInfo>();
        if (string.IsNullOrEmpty(credentials.PlantId)) return devices;
        var (token, _) = await LoginAsync(credentials);
        if (token == null) return devices;
        var baseUrl = GetBaseUrl(credentials);

        try
        {
            var data = await GetAsync(token, $"{baseUrl}/openapi/systems/{credentials.PlantId}/devices");
            if (data == null) return devices;

            JsonElement arr;
            if (data.Value.ValueKind == JsonValueKind.Array) arr = data.Value;
            else if (data.Value.TryGetProperty("list", out var l)) arr = l;
            else return devices;

            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                devices.Add(new DeviceInfo
                {
                    DeviceId = SafeGetString(item, "deviceId") ?? SafeGetString(item, "sn") ?? "",
                    DeviceName = SafeGetString(item, "deviceName") ?? SafeGetString(item, "name") ?? "",
                    DeviceType = SafeGetString(item, "deviceType") ?? SafeGetString(item, "type") ?? "Inverter",
                    Model = SafeGetString(item, "model") ?? SafeGetString(item, "productModel"),
                    SerialNumber = SafeGetString(item, "sn") ?? SafeGetString(item, "serialNumber"),
                    IsOnline = (SafeGetInt(item, "status") ?? 0) == 1
                        || SafeGetString(item, "status")?.Equals("online", StringComparison.OrdinalIgnoreCase) == true,
                    RatedPowerKw = SafeGetNullableDecimal(item, "ratedPower"),
                });
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Sigenergy GetDevices failed"); }
        return devices;
    }

    // ══════════════════════════════════════════════════════════════
    //  ApiResult<T> typed wrappers
    // ══════════════════════════════════════════════════════════════

    public async Task<ApiResult<bool>> TestConnectionResultAsync(ApiCredentials credentials, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var (ok, msg) = await TestConnectionAsync(credentials);
            sw.Stop();
            return ok ? ApiResult<bool>.Ok(true, msg, sw.Elapsed)
                      : ApiResult<bool>.Fail(ApiResultKind.AuthenticationFailed, msg, duration: sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<bool>(ex, "Sigenergy", sw.Elapsed); }
    }

    public async Task<ApiResult<PlantInfo>> GetPlantInfoResultAsync(ApiCredentials credentials, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var info = await GetPlantInfoAsync(credentials);
            sw.Stop();
            return info != null ? ApiResult<PlantInfo>.Ok(info, duration: sw.Elapsed)
                                : ApiResult<PlantInfo>.Fail(ApiResultKind.PlantNotFound, "System not found", duration: sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<PlantInfo>(ex, "Sigenergy", sw.Elapsed); }
    }

    public async Task<ApiResult<RealtimeData>> GetRealtimeResultAsync(ApiCredentials credentials, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var data = await GetRealtimeDataAsync(credentials);
            sw.Stop();
            if (data == null) return ApiResult<RealtimeData>.Fail(ApiResultKind.CloudServerError, "No realtime data", duration: sw.Elapsed);
            return data.IsOnline
                ? ApiResult<RealtimeData>.Ok(data, "Online", sw.Elapsed)
                : ApiResult<RealtimeData>.Offline(data, "Device offline/sleeping", sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<RealtimeData>(ex, "Sigenergy", sw.Elapsed); }
    }

    public async Task<ApiResult<List<EnergyRecord>>> GetHistoricalResultAsync(ApiCredentials credentials, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var data = await GetHistoricalDataAsync(credentials, from, to);
            sw.Stop();
            return ApiResult<List<EnergyRecord>>.Ok(data, $"{data.Count} records", sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<List<EnergyRecord>>(ex, "Sigenergy", sw.Elapsed); }
    }

    public async Task<ApiResult<List<AlarmRecord>>> GetAlarmsResultAsync(ApiCredentials credentials, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var data = await GetAlarmsAsync(credentials, from, to);
            sw.Stop();
            return ApiResult<List<AlarmRecord>>.Ok(data, $"{data.Count} alarms", sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<List<AlarmRecord>>(ex, "Sigenergy", sw.Elapsed); }
    }

    public async Task<ApiResult<List<DeviceInfo>>> GetDevicesResultAsync(ApiCredentials credentials, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var data = await GetDevicesAsync(credentials);
            sw.Stop();
            return ApiResult<List<DeviceInfo>>.Ok(data, $"{data.Count} devices", sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<List<DeviceInfo>>(ex, "Sigenergy", sw.Elapsed); }
    }

    // ══════════════════════════════════════════════════════════════
    //  Safe JSON helpers
    // ══════════════════════════════════════════════════════════════

    private static string? SafeGetString(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
        return null;
    }
    private static int? SafeGetInt(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var si)) return si;
        return null;
    }
    private static decimal SafeGetDecimal(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), out var sd)) return sd;
        return 0;
    }
    private static decimal? SafeGetNullableDecimal(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), out var sd)) return sd;
        return null;
    }
    private static double? SafeGetDouble(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        return null;
    }
    private static string Truncate(string? s, int max) => s == null ? "" : s.Length <= max ? s : s[..max] + "…";
}
