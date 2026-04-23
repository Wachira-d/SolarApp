using System.Net.Http.Json;
using System.Text.Json;
using SolarApp.Models.Enums;
using SolarApp.Services.InverterApi.Models;

namespace SolarApp.Services.InverterApi.Adapters;

/// <summary>
/// Adapter for Huawei FusionSolar Northbound API.
/// Authentication: Username/Password -> Session Token
/// Base URL: https://{region}.fusionsolar.huawei.com/thirdData/
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
        try
        {
            var client = _httpClientFactory.CreateClient();
            var token = await LoginAsync(client, credentials);
            var ok = !string.IsNullOrEmpty(token);
            return (ok, ok ? "Connected to Huawei FusionSolar!" : "Login failed: no token returned");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Huawei FusionSolar connection test failed");
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

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var station = json.GetProperty("data").GetProperty("list").EnumerateArray().FirstOrDefault();

            return new PlantInfo
            {
                PlantId = station.GetProperty("stationCode").GetString() ?? "",
                PlantName = station.GetProperty("stationName").GetString() ?? "",
                CapacityKw = station.GetProperty("capacity").GetDecimal(),
                Address = station.TryGetProperty("stationAddr", out var addr) ? addr.GetString() : null
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Huawei plant info");
            return null;
        }
    }

    public async Task<RealtimeData?> GetRealtimeDataAsync(ApiCredentials credentials)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var token = await LoginAsync(client, credentials);
            if (token == null) return null;

            client.DefaultRequestHeaders.Add("XSRF-TOKEN", token);
            var response = await client.PostAsJsonAsync($"{BaseUrl}/getStationRealKpi",
                new { stationCodes = credentials.PlantId });

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var data = json.GetProperty("data").EnumerateArray().FirstOrDefault();
            var kpi = data.GetProperty("dataItemMap");

            return new RealtimeData
            {
                Timestamp = DateTime.UtcNow,
                CurrentPowerKw = GetDecimal(kpi, "real_health_state") >= 0 ? GetDecimal(kpi, "day_power") : 0,
                TodayEnergyKwh = GetDecimal(kpi, "day_power"),
                TotalEnergyKwh = GetDecimal(kpi, "total_power"),
                IsOnline = GetDecimal(kpi, "real_health_state") >= 1
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Huawei realtime data");
            return null;
        }
    }

    public async Task<List<EnergyRecord>> GetHistoricalDataAsync(ApiCredentials credentials, DateTime from, DateTime to)
    {
        var records = new List<EnergyRecord>();
        try
        {
            var client = _httpClientFactory.CreateClient();
            var token = await LoginAsync(client, credentials);
            if (token == null) return records;

            client.DefaultRequestHeaders.Add("XSRF-TOKEN", token);
            var collectTime = new DateTimeOffset(from).ToUnixTimeMilliseconds();
            var response = await client.PostAsJsonAsync($"{BaseUrl}/getKpiStationHour",
                new { stationCodes = credentials.PlantId, collectTime });

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();

            if (json.TryGetProperty("data", out var dataArr))
            {
                foreach (var item in dataArr.EnumerateArray())
                {
                    var kpi = item.GetProperty("dataItemMap");
                    records.Add(new EnergyRecord
                    {
                        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(
                            item.GetProperty("collectTime").GetInt64()).DateTime,
                        EnergyGenerationKwh = GetDecimal(kpi, "inverter_power"),
                        GridExportKwh = GetDecimal(kpi, "ongrid_power"),
                        SelfConsumptionKwh = GetDecimal(kpi, "selfuse_power")
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Huawei historical data");
        }
        return records;
    }

    public async Task<List<AlarmRecord>> GetAlarmsAsync(ApiCredentials credentials, DateTime from, DateTime to)
    {
        var alarms = new List<AlarmRecord>();
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

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();

            if (json.TryGetProperty("data", out var data) && data.TryGetProperty("list", out var list))
            {
                foreach (var item in list.EnumerateArray())
                {
                    alarms.Add(new AlarmRecord
                    {
                        AlarmId = item.GetProperty("alarmId").GetString() ?? "",
                        AlarmName = item.GetProperty("alarmName").GetString() ?? "",
                        Severity = item.GetProperty("severity").GetInt32(),
                        OccurredAt = DateTimeOffset.FromUnixTimeMilliseconds(
                            item.GetProperty("raiseTime").GetInt64()).DateTime,
                        DeviceName = item.TryGetProperty("devName", out var dev) ? dev.GetString() : null
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Huawei alarms");
        }
        return alarms;
    }

    public async Task<List<DeviceInfo>> GetDevicesAsync(ApiCredentials credentials)
    {
        var devices = new List<DeviceInfo>();
        try
        {
            var client = _httpClientFactory.CreateClient();
            var token = await LoginAsync(client, credentials);
            if (token == null) return devices;

            client.DefaultRequestHeaders.Add("XSRF-TOKEN", token);
            var response = await client.PostAsJsonAsync($"{BaseUrl}/getDevList",
                new { stationCodes = credentials.PlantId });

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();

            if (json.TryGetProperty("data", out var dataArr))
            {
                foreach (var item in dataArr.EnumerateArray())
                {
                    devices.Add(new DeviceInfo
                    {
                        DeviceId = item.GetProperty("id").GetString() ?? "",
                        DeviceName = item.GetProperty("devName").GetString() ?? "",
                        DeviceType = item.GetProperty("devTypeId").GetString() ?? "",
                        SerialNumber = item.TryGetProperty("esnCode", out var esn) ? esn.GetString() : null,
                        IsOnline = item.TryGetProperty("devStatus", out var s) && s.GetInt32() == 1
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Huawei devices");
        }
        return devices;
    }

    private async Task<string?> LoginAsync(HttpClient client, ApiCredentials credentials)
    {
        var response = await client.PostAsJsonAsync($"{BaseUrl}/login",
            new { userName = credentials.Username, systemCode = credentials.Password });

        if (response.Headers.TryGetValues("XSRF-TOKEN", out var tokens))
            return tokens.FirstOrDefault();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (json.TryGetProperty("data", out var data) && data.TryGetProperty("token", out var token))
            return token.GetString();

        return null;
    }

    private static decimal GetDecimal(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var val) && val.TryGetDecimal(out var d))
            return d;
        return 0;
    }
}
