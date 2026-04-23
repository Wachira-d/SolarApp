using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SolarApp.Models.Enums;
using SolarApp.Services.InverterApi.Models;

namespace SolarApp.Services.InverterApi.Adapters;

/// <summary>
/// Adapter for Deye Cloud API.
/// Authentication: Username/Password -> Access Token -> Bearer Token
/// Base URL: https://eu1-developer.deyecloud.com/v1.0/
/// </summary>
public class DeyeAdapter : IInverterApiAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DeyeAdapter> _logger;
    private const string BaseUrl = "https://eu1-developer.deyecloud.com/v1.0";

    public InverterBrand Brand => InverterBrand.Deye;

    public DeyeAdapter(IHttpClientFactory httpClientFactory, ILogger<DeyeAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(ApiCredentials credentials)
    {
        try
        {
            var token = await GetTokenAsync(credentials);
            var ok = !string.IsNullOrEmpty(token);
            return (ok, ok ? "Connected to Deye Cloud!" : "Login failed: no token returned");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deye connection test failed");
            return (false, $"Deye error: {ex.Message}");
        }
    }

    public async Task<PlantInfo?> GetPlantInfoAsync(ApiCredentials credentials)
    {
        try
        {
            var client = await CreateAuthenticatedClientAsync(credentials);
            if (client == null) return null;

            var response = await client.PostAsJsonAsync($"{BaseUrl}/station/latest",
                new { stationId = credentials.PlantId });
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var data = json.GetProperty("data");

            return new PlantInfo
            {
                PlantId = credentials.PlantId ?? "",
                PlantName = data.TryGetProperty("stationName", out var name) ? name.GetString() ?? "" : "",
                CapacityKw = GetDecimal(data, "installedCapacity"),
                Address = data.TryGetProperty("locationAddress", out var addr) ? addr.GetString() : null
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Deye plant info");
            return null;
        }
    }

    public async Task<RealtimeData?> GetRealtimeDataAsync(ApiCredentials credentials)
    {
        try
        {
            var client = await CreateAuthenticatedClientAsync(credentials);
            if (client == null) return null;

            var response = await client.PostAsJsonAsync($"{BaseUrl}/station/latest",
                new { stationId = credentials.PlantId });
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var data = json.GetProperty("data");

            return new RealtimeData
            {
                Timestamp = DateTime.UtcNow,
                CurrentPowerKw = GetDecimal(data, "generationPower") / 1000m,
                TodayEnergyKwh = GetDecimal(data, "generationToday"),
                TotalEnergyKwh = GetDecimal(data, "generationTotal"),
                BatterySocPercent = GetNullableDecimal(data, "batterySoc"),
                IsOnline = data.TryGetProperty("status", out var s) && s.GetInt32() == 1
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Deye realtime data");
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

            var response = await client.PostAsJsonAsync($"{BaseUrl}/device/originalData",
                new
                {
                    deviceSn = credentials.PlantId,
                    startDay = from.ToString("yyyy-MM-dd"),
                    endDay = to.ToString("yyyy-MM-dd")
                });

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (json.TryGetProperty("data", out var data))
            {
                foreach (var item in data.EnumerateArray())
                {
                    records.Add(new EnergyRecord
                    {
                        Timestamp = DateTime.Parse(item.GetProperty("date").GetString() ?? ""),
                        EnergyGenerationKwh = GetDecimal(item, "generation"),
                        GridExportKwh = GetDecimal(item, "gridExport"),
                        GridImportKwh = GetDecimal(item, "gridImport"),
                        SelfConsumptionKwh = GetDecimal(item, "selfConsumption"),
                        BatteryChargeKwh = GetNullableDecimal(item, "batteryCharge"),
                        BatteryDischargeKwh = GetNullableDecimal(item, "batteryDischarge")
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Deye historical data");
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

            var response = await client.PostAsJsonAsync($"{BaseUrl}/device/fault",
                new { deviceSn = credentials.PlantId, pageNo = 1, pageSize = 100 });
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();

            if (json.TryGetProperty("data", out var data) && data.TryGetProperty("records", out var records))
            {
                foreach (var item in records.EnumerateArray())
                {
                    alarms.Add(new AlarmRecord
                    {
                        AlarmId = item.GetProperty("id").GetString() ?? "",
                        AlarmName = item.GetProperty("message").GetString() ?? "",
                        Severity = item.TryGetProperty("level", out var lv) ? lv.GetInt32() : 3,
                        OccurredAt = DateTime.Parse(item.GetProperty("createTime").GetString() ?? ""),
                        DeviceName = item.TryGetProperty("deviceSn", out var sn) ? sn.GetString() : null
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Deye alarms");
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

            var response = await client.PostAsJsonAsync($"{BaseUrl}/device/list",
                new { stationId = credentials.PlantId, pageNo = 1, pageSize = 100 });
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();

            if (json.TryGetProperty("data", out var data) && data.TryGetProperty("records", out var records))
            {
                foreach (var item in records.EnumerateArray())
                {
                    devices.Add(new DeviceInfo
                    {
                        DeviceId = item.GetProperty("deviceSn").GetString() ?? "",
                        DeviceName = item.TryGetProperty("deviceName", out var name) ? name.GetString() ?? "" : "",
                        DeviceType = item.TryGetProperty("deviceType", out var dt) ? dt.GetString() ?? "" : "",
                        SerialNumber = item.GetProperty("deviceSn").GetString(),
                        IsOnline = item.TryGetProperty("status", out var s) && s.GetInt32() == 1
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Deye devices");
        }
        return devices;
    }

    private async Task<HttpClient?> CreateAuthenticatedClientAsync(ApiCredentials credentials)
    {
        var token = await GetTokenAsync(credentials);
        if (token == null) return null;

        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<string?> GetTokenAsync(ApiCredentials credentials)
    {
        var client = _httpClientFactory.CreateClient();
        var response = await client.PostAsJsonAsync($"{BaseUrl}/token",
            new
            {
                appId = credentials.ApiKey,
                appSecret = credentials.ApiSecret,
                email = credentials.Username,
                password = credentials.Password
            });

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (json.TryGetProperty("data", out var data) && data.TryGetProperty("accessToken", out var token))
            return token.GetString();

        return null;
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
