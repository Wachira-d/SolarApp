using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SolarApp.Models.Enums;
using SolarApp.Services.InverterApi.Models;

namespace SolarApp.Services.InverterApi.Adapters;

/// <summary>
/// Solis Cloud API Adapter.
///
/// Mode 1 - Official API (HMAC-SHA1 signature):
///   ApiKey = API Key ID, ApiSecret = API Key Secret
///   Get from: SolisCloud > User Profile > API Management
///
/// Mode 2 - Reverse Web Login (unofficial, when ApiKey = "SOLISWEB"):
///   Username = SolisCloud email, Password = SolisCloud password
///   Uses the same login flow as the web dashboard.
///
/// PlantId = Station ID (numeric, e.g. 1298491919450550810)
/// </summary>
public class SolisAdapter : IInverterApiAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SolisAdapter> _logger;
    private const string BaseUrl = "https://www.soliscloud.com:13333/v1/api";
    private const string WebBaseUrl = "https://www.soliscloud.com";

    public InverterBrand Brand => InverterBrand.Solis;

    public SolisAdapter(IHttpClientFactory httpClientFactory, ILogger<SolisAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private bool IsWebMode(ApiCredentials c) =>
        string.Equals(c.ApiKey, "SOLISWEB", StringComparison.OrdinalIgnoreCase);

    public async Task<(bool Success, string Message)> TestConnectionAsync(ApiCredentials credentials)
    {
        if (IsWebMode(credentials))
        {
            if (string.IsNullOrEmpty(credentials.Username))
                return (false, "Username (SolisCloud email) is required for SOLISWEB mode");
            if (string.IsNullOrEmpty(credentials.Password))
                return (false, "Password (SolisCloud password) is required for SOLISWEB mode");
        }
        else
        {
            if (string.IsNullOrEmpty(credentials.ApiKey))
                return (false, "API Key ID is required (get from SolisCloud > User Profile > API Management)");
            if (string.IsNullOrEmpty(credentials.ApiSecret))
                return (false, "API Secret is required");
        }

        try
        {
            if (IsWebMode(credentials))
            {
                var token = await WebLoginAsync(credentials);
                if (string.IsNullOrEmpty(token))
                    return (false, "Solis web login failed. Check username/password.");

                var plant = await GetPlantInfoAsync(credentials);
                return plant != null
                    ? (true, $"Connected to Solis Cloud (web mode)! Plant: {plant.PlantName}")
                    : (true, "Solis web login OK but no plant data found. Check Station ID.");
            }

            var body = JsonSerializer.Serialize(new { pageNo = 1, pageSize = 1 });
            var (response, error) = await SendRequestWithDetailAsync(credentials, "/stationList", body);

            if (response == null)
                return (false, $"Solis API call failed: {error}");

            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return (false, $"Solis HTTP {(int)response.StatusCode}: {Truncate(content, 300)}");

            var json = JsonDocument.Parse(content).RootElement;

            if (json.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.False)
            {
                var msg = SafeGetString(json, "msg") ?? SafeGetString(json, "message") ?? content;
                return (false, $"Solis API error: {msg}");
            }

            if (!json.TryGetProperty("data", out var data))
                return (false, $"Solis: no 'data' in response. Body: {Truncate(content, 300)}");

            return (true, "Connected to Solis Cloud (API mode)!");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Solis TestConnection failed");
            return (false, $"Solis error: {ex.Message}");
        }
    }

    public async Task<PlantInfo?> GetPlantInfoAsync(ApiCredentials credentials)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { pageNo = 1, pageSize = 1 });
            var json = await SendAndParseAsync(credentials, "/stationList", body);
            if (json == null) return null;

            JsonElement station;
            if (json.Value.TryGetProperty("data", out var data))
            {
                if (data.TryGetProperty("page", out var page) && page.TryGetProperty("records", out var recs))
                    station = recs.EnumerateArray().FirstOrDefault();
                else if (data.TryGetProperty("records", out var recs2))
                    station = recs2.EnumerateArray().FirstOrDefault();
                else if (data.ValueKind == JsonValueKind.Array)
                    station = data.EnumerateArray().FirstOrDefault();
                else
                    return null;
            }
            else return null;

            if (station.ValueKind == JsonValueKind.Undefined) return null;

            return new PlantInfo
            {
                PlantId = SafeGetString(station, "id") ?? SafeGetString(station, "stationId") ?? "",
                PlantName = SafeGetString(station, "stationName") ?? SafeGetString(station, "name") ?? "",
                CapacityKw = SafeGetDecimal(station, "capacity"),
                Address = SafeGetString(station, "addr") ?? SafeGetString(station, "address")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Solis GetPlantInfo failed");
            return null;
        }
    }

    public async Task<RealtimeData?> GetRealtimeDataAsync(ApiCredentials credentials)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { id = credentials.PlantId });
            var json = await SendAndParseAsync(credentials, "/stationDetail", body);
            if (json == null) return null;

            if (!json.Value.TryGetProperty("data", out var data)) return null;

            return new RealtimeData
            {
                Timestamp = DateTime.UtcNow,
                CurrentPowerKw = SafeGetDecimal(data, "power"),
                TodayEnergyKwh = SafeGetDecimal(data, "dayEnergy"),
                TotalEnergyKwh = SafeGetDecimal(data, "allEnergy"),
                GridExportKw = SafeGetDecimal(data, "gridSellPower"),
                GridImportKw = SafeGetDecimal(data, "gridPurchasedPower"),
                BatterySocPercent = SafeGetNullableDecimal(data, "batteryPercent"),
                IsOnline = SafeGetInt(data, "state") == 1
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Solis GetRealtimeData failed");
            return null;
        }
    }

    public async Task<List<EnergyRecord>> GetHistoricalDataAsync(ApiCredentials credentials, DateTime from, DateTime to)
    {
        var records = new List<EnergyRecord>();
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                id = credentials.PlantId,
                timeType = 2,
                startTime = from.ToString("yyyy-MM-dd"),
                endTime = to.ToString("yyyy-MM-dd")
            });
            var json = await SendAndParseAsync(credentials, "/stationDay", body);
            if (json == null) return records;

            if (json.Value.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    try
                    {
                        var dateStr = SafeGetString(item, "date") ?? SafeGetString(item, "dataTimestamp");
                        if (dateStr == null || !DateTime.TryParse(dateStr, out var date)) continue;

                        records.Add(new EnergyRecord
                        {
                            Timestamp = date,
                            EnergyGenerationKwh = SafeGetDecimal(item, "energy"),
                            GridExportKwh = SafeGetDecimal(item, "gridSellEnergy"),
                            GridImportKwh = SafeGetDecimal(item, "gridPurchasedEnergy"),
                            SelfConsumptionKwh = SafeGetDecimal(item, "homeLoadEnergy")
                        });
                    }
                    catch (Exception itemEx)
                    {
                        _logger.LogWarning(itemEx, "Solis: skipping malformed history record");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Solis GetHistoricalData failed");
        }
        return records;
    }

    public async Task<List<AlarmRecord>> GetAlarmsAsync(ApiCredentials credentials, DateTime from, DateTime to)
    {
        var alarms = new List<AlarmRecord>();
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                stationId = credentials.PlantId,
                pageNo = 1,
                pageSize = 100,
                beginTime = from.ToString("yyyy-MM-dd HH:mm:ss"),
                endTime = to.ToString("yyyy-MM-dd HH:mm:ss")
            });
            var json = await SendAndParseAsync(credentials, "/alarmList", body);
            if (json == null) return alarms;

            if (json.Value.TryGetProperty("data", out var data) && data.TryGetProperty("records", out var recs))
            {
                foreach (var item in recs.EnumerateArray())
                {
                    try
                    {
                        alarms.Add(new AlarmRecord
                        {
                            AlarmId = SafeGetString(item, "alarmId") ?? SafeGetString(item, "id") ?? "",
                            AlarmName = SafeGetString(item, "alarmMsg") ?? SafeGetString(item, "alarmMessage") ?? "",
                            Severity = SafeGetInt(item, "alarmLevel") ?? 3,
                            OccurredAt = DateTime.TryParse(SafeGetString(item, "alarmBeginTime"), out var dt)
                                ? dt : DateTime.UtcNow,
                            DeviceName = SafeGetString(item, "inverterSn") ?? SafeGetString(item, "deviceSn")
                        });
                    }
                    catch (Exception itemEx)
                    {
                        _logger.LogWarning(itemEx, "Solis: skipping malformed alarm record");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Solis GetAlarms failed");
        }
        return alarms;
    }

    public async Task<List<DeviceInfo>> GetDevicesAsync(ApiCredentials credentials)
    {
        var devices = new List<DeviceInfo>();
        try
        {
            var body = JsonSerializer.Serialize(new { stationId = credentials.PlantId, pageNo = 1, pageSize = 100 });
            var json = await SendAndParseAsync(credentials, "/inverterList", body);
            if (json == null) return devices;

            JsonElement records;
            if (json.Value.TryGetProperty("data", out var data))
            {
                if (data.TryGetProperty("page", out var page) && page.TryGetProperty("records", out var r))
                    records = r;
                else if (data.TryGetProperty("records", out var r2))
                    records = r2;
                else if (data.ValueKind == JsonValueKind.Array)
                    records = data;
                else
                    return devices;
            }
            else return devices;

            foreach (var item in records.EnumerateArray())
            {
                devices.Add(new DeviceInfo
                {
                    DeviceId = SafeGetString(item, "id") ?? SafeGetString(item, "inverterId") ?? "",
                    DeviceName = SafeGetString(item, "collectorSn") ?? SafeGetString(item, "sn") ?? "",
                    DeviceType = "Inverter",
                    SerialNumber = SafeGetString(item, "inverterSn") ?? SafeGetString(item, "sn"),
                    Model = SafeGetString(item, "inverterModel") ?? SafeGetString(item, "model"),
                    IsOnline = SafeGetInt(item, "state") == 1
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Solis GetDevices failed");
        }
        return devices;
    }

    // ══════════════════════════════════════════════════════════════
    //  HTTP + HMAC-SHA1 Signature
    // ══════════════════════════════════════════════════════════════

    private async Task<JsonElement?> SendAndParseAsync(ApiCredentials credentials, string path, string body)
    {
        // Route to web mode if SOLISWEB
        if (IsWebMode(credentials))
            return await SendWebRequestAsync(credentials, path, body);

        var (response, error) = await SendRequestWithDetailAsync(credentials, path, body);
        if (response == null)
        {
            _logger.LogWarning("Solis {Path}: request failed - {Error}", path, error);
            return null;
        }

        var content = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Solis {Path}: HTTP {Code} - {Body}",
                path, (int)response.StatusCode, Truncate(content, 300));
            return null;
        }

        try
        {
            var json = JsonDocument.Parse(content).RootElement;

            if (json.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.False)
            {
                _logger.LogWarning("Solis {Path}: API error - {Msg}",
                    path, SafeGetString(json, "msg") ?? content);
                return null;
            }

            return json;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Solis {Path}: invalid JSON", path);
            return null;
        }
    }

    private async Task<(HttpResponseMessage? Response, string? Error)> SendRequestWithDetailAsync(
        ApiCredentials credentials, string path, string body)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var contentMd5 = Convert.ToBase64String(MD5.HashData(Encoding.UTF8.GetBytes(body)));
            var date = DateTime.UtcNow.ToString("R");
            var contentType = "application/json";

            var signStr = $"POST\n{contentMd5}\n{contentType}\n{date}\n{path}";
            using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(credentials.ApiSecret ?? ""));
            var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(signStr)));

            client.DefaultRequestHeaders.Add("Authorization", $"API {credentials.ApiKey}:{signature}");
            client.DefaultRequestHeaders.Add("Date", date);
            client.DefaultRequestHeaders.Add("Content-MD5", contentMd5);

            _logger.LogDebug("Solis: POST {Url}{Path}", BaseUrl, path);

            var response = await client.PostAsync($"{BaseUrl}{path}",
                new StringContent(body, Encoding.UTF8, contentType));

            return (response, null);
        }
        catch (HttpRequestException ex)
        {
            return (null, $"Network error: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return (null, "Request timed out");
        }
        catch (Exception ex)
        {
            return (null, $"Exception: {ex.Message}");
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  SOLISWEB mode: Reverse Web Login (unofficial)
    // ══════════════════════════════════════════════════════════════

    private string? _webToken;

    private async Task<string?> WebLoginAsync(ApiCredentials credentials)
    {
        if (!string.IsNullOrEmpty(_webToken)) return _webToken;

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 SolarApp/1.0");

            var response = await client.PostAsJsonAsync($"{WebBaseUrl}/u/login", new
            {
                userInfo = credentials.Username,
                passWord = credentials.Password
            });

            var body = await response.Content.ReadAsStringAsync();
            _logger.LogInformation("Solis web login: HTTP {Code}, len={Len}", (int)response.StatusCode, body.Length);

            if (!response.IsSuccessStatusCode) return null;

            var json = JsonDocument.Parse(body).RootElement;

            // Try multiple known response structures
            if (json.TryGetProperty("data", out var data))
            {
                _webToken = SafeGetString(data, "token") ?? SafeGetString(data, "csrfToken");
                if (!string.IsNullOrEmpty(_webToken))
                {
                    _logger.LogInformation("Solis web login OK, token obtained");
                    return _webToken;
                }
            }

            // Some versions return token at root
            _webToken = SafeGetString(json, "token") ?? SafeGetString(json, "csrfToken");
            if (!string.IsNullOrEmpty(_webToken)) return _webToken;

            _logger.LogWarning("Solis web login: no token found. Response: {Body}", Truncate(body, 300));
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Solis web login failed");
            return null;
        }
    }

    private async Task<JsonElement?> SendWebRequestAsync(ApiCredentials credentials, string path, string body)
    {
        try
        {
            var token = await WebLoginAsync(credentials);
            if (string.IsNullOrEmpty(token))
            {
                _logger.LogWarning("Solis web: no token, cannot make request");
                return null;
            }

            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("token", token);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 SolarApp/1.0");

            var response = await client.PostAsync($"{WebBaseUrl}/v2/api{path}",
                new StringContent(body, Encoding.UTF8, "application/json"));

            var content = await response.Content.ReadAsStringAsync();
            _logger.LogDebug("Solis web {Path}: HTTP {Code}", path, (int)response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Solis web {Path}: HTTP {Code}: {Body}", path, (int)response.StatusCode, Truncate(content, 200));
                return null;
            }

            return JsonDocument.Parse(content).RootElement;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Solis web {Path} failed", path);
            return null;
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  Safe JSON helpers (handle Number/String/Null gracefully)
    // ══════════════════════════════════════════════════════════════

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
}
