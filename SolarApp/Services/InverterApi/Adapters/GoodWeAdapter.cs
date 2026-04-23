using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SolarApp.Models.Enums;
using SolarApp.Services.InverterApi.Models;

namespace SolarApp.Services.InverterApi.Adapters;

/// <summary>
/// GoodWe Adapter - supports 2 authentication modes:
///
/// Mode 1 - Official SEMS API (CrossLogin):
///   Username = SEMS account email
///   Password = SEMS account password
///   Uses /api/v1/Common/CrossLogin → gets Token → used in subsequent requests
///
/// Mode 2 - SEMS+ Reverse API (unofficial web login):
///   Username = SEMS+ account email
///   Password = SEMS+ account password
///   ApiKey = "SEMSPLUS" (flag to enable this mode)
///   Logs in via the SEMS+ web portal, captures session/token from response,
///   then uses same endpoints that the SEMS+ web dashboard uses.
///   NOTE: This is unofficial and may break if GoodWe changes their web app.
///
/// PlantId = Power Station ID
/// </summary>
public class GoodWeAdapter : IInverterApiAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GoodWeAdapter> _logger;
    private const string SemsApiUrl = "https://www.semsportal.com/api/v1";
    private const string SemsPlusUrl = "https://www.semsportal.com/api/v2";

    public InverterBrand Brand => InverterBrand.GoodWe;

    public GoodWeAdapter(IHttpClientFactory httpClientFactory, ILogger<GoodWeAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private bool IsSemsPlus(ApiCredentials c) =>
        string.Equals(c.ApiKey, "SEMSPLUS", StringComparison.OrdinalIgnoreCase);

    public async Task<bool> TestConnectionAsync(ApiCredentials credentials)
    {
        try
        {
            var (client, baseUrl) = await CreateAuthenticatedClientAsync(credentials);
            return client != null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GoodWe connection test failed");
            return false;
        }
    }

    public async Task<PlantInfo?> GetPlantInfoAsync(ApiCredentials credentials)
    {
        try
        {
            var (client, baseUrl) = await CreateAuthenticatedClientAsync(credentials);
            if (client == null) return null;

            var response = await client.PostAsJsonAsync(
                $"{baseUrl}/PowerStation/GetMonitorDetailByPowerstationId",
                new { powerStationId = credentials.PlantId });

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GoodWe GetPlantInfo: {Code}", response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (!json.TryGetProperty("data", out var data)) return null;

            return new PlantInfo
            {
                PlantId = credentials.PlantId ?? "",
                PlantName = data.TryGetProperty("info", out var info)
                    ? (info.TryGetProperty("stationname", out var sn) ? sn.GetString() ?? "" : "") : "",
                CapacityKw = data.TryGetProperty("kpiView", out var kpi)
                    ? GetDecimal(kpi, "capacity") : 0,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get GoodWe plant info");
            return null;
        }
    }

    public async Task<RealtimeData?> GetRealtimeDataAsync(ApiCredentials credentials)
    {
        try
        {
            var (client, baseUrl) = await CreateAuthenticatedClientAsync(credentials);
            if (client == null) return null;

            var response = await client.PostAsJsonAsync(
                $"{baseUrl}/PowerStation/GetMonitorDetailByPowerstationId",
                new { powerStationId = credentials.PlantId });

            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (!json.TryGetProperty("data", out var data)) return null;

            var kpi = data.TryGetProperty("kpiView", out var k) ? k : default;
            var info = data.TryGetProperty("info", out var i) ? i : default;

            return new RealtimeData
            {
                Timestamp = DateTime.UtcNow,
                CurrentPowerKw = GetDecimal(kpi, "pac"),
                TodayEnergyKwh = GetDecimal(kpi, "eday"),
                TotalEnergyKwh = GetDecimal(kpi, "etotal"),
                BatterySocPercent = GetNullableDecimal(kpi, "soc"),
                IsOnline = info.ValueKind != JsonValueKind.Undefined
                    && info.TryGetProperty("status", out var s) && s.GetInt32() == 1
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get GoodWe realtime data");
            return null;
        }
    }

    public async Task<List<EnergyRecord>> GetHistoricalDataAsync(
        ApiCredentials credentials, DateTime from, DateTime to)
    {
        var records = new List<EnergyRecord>();
        try
        {
            var (client, baseUrl) = await CreateAuthenticatedClientAsync(credentials);
            if (client == null) return records;

            var response = await client.PostAsJsonAsync(
                $"{baseUrl}/PowerStation/GetPowerStationPowerAndIncomeByDay",
                new
                {
                    powerStationId = credentials.PlantId,
                    startDate = from.ToString("yyyy-MM-dd"),
                    endDate = to.ToString("yyyy-MM-dd")
                });

            if (!response.IsSuccessStatusCode) return records;

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (json.TryGetProperty("data", out var data))
            {
                foreach (var item in data.EnumerateArray())
                {
                    records.Add(new EnergyRecord
                    {
                        Timestamp = DateTime.Parse(item.GetProperty("date").GetString() ?? ""),
                        EnergyGenerationKwh = GetDecimal(item, "eday"),
                        GridExportKwh = GetDecimal(item, "gridSell"),
                        GridImportKwh = GetDecimal(item, "gridBuy"),
                        SelfConsumptionKwh = GetDecimal(item, "selfUse")
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get GoodWe historical data");
        }
        return records;
    }

    public async Task<List<AlarmRecord>> GetAlarmsAsync(
        ApiCredentials credentials, DateTime from, DateTime to)
    {
        var alarms = new List<AlarmRecord>();
        try
        {
            var (client, baseUrl) = await CreateAuthenticatedClientAsync(credentials);
            if (client == null) return alarms;

            var response = await client.PostAsJsonAsync(
                $"{baseUrl}/PowerStation/GetPowerStationFaultList",
                new { powerStationId = credentials.PlantId, pageIndex = 1, pageSize = 100 });

            if (!response.IsSuccessStatusCode) return alarms;

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (json.TryGetProperty("data", out var data) && data.TryGetProperty("list", out var list))
            {
                foreach (var item in list.EnumerateArray())
                {
                    alarms.Add(new AlarmRecord
                    {
                        AlarmId = item.TryGetProperty("faultId", out var fid) ? fid.GetString() ?? "" : "",
                        AlarmName = item.TryGetProperty("faultMessage", out var fm) ? fm.GetString() ?? "" : "",
                        Severity = item.TryGetProperty("warningLevel", out var lv) ? lv.GetInt32() : 3,
                        OccurredAt = item.TryGetProperty("happenTime", out var ht)
                            ? DateTime.Parse(ht.GetString() ?? DateTime.UtcNow.ToString()) : DateTime.UtcNow,
                        DeviceName = item.TryGetProperty("inverterSN", out var sn) ? sn.GetString() : null
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get GoodWe alarms");
        }
        return alarms;
    }

    public async Task<List<DeviceInfo>> GetDevicesAsync(ApiCredentials credentials)
    {
        var devices = new List<DeviceInfo>();
        try
        {
            var (client, baseUrl) = await CreateAuthenticatedClientAsync(credentials);
            if (client == null) return devices;

            var response = await client.PostAsJsonAsync(
                $"{baseUrl}/PowerStation/GetMonitorDetailByPowerstationId",
                new { powerStationId = credentials.PlantId });

            if (!response.IsSuccessStatusCode) return devices;

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (!json.TryGetProperty("data", out var data)) return devices;

            if (data.TryGetProperty("inverter", out var inverters))
            {
                foreach (var item in inverters.EnumerateArray())
                {
                    devices.Add(new DeviceInfo
                    {
                        DeviceId = item.TryGetProperty("sn", out var snVal) ? snVal.GetString() ?? "" : "",
                        DeviceName = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        DeviceType = "Inverter",
                        SerialNumber = item.TryGetProperty("sn", out var snv) ? snv.GetString() : null,
                        Model = item.TryGetProperty("model", out var m) ? m.GetString() : null,
                        IsOnline = item.TryGetProperty("status", out var st) && st.GetInt32() == 1
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get GoodWe devices");
        }
        return devices;
    }

    // ══════════════════════════════════════════════════════════════
    //  AUTHENTICATION
    // ══════════════════════════════════════════════════════════════

    private async Task<(HttpClient? Client, string BaseUrl)> CreateAuthenticatedClientAsync(
        ApiCredentials credentials)
    {
        if (IsSemsPlus(credentials))
            return await CreateSemsPlusClientAsync(credentials);

        return await CreateSemsClientAsync(credentials);
    }

    // ── Mode 1: Official SEMS CrossLogin API ──
    private async Task<(HttpClient? Client, string BaseUrl)> CreateSemsClientAsync(
        ApiCredentials credentials)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.PostAsJsonAsync($"{SemsApiUrl}/Common/CrossLogin",
                new { account = credentials.Username, pwd = credentials.Password });

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GoodWe SEMS login failed: {Code}", response.StatusCode);
                return (null, SemsApiUrl);
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();

            string? token = null;
            if (json.TryGetProperty("data", out var data))
            {
                if (data.TryGetProperty("token", out var tk))
                    token = tk.GetString();
            }

            if (string.IsNullOrEmpty(token))
            {
                _logger.LogWarning("GoodWe SEMS: no token in login response");
                return (null, SemsApiUrl);
            }

            var authedClient = _httpClientFactory.CreateClient();
            authedClient.DefaultRequestHeaders.Add("Token", token);

            _logger.LogDebug("GoodWe SEMS login OK");
            return (authedClient, SemsApiUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GoodWe SEMS login error");
            return (null, SemsApiUrl);
        }
    }

    // ── Mode 2: SEMS+ Reverse API (unofficial web login) ──
    // Uses the same web flow that the SEMS+ portal uses:
    // 1) POST login with email/password
    // 2) Capture token/session from response
    // 3) Use token in subsequent API calls to v2 endpoints
    private async Task<(HttpClient? Client, string BaseUrl)> CreateSemsPlusClientAsync(
        ApiCredentials credentials)
    {
        try
        {
            _logger.LogDebug("GoodWe SEMS+ reverse login attempt");

            var handler = new HttpClientHandler
            {
                UseCookies = true,
                CookieContainer = new CookieContainer()
            };
            var client = new HttpClient(handler) { BaseAddress = new Uri("https://www.semsportal.com") };

            // Step 1: Login via web endpoint (same as browser does)
            var loginPayload = new
            {
                account = credentials.Username,
                pwd = credentials.Password
            };

            client.DefaultRequestHeaders.Add("Accept", "application/json");
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

            var loginResponse = await client.PostAsJsonAsync("/api/v2/Common/CrossLogin", loginPayload);

            if (!loginResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("GoodWe SEMS+ login failed: {Code}", loginResponse.StatusCode);
                return (null, SemsPlusUrl);
            }

            var loginJson = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();

            string? token = null;
            string? uid = null;
            string? timestamp = null;

            if (loginJson.TryGetProperty("data", out var data))
            {
                token = data.TryGetProperty("token", out var tk) ? tk.GetString() : null;
                uid = data.TryGetProperty("uid", out var u) ? u.GetString() : null;
                timestamp = data.TryGetProperty("timestamp", out var ts) ? ts.GetString() : null;
            }

            if (string.IsNullOrEmpty(token))
            {
                _logger.LogWarning("GoodWe SEMS+: no token returned");
                return (null, SemsPlusUrl);
            }

            // Step 2: Create authenticated client with captured token
            var authedClient = new HttpClient(handler) { BaseAddress = new Uri("https://www.semsportal.com") };
            authedClient.DefaultRequestHeaders.Add("Token", token);
            authedClient.DefaultRequestHeaders.Add("Accept", "application/json");
            authedClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

            if (!string.IsNullOrEmpty(uid))
                authedClient.DefaultRequestHeaders.Add("Uid", uid);
            if (!string.IsNullOrEmpty(timestamp))
                authedClient.DefaultRequestHeaders.Add("Timestamp", timestamp);

            _logger.LogDebug("GoodWe SEMS+ login OK (token captured)");
            return (authedClient, SemsPlusUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GoodWe SEMS+ reverse login error");
            return (null, SemsPlusUrl);
        }
    }

    private static decimal GetDecimal(JsonElement el, string prop)
    {
        if (el.ValueKind == JsonValueKind.Undefined) return 0;
        if (el.TryGetProperty(prop, out var val))
        {
            if (val.TryGetDecimal(out var d)) return d;
            if (val.ValueKind == JsonValueKind.String && decimal.TryParse(val.GetString(), out var d2)) return d2;
        }
        return 0;
    }

    private static decimal? GetNullableDecimal(JsonElement el, string prop)
    {
        if (el.ValueKind == JsonValueKind.Undefined) return null;
        if (el.TryGetProperty(prop, out var val))
        {
            if (val.TryGetDecimal(out var d)) return d;
            if (val.ValueKind == JsonValueKind.String && decimal.TryParse(val.GetString(), out var d2)) return d2;
        }
        return null;
    }
}
