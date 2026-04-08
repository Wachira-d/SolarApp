using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SolarApp.Models.Enums;
using SolarApp.Services.InverterApi.Models;

namespace SolarApp.Services.InverterApi.Adapters;

/// <summary>
/// Adapter for Solis Cloud API.
/// Authentication: API Key + Secret with HMAC signature
/// Base URL: https://www.soliscloud.com:13333/v1/api/
/// </summary>
public class SolisAdapter : IInverterApiAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SolisAdapter> _logger;
    private const string BaseUrl = "https://www.soliscloud.com:13333/v1/api";

    public InverterBrand Brand => InverterBrand.Solis;

    public SolisAdapter(IHttpClientFactory httpClientFactory, ILogger<SolisAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<bool> TestConnectionAsync(ApiCredentials credentials)
    {
        try
        {
            var result = await GetPlantInfoAsync(credentials);
            return result != null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Solis connection test failed");
            return false;
        }
    }

    public async Task<PlantInfo?> GetPlantInfoAsync(ApiCredentials credentials)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { pageNo = 1, pageSize = 1 });
            var response = await SendRequestAsync(credentials, "/stationList", body);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var station = json.GetProperty("data").GetProperty("page").GetProperty("records")
                .EnumerateArray().FirstOrDefault();

            return new PlantInfo
            {
                PlantId = station.GetProperty("id").GetString() ?? "",
                PlantName = station.GetProperty("stationName").GetString() ?? "",
                CapacityKw = GetDecimal(station, "capacity"),
                Address = station.TryGetProperty("addr", out var addr) ? addr.GetString() : null
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Solis plant info");
            return null;
        }
    }

    public async Task<RealtimeData?> GetRealtimeDataAsync(ApiCredentials credentials)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { id = credentials.PlantId });
            var response = await SendRequestAsync(credentials, "/stationDetail", body);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var data = json.GetProperty("data");

            return new RealtimeData
            {
                Timestamp = DateTime.UtcNow,
                CurrentPowerKw = GetDecimal(data, "power"),
                TodayEnergyKwh = GetDecimal(data, "dayEnergy"),
                TotalEnergyKwh = GetDecimal(data, "allEnergy"),
                IsOnline = data.TryGetProperty("state", out var s) && s.GetInt32() == 1
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Solis realtime data");
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
                timeType = 2, // daily
                startTime = from.ToString("yyyy-MM-dd"),
                endTime = to.ToString("yyyy-MM-dd")
            });
            var response = await SendRequestAsync(credentials, "/stationDay", body);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();

            if (json.TryGetProperty("data", out var data))
            {
                foreach (var item in data.EnumerateArray())
                {
                    records.Add(new EnergyRecord
                    {
                        Timestamp = DateTime.Parse(item.GetProperty("date").GetString() ?? ""),
                        EnergyGenerationKwh = GetDecimal(item, "energy"),
                        GridExportKwh = GetDecimal(item, "gridSellEnergy"),
                        GridImportKwh = GetDecimal(item, "gridPurchasedEnergy"),
                        SelfConsumptionKwh = GetDecimal(item, "homeLoadEnergy")
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Solis historical data");
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
            var response = await SendRequestAsync(credentials, "/alarmList", body);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();

            if (json.TryGetProperty("data", out var data) && data.TryGetProperty("records", out var records))
            {
                foreach (var item in records.EnumerateArray())
                {
                    alarms.Add(new AlarmRecord
                    {
                        AlarmId = item.GetProperty("alarmId").GetString() ?? "",
                        AlarmName = item.GetProperty("alarmMsg").GetString() ?? "",
                        Severity = item.TryGetProperty("alarmLevel", out var lv) ? lv.GetInt32() : 3,
                        OccurredAt = DateTime.Parse(item.GetProperty("alarmBeginTime").GetString() ?? ""),
                        DeviceName = item.TryGetProperty("inverterSn", out var sn) ? sn.GetString() : null
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Solis alarms");
        }
        return alarms;
    }

    public async Task<List<DeviceInfo>> GetDevicesAsync(ApiCredentials credentials)
    {
        var devices = new List<DeviceInfo>();
        try
        {
            var body = JsonSerializer.Serialize(new { stationId = credentials.PlantId, pageNo = 1, pageSize = 100 });
            var response = await SendRequestAsync(credentials, "/inverterList", body);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();

            if (json.TryGetProperty("data", out var data) && data.TryGetProperty("page", out var page))
            {
                foreach (var item in page.GetProperty("records").EnumerateArray())
                {
                    devices.Add(new DeviceInfo
                    {
                        DeviceId = item.GetProperty("id").GetString() ?? "",
                        DeviceName = item.TryGetProperty("collectorSn", out var cn) ? cn.GetString() ?? "" : "",
                        DeviceType = "Inverter",
                        SerialNumber = item.GetProperty("inverterSn").GetString(),
                        Model = item.TryGetProperty("inverterModel", out var m) ? m.GetString() : null,
                        IsOnline = item.TryGetProperty("state", out var s) && s.GetInt32() == 1
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Solis devices");
        }
        return devices;
    }

    private async Task<HttpResponseMessage> SendRequestAsync(ApiCredentials credentials, string path, string body)
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

        return await client.PostAsync($"{BaseUrl}{path}",
            new StringContent(body, Encoding.UTF8, contentType));
    }

    private static decimal GetDecimal(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val) && val.TryGetDecimal(out var d)) return d;
        return 0;
    }
}
