using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SolarApp.Models.Enums;
using SolarApp.Services.InverterApi.Models;

namespace SolarApp.Services.InverterApi.Adapters;

/// <summary>
/// FOX ESS Cloud API Adapter.
///
/// Supports 2 authentication modes (cannot use both simultaneously):
///
/// Mode 1 - Private Token (simple):
///   ApiKey = Private Token from foxesscloud.com > User Profile > API Management
///   Sent as "token" header on every request.
///
/// Mode 2 - OAuth (Client ID + Secret):
///   Username = Client ID from foxesscloud.com > Authorization Token tab
///   ApiSecret = Client Secret
///   Gets an access token via /oauth/token, sent as "Authorization: Bearer" header.
///
/// PlantId = Device Serial Number (SN) of the inverter.
///
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

    private async Task<HttpClient> CreateClientAsync(ApiCredentials credentials)
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("lang", "en");

        // Mode 1: Private Token (preferred if available)
        if (!string.IsNullOrEmpty(credentials.ApiKey))
        {
            _logger.LogDebug("FOX ESS: Using Private Token authentication");

            // FOX ESS Open API v1 requires signature
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            var signature = GenerateSignature(credentials.ApiKey, timestamp);

            client.DefaultRequestHeaders.Add("token", credentials.ApiKey);
            client.DefaultRequestHeaders.Add("timestamp", timestamp);
            client.DefaultRequestHeaders.Add("signature", signature);
        }
        // Mode 2: OAuth (Client ID + Secret)
        else if (!string.IsNullOrEmpty(credentials.Username) && !string.IsNullOrEmpty(credentials.ApiSecret))
        {
            _logger.LogDebug("FOX ESS: Using OAuth (Client ID + Secret) authentication");
            var accessToken = await GetOAuthTokenAsync(credentials.Username, credentials.ApiSecret);
            if (!string.IsNullOrEmpty(accessToken))
            {
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
            }
        }
        else
        {
            _logger.LogWarning("FOX ESS: No credentials provided");
        }

        return client;
    }

    private static string GenerateSignature(string token, string timestamp)
    {
        var signText = $"{token}\\r\\n{timestamp}";
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(signText));
        return Convert.ToHexString(hash).ToLower();
    }

    private async Task<string?> GetOAuthTokenAsync(string clientId, string clientSecret)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.PostAsJsonAsync(
                "https://www.foxesscloud.com/oauth/token",
                new
                {
                    grant_type = "client_credentials",
                    client_id = clientId,
                    client_secret = clientSecret
                });

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (json.TryGetProperty("access_token", out var token))
                return token.GetString();

            if (json.TryGetProperty("data", out var data) && data.TryGetProperty("access_token", out var token2))
                return token2.GetString();

            _logger.LogWarning("FOX ESS OAuth: No access_token in response");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FOX ESS OAuth token request failed");
            return null;
        }
    }

    public async Task<bool> TestConnectionAsync(ApiCredentials credentials)
    {
        try
        {
            var client = await CreateClientAsync(credentials);
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
            var client = await CreateClientAsync(credentials);
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
            var client = await CreateClientAsync(credentials);
            var response = await client.PostAsJsonAsync($"{BaseUrl}/device/real/query",
                new { sn = credentials.PlantId, variables = new[] { "pvPower", "generationPower",
                    "feedinPower", "gridConsumptionPower", "SoC", "batChargePower",
                    "batDischargePower", "generationTotal", "todayYield" } });
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();

            if (json.TryGetProperty("result", out var result))
            {
                var datas = result.EnumerateArray().ToDictionary(
                    v => v.GetProperty("variable").GetString() ?? "",
                    v => v.TryGetProperty("value", out var val) && val.TryGetDecimal(out var d) ? d : 0m);

                return new RealtimeData
                {
                    Timestamp = DateTime.UtcNow,
                    CurrentPowerKw = datas.GetValueOrDefault("pvPower"),
                    TodayEnergyKwh = datas.GetValueOrDefault("todayYield"),
                    TotalEnergyKwh = datas.GetValueOrDefault("generationTotal"),
                    GridExportKw = datas.GetValueOrDefault("feedinPower"),
                    GridImportKw = datas.GetValueOrDefault("gridConsumptionPower"),
                    BatterySocPercent = datas.TryGetValue("SoC", out var soc) && soc > 0 ? soc : null,
                    IsOnline = true
                };
            }

            return null;
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
            var client = await CreateClientAsync(credentials);
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
            var client = await CreateClientAsync(credentials);
            var response = await client.PostAsJsonAsync($"{BaseUrl}/device/fault/list", new
            {
                sn = credentials.PlantId,
                pageSize = 100,
                currentPage = 1
            });
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
            var client = await CreateClientAsync(credentials);
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
