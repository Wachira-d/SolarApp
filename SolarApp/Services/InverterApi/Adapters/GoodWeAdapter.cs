using System.Net.Http.Json;
using System.Text.Json;
using SolarApp.Models.Enums;
using SolarApp.Services.InverterApi.Models;

namespace SolarApp.Services.InverterApi.Adapters;

/// <summary>
/// Adapter for GoodWe SEMS Portal API.
/// Authentication: CrossLogin with email/password -> Token
/// Base URL: https://www.semsportal.com/api/v1/
/// </summary>
public class GoodWeAdapter : IInverterApiAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GoodWeAdapter> _logger;
    private const string BaseUrl = "https://www.semsportal.com/api/v1";

    public InverterBrand Brand => InverterBrand.GoodWe;

    public GoodWeAdapter(IHttpClientFactory httpClientFactory, ILogger<GoodWeAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<bool> TestConnectionAsync(ApiCredentials credentials)
    {
        try
        {
            var token = await LoginAsync(credentials);
            return token != null;
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
            var client = await CreateAuthenticatedClientAsync(credentials);
            if (client == null) return null;

            var response = await client.PostAsJsonAsync(
                $"{BaseUrl}/PowerStation/GetMonitorDetailByPowerstationId",
                new { powerStationId = credentials.PlantId });
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var data = json.GetProperty("data");

            return new PlantInfo
            {
                PlantId = credentials.PlantId ?? "",
                PlantName = data.TryGetProperty("info", out var info)
                    ? info.GetProperty("stationname").GetString() ?? "" : "",
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
            var client = await CreateAuthenticatedClientAsync(credentials);
            if (client == null) return null;

            var response = await client.PostAsJsonAsync(
                $"{BaseUrl}/PowerStation/GetMonitorDetailByPowerstationId",
                new { powerStationId = credentials.PlantId });
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var data = json.GetProperty("data");
            var kpi = data.GetProperty("kpiView");

            return new RealtimeData
            {
                Timestamp = DateTime.UtcNow,
                CurrentPowerKw = GetDecimal(kpi, "pac"),
                TodayEnergyKwh = GetDecimal(kpi, "eday"),
                TotalEnergyKwh = GetDecimal(kpi, "etotal"),
                IsOnline = data.TryGetProperty("info", out var info)
                    && info.TryGetProperty("status", out var s) && s.GetInt32() == 1
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get GoodWe realtime data");
            return null;
        }
    }

    public async Task<List<EnergyRecord>> GetHistoricalDataAsync(ApiCredentials credentials, DateTime from, DateTime to)
    {
        var records = new List<EnergyRecord>();
        try
        {
            var client = await CreateAuthenticatedClientAsync(credentials);
            if (client == null) return records;

            var response = await client.PostAsJsonAsync($"{BaseUrl}/PowerStation/GetPowerStationPowerAndIncomeByDay",
                new
                {
                    powerStationId = credentials.PlantId,
                    startDate = from.ToString("yyyy-MM-dd"),
                    endDate = to.ToString("yyyy-MM-dd")
                });

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

    public async Task<List<AlarmRecord>> GetAlarmsAsync(ApiCredentials credentials, DateTime from, DateTime to)
    {
        var alarms = new List<AlarmRecord>();
        try
        {
            var client = await CreateAuthenticatedClientAsync(credentials);
            if (client == null) return alarms;

            var response = await client.PostAsJsonAsync($"{BaseUrl}/PowerStation/GetPowerStationFaultList",
                new { powerStationId = credentials.PlantId, pageIndex = 1, pageSize = 100 });
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();

            if (json.TryGetProperty("data", out var data) && data.TryGetProperty("list", out var list))
            {
                foreach (var item in list.EnumerateArray())
                {
                    alarms.Add(new AlarmRecord
                    {
                        AlarmId = item.GetProperty("faultId").GetString() ?? "",
                        AlarmName = item.GetProperty("faultMessage").GetString() ?? "",
                        Severity = item.TryGetProperty("warningLevel", out var lv) ? lv.GetInt32() : 3,
                        OccurredAt = DateTime.Parse(item.GetProperty("happenTime").GetString() ?? ""),
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
            var client = await CreateAuthenticatedClientAsync(credentials);
            if (client == null) return devices;

            var response = await client.PostAsJsonAsync(
                $"{BaseUrl}/PowerStation/GetMonitorDetailByPowerstationId",
                new { powerStationId = credentials.PlantId });
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var data = json.GetProperty("data");

            if (data.TryGetProperty("inverter", out var inverters))
            {
                foreach (var item in inverters.EnumerateArray())
                {
                    devices.Add(new DeviceInfo
                    {
                        DeviceId = item.GetProperty("sn").GetString() ?? "",
                        DeviceName = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        DeviceType = "Inverter",
                        SerialNumber = item.GetProperty("sn").GetString(),
                        Model = item.TryGetProperty("model", out var m) ? m.GetString() : null,
                        IsOnline = item.TryGetProperty("status", out var s) && s.GetInt32() == 1
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

    private async Task<HttpClient?> CreateAuthenticatedClientAsync(ApiCredentials credentials)
    {
        var token = await LoginAsync(credentials);
        if (token == null) return null;

        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("Token", token);
        return client;
    }

    private async Task<string?> LoginAsync(ApiCredentials credentials)
    {
        var client = _httpClientFactory.CreateClient();
        var response = await client.PostAsJsonAsync($"{BaseUrl}/Common/CrossLogin",
            new { account = credentials.Username, pwd = credentials.Password });
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        if (json.TryGetProperty("data", out var data) && data.TryGetProperty("token", out var token))
            return token.GetString();

        return null;
    }

    private static decimal GetDecimal(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val) && val.TryGetDecimal(out var d)) return d;
        return 0;
    }
}
