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
            var response = await client.PostAsJsonAsync($"{BaseUrl}/getStationList",
                new { pageNo = 1, pageSize = 1 });
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
        try
        {
            var client = _httpClientFactory.CreateClient();
            var token = await LoginAsync(client, credentials);
            if (token == null) return null;

            client.DefaultRequestHeaders.Add("XSRF-TOKEN", token);
            var response = await client.PostAsJsonAsync($"{BaseUrl}/getStationList",
                new { pageNo = 1, pageSize = 1 });
            if (!response.IsSuccessStatusCode) return null;

            var json = await ReadJsonAsync(response);
            if (json == null) return null;

            var station = FindFirstArrayItem(json.Value, "data", "list");
            if (station == null) return null;

            return new PlantInfo
            {
                PlantId = SafeGetString(station.Value, "stationCode")
                         ?? SafeGetString(station.Value, "plantCode") ?? "",
                PlantName = SafeGetString(station.Value, "stationName")
                           ?? SafeGetString(station.Value, "plantName") ?? "",
                CapacityKw = SafeGetDecimal(station.Value, "capacity"),
                Address = SafeGetString(station.Value, "stationAddr")
                         ?? SafeGetString(station.Value, "plantAddress")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Huawei GetPlantInfo failed");
            return null;
        }
    }

    public async Task<RealtimeData?> GetRealtimeDataAsync(ApiCredentials credentials)
    {
        if (string.IsNullOrEmpty(credentials.PlantId)) return null;

        try
        {
            var client = _httpClientFactory.CreateClient();
            var token = await LoginAsync(client, credentials);
            if (token == null) return null;

            client.DefaultRequestHeaders.Add("XSRF-TOKEN", token);
            var response = await client.PostAsJsonAsync($"{BaseUrl}/getStationRealKpi",
                new { stationCodes = credentials.PlantId });
            if (!response.IsSuccessStatusCode) return null;

            var json = await ReadJsonAsync(response);
            if (json == null) return null;

            if (!json.Value.TryGetProperty("data", out var dataArr)
                || dataArr.ValueKind != JsonValueKind.Array) return null;

            var first = dataArr.EnumerateArray().FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Object) return null;

            if (!first.TryGetProperty("dataItemMap", out var kpi)
                || kpi.ValueKind != JsonValueKind.Object) return null;

            var health = SafeGetDecimal(kpi, "real_health_state");

            // Huawei NBI station-level KPI semantics:
            //   day_power   = today's accumulated energy (kWh) — NOT instantaneous power
            //   total_power = lifetime accumulated energy (kWh)
            //   real_health_state: 0=offline, 1=faulty, 2=healthy
            // Instantaneous power must come from /getDevRealKpi (device-level).
            decimal currentPowerKw = 0;
            try
            {
                currentPowerKw = await FetchStationActivePowerKwAsync(client, credentials.PlantId!);
            }
            catch (Exception powerEx)
            {
                _logger.LogDebug(powerEx, "Huawei: could not fetch device-level active_power (non-fatal)");
            }

            return new RealtimeData
            {
                Timestamp = DateTime.UtcNow,
                CurrentPowerKw = currentPowerKw,
                TodayEnergyKwh = SafeGetDecimal(kpi, "day_power"),
                TotalEnergyKwh = SafeGetDecimal(kpi, "total_power"),
                IsOnline = health >= 1
            };
        }
        catch (Exception ex)
        {
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
        var devices = new List<DeviceInfo>();
        if (string.IsNullOrEmpty(credentials.PlantId)) return devices;

        try
        {
            var client = _httpClientFactory.CreateClient();
            var token = await LoginAsync(client, credentials);
            if (token == null) return devices;

            client.DefaultRequestHeaders.Add("XSRF-TOKEN", token);
            var response = await client.PostAsJsonAsync($"{BaseUrl}/getDevList",
                new { stationCodes = credentials.PlantId });
            if (!response.IsSuccessStatusCode) return devices;

            var json = await ReadJsonAsync(response);
            if (json == null) return devices;

            if (!json.Value.TryGetProperty("data", out var dataArr)
                || dataArr.ValueKind != JsonValueKind.Array) return devices;

            foreach (var item in dataArr.EnumerateArray())
            {
                try
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;

                    devices.Add(new DeviceInfo
                    {
                        DeviceId = SafeGetString(item, "id") ?? SafeGetString(item, "devDn") ?? "",
                        DeviceName = SafeGetString(item, "devName") ?? "",
                        DeviceType = SafeGetString(item, "devTypeId") ?? "",
                        SerialNumber = SafeGetString(item, "esnCode"),
                        IsOnline = SafeGetInt(item, "devStatus") == 1
                    });
                }
                catch (Exception itemEx)
                {
                    _logger.LogWarning(itemEx, "Huawei: skipping malformed device item");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Huawei GetDevices failed");
        }
        return devices;
    }

    // ══════════════════════════════════════════════════════════════
    //  Authentication
    // ══════════════════════════════════════════════════════════════

    private async Task<string?> LoginAsync(HttpClient client, ApiCredentials credentials)
    {
        var (token, _) = await LoginWithDetailAsync(client, credentials);
        return token;
    }

    private async Task<(string? Token, string Error)> LoginWithDetailAsync(HttpClient client, ApiCredentials credentials)
    {
        try
        {
            var response = await client.PostAsJsonAsync($"{BaseUrl}/login",
                new { userName = credentials.Username, systemCode = credentials.Password });

            // Token is in XSRF-TOKEN header
            if (response.Headers.TryGetValues("XSRF-TOKEN", out var tokens))
            {
                var token = tokens.FirstOrDefault();
                if (!string.IsNullOrEmpty(token)) return (token, "OK");
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
            return info != null
                ? ApiResult<PlantInfo>.Ok(info, "Plant info retrieved", sw.Elapsed)
                : ApiResult<PlantInfo>.Fail(ApiResultKind.PlantNotFound,
                    $"Plant '{credentials.PlantId}' not found on Huawei account.", duration: sw.Elapsed);
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
                return ApiResult<RealtimeData>.Fail(ApiResultKind.CloudServerError,
                    "Huawei API returned no data. Could be server issue or incorrect PlantId.", duration: sw.Elapsed);
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
            return ApiResult<List<DeviceInfo>>.Ok(list, $"{list.Count} device(s) fetched", sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<List<DeviceInfo>>(ex, "Huawei", sw.Elapsed); }
    }

}
