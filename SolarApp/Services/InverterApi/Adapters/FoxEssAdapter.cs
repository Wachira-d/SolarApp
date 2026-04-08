using System.Net.Http.Json;
using System.Text.Json;
using SolarApp.Models.Enums;
using SolarApp.Services.InverterApi.Models;

namespace SolarApp.Services.InverterApi.Adapters;

/// <summary>
/// Adapter for FOX ESS Cloud API.
/// Authentication: API Key in header (token)
/// Base URL: https://www.foxesscloud.com/op/v0/
/// </summary>
public class FoxEssAdapter : IInverterApiAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FoxEssAdapter> _logger;
    private const string BaseUrl = "https://www.foxesscloud.com/op/v0";

    public InverterBrand Brand => InverterBrand.FoxESS;

    public FoxEssAdapter(IHttpClientFactory httpClientFactory, ILogger<FoxEssAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private HttpClient CreateClient(ApiCredentials credentials)
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("token", credentials.ApiKey);
        client.DefaultRequestHeaders.Add("lang", "en");
        return client;
    }

    public async Task<bool> TestConnectionAsync(ApiCredentials credentials)
    {
        try
        {
            var client = CreateClient(credentials);
            var response = await client.GetAsync($"{BaseUrl}/device/list?pageSize=1&currentPage=1");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FOX ESS connection test failed");
            return false;
        }
    }

    public async Task<PlantInfo?> GetPlantInfoAsync(ApiCredentials credentials)
    {
        try
        {
            var client = CreateClient(credentials);
            var response = await client.GetAsync($"{BaseUrl}/plant/list?pageSize=1&currentPage=1");
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var plant = json.GetProperty("result").GetProperty("data").EnumerateArray().FirstOrDefault();

            return new PlantInfo
            {
                PlantId = plant.GetProperty("stationID").GetString() ?? "",
                PlantName = plant.GetProperty("stationName").GetString() ?? "",
                CapacityKw = GetDecimal(plant, "capacity"),
                Address = plant.TryGetProperty("address", out var addr) ? addr.GetString() : null
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get FOX ESS plant info");
            return null;
        }
    }

    public async Task<RealtimeData?> GetRealtimeDataAsync(ApiCredentials credentials)
    {
        try
        {
            var client = CreateClient(credentials);
            var response = await client.GetAsync(
                $"{BaseUrl}/device/real/query?sn={credentials.PlantId}");
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var data = json.GetProperty("result");

            return new RealtimeData
            {
                Timestamp = DateTime.UtcNow,
                CurrentPowerKw = GetDecimal(data, "pvPower"),
                TodayEnergyKwh = GetDecimal(data, "todayYield"),
                TotalEnergyKwh = GetDecimal(data, "cumulativeYield"),
                GridExportKw = GetDecimal(data, "feedinPower"),
                GridImportKw = GetDecimal(data, "gridConsumptionPower"),
                BatterySocPercent = GetNullableDecimal(data, "SoC"),
                IsOnline = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get FOX ESS realtime data");
            return null;
        }
    }

    public async Task<List<EnergyRecord>> GetHistoricalDataAsync(ApiCredentials credentials, DateTime from, DateTime to)
    {
        var records = new List<EnergyRecord>();
        try
        {
            var client = CreateClient(credentials);
            var response = await client.PostAsJsonAsync($"{BaseUrl}/device/history/query", new
            {
                sn = credentials.PlantId,
                beginDate = from.ToString("yyyy-MM-dd"),
                endDate = to.ToString("yyyy-MM-dd"),
                timeType = "day"
            });

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (json.TryGetProperty("result", out var result))
            {
                foreach (var item in result.EnumerateArray())
                {
                    records.Add(new EnergyRecord
                    {
                        Timestamp = DateTime.Parse(item.GetProperty("date").GetString() ?? ""),
                        EnergyGenerationKwh = GetDecimal(item, "generation"),
                        GridExportKwh = GetDecimal(item, "feedin"),
                        GridImportKwh = GetDecimal(item, "gridConsumption"),
                        SelfConsumptionKwh = GetDecimal(item, "chargeEnergyToTal"),
                        BatteryChargeKwh = GetNullableDecimal(item, "batChargEnergy"),
                        BatteryDischargeKwh = GetNullableDecimal(item, "batDischargeEnergy")
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get FOX ESS historical data");
        }
        return records;
    }

    public async Task<List<AlarmRecord>> GetAlarmsAsync(ApiCredentials credentials, DateTime from, DateTime to)
    {
        var alarms = new List<AlarmRecord>();
        try
        {
            var client = CreateClient(credentials);
            var response = await client.GetAsync(
                $"{BaseUrl}/device/fault/list?sn={credentials.PlantId}&pageSize=100&currentPage=1");
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();

            if (json.TryGetProperty("result", out var result) && result.TryGetProperty("data", out var data))
            {
                foreach (var item in data.EnumerateArray())
                {
                    alarms.Add(new AlarmRecord
                    {
                        AlarmId = item.GetProperty("faultCode").GetString() ?? "",
                        AlarmName = item.GetProperty("faultMessage").GetString() ?? "",
                        Severity = item.TryGetProperty("faultType", out var ft) ? ft.GetInt32() : 3,
                        OccurredAt = DateTime.Parse(item.GetProperty("beginDate").GetString() ?? ""),
                        DeviceName = item.TryGetProperty("deviceSN", out var sn) ? sn.GetString() : null
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get FOX ESS alarms");
        }
        return alarms;
    }

    public async Task<List<DeviceInfo>> GetDevicesAsync(ApiCredentials credentials)
    {
        var devices = new List<DeviceInfo>();
        try
        {
            var client = CreateClient(credentials);
            var response = await client.GetAsync($"{BaseUrl}/device/list?pageSize=100&currentPage=1");
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();

            if (json.TryGetProperty("result", out var result) && result.TryGetProperty("data", out var data))
            {
                foreach (var item in data.EnumerateArray())
                {
                    devices.Add(new DeviceInfo
                    {
                        DeviceId = item.GetProperty("deviceSN").GetString() ?? "",
                        DeviceName = item.TryGetProperty("deviceName", out var name) ? name.GetString() ?? "" : "",
                        DeviceType = item.TryGetProperty("deviceType", out var dt) ? dt.GetString() ?? "" : "",
                        SerialNumber = item.GetProperty("deviceSN").GetString(),
                        IsOnline = item.TryGetProperty("status", out var s) && s.GetInt32() == 1
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get FOX ESS devices");
        }
        return devices;
    }

    private static decimal GetDecimal(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val) && val.TryGetDecimal(out var d)) return d;
        return 0;
    }

    private static decimal? GetNullableDecimal(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val) && val.TryGetDecimal(out var d)) return d;
        return null;
    }
}
