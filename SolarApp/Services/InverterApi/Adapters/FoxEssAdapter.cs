using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SolarApp.Models.Enums;
using SolarApp.Services.InverterApi.Common;
using SolarApp.Services.InverterApi.Models;

namespace SolarApp.Services.InverterApi.Adapters;

/// <summary>
/// FOX ESS Cloud OpenAPI Adapter (spec v1.1.5).
///
/// Authentication modes (mutually exclusive):
///
/// Mode 1 - Private Token + MD5 Signature (PRIMARY):
///   ApiKey = Private Token from foxesscloud.com > User Profile > API Management
///
///   Required headers per request:
///     Content-Type: application/json
///     token: {api_key}
///     timestamp: {unix_ms}
///     signature: MD5(path + "\r\n" + token + "\r\n" + timestamp).ToLowerHex()
///     lang: en
///
///   CRITICAL: "\r\n" in signText means the LITERAL 4 characters
///   backslash-r-backslash-n, NOT the CR+LF control characters.
///   Signature must be regenerated for every request (path changes).
///
/// Mode 2 - OAuth Client Credentials (Authorization Token tab):
///   Username = Client ID, ApiSecret = Client Secret
///   POST /oauth/token -> access_token -> "Authorization: Bearer {token}" header
///
/// PlantId = Device Serial Number (SN) for device-specific queries.
///
/// Docs: https://www.foxesscloud.com/public/i18n/en/OpenApiDocument.html
/// </summary>
public class FoxEssAdapter : IInverterApiAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FoxEssAdapter> _logger;
    private const string BaseUrl = "https://www.foxesscloud.com";

    public InverterBrand Brand => InverterBrand.FoxESS;

    public FoxEssAdapter(IHttpClientFactory httpClientFactory, ILogger<FoxEssAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // ══════════════════════════════════════════════════════════════
    //  PUBLIC API METHODS
    // ══════════════════════════════════════════════════════════════

    public async Task<(bool Success, string Message)> TestConnectionAsync(ApiCredentials credentials)
    {
        try
        {
            var json = await SendPostAsync(credentials, "/op/v0/plant/list",
                new { currentPage = 1, pageSize = 1 });
            var ok = json.HasValue && GetErrno(json.Value) == 0;
            return (ok, ok ? "Connected to FOX ESS!" : $"FOX ESS errno={GetErrno(json ?? default)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FOX ESS TestConnection failed");
            return (false, $"FOX ESS error: {ex.Message}");
        }
    }

    public async Task<PlantInfo?> GetPlantInfoAsync(ApiCredentials credentials)
    {
        try
        {
            var list = await SendPostAsync(credentials, "/op/v0/plant/list",
                new { currentPage = 1, pageSize = 10 });

            if (!list.HasValue || GetErrno(list.Value) != 0) return null;

            var plants = list.Value.GetProperty("result").GetProperty("data");
            var plant = plants.EnumerateArray().FirstOrDefault();
            if (plant.ValueKind == JsonValueKind.Undefined) return null;

            var stationId = GetString(plant, "stationID") ?? "";
            var stationName = GetString(plant, "name") ?? "";

            // Get detail for capacity and address
            var detail = await SendGetAsync(credentials, "/op/v0/plant/detail", $"id={stationId}");

            decimal capacity = 0;
            string? address = null;
            if (detail.HasValue && GetErrno(detail.Value) == 0)
            {
                var d = detail.Value.GetProperty("result");
                capacity = GetDecimal(d, "capacity");
                address = GetString(d, "address");
            }

            return new PlantInfo
            {
                PlantId = stationId,
                PlantName = stationName,
                CapacityKw = capacity,
                Address = address
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FOX ESS GetPlantInfo failed");
            return null;
        }
    }

    public async Task<RealtimeData?> GetRealtimeDataAsync(ApiCredentials credentials)
    {
        if (string.IsNullOrEmpty(credentials.PlantId))
        {
            _logger.LogWarning("FOX ESS: PlantId (device SN) is required for realtime data");
            return null;
        }

        try
        {
            var json = await SendPostAsync(credentials, "/op/v1/device/real/query", new
            {
                sns = new[] { credentials.PlantId },
                variables = new[]
                {
                    "pvPower", "generationPower", "feedinPower",
                    "gridConsumptionPower", "SoC", "batChargePower",
                    "batDischargePower", "generationTotal", "todayYield"
                }
            });

            if (!json.HasValue || GetErrno(json.Value) != 0) return null;

            var device = json.Value.GetProperty("result").EnumerateArray().FirstOrDefault();
            if (device.ValueKind == JsonValueKind.Undefined) return null;

            // Build a dictionary of variable -> value (safe against null/wrong-type)
            var values = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            if (device.TryGetProperty("datas", out var datas) && datas.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in datas.EnumerateArray())
                {
                    if (d.ValueKind != JsonValueKind.Object) continue;
                    var name = GetString(d, "variable");
                    if (string.IsNullOrEmpty(name)) continue;
                    var dec = GetDecimal(d, "value");
                    if (dec != 0 || (d.TryGetProperty("value", out var vv) && vv.ValueKind == JsonValueKind.Number))
                        values[name] = dec;
                }
            }

            decimal Get(string k) => values.TryGetValue(k, out var v) ? v : 0;

            long? timeMs = null;
            if (device.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var tms))
                timeMs = tms;

            return new RealtimeData
            {
                Timestamp = timeMs.HasValue
                    ? DateTimeOffset.FromUnixTimeMilliseconds(timeMs.Value).UtcDateTime
                    : DateTime.UtcNow,
                CurrentPowerKw = Get("pvPower") != 0 ? Get("pvPower") : Get("generationPower"),
                TodayEnergyKwh = Get("todayYield"),
                TotalEnergyKwh = Get("generationTotal"),
                GridExportKw = Get("feedinPower"),
                GridImportKw = Get("gridConsumptionPower"),
                BatterySocPercent = values.TryGetValue("SoC", out var soc) ? soc : null,
                IsOnline = values.Count > 0
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FOX ESS GetRealtimeData failed");
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
            // history/query has 24h max window, so use report/query for daily data instead
            var years = EnumerateMonths(from, to);

            foreach (var ym in years)
            {
                var json = await SendPostAsync(credentials, "/op/v0/device/report/query", new
                {
                    sn = credentials.PlantId,
                    year = ym.Year,
                    month = ym.Month,
                    dimension = "month",
                    variables = new[] { "generation", "feedin", "gridConsumption" }
                });

                if (!json.HasValue || GetErrno(json.Value) != 0) continue;

                var daysInMonth = DateTime.DaysInMonth(ym.Year, ym.Month);
                var dayValues = new decimal[daysInMonth];
                var exportValues = new decimal[daysInMonth];
                var importValues = new decimal[daysInMonth];

                foreach (var variable in json.Value.GetProperty("result").EnumerateArray())
                {
                    var name = GetString(variable, "variable");
                    if (!variable.TryGetProperty("values", out var vals)) continue;

                    var arr = vals.EnumerateArray().Select(v =>
                    {
                        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
                        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(),
                            System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d2))
                            return d2;
                        return 0m;
                    }).ToArray();
                    var target = name switch
                    {
                        "generation" => dayValues,
                        "feedin" => exportValues,
                        "gridConsumption" => importValues,
                        _ => null
                    };
                    if (target == null) continue;
                    for (int i = 0; i < Math.Min(arr.Length, target.Length); i++)
                        target[i] = arr[i];
                }

                for (int i = 0; i < daysInMonth; i++)
                {
                    var date = new DateTime(ym.Year, ym.Month, i + 1);
                    if (date < from.Date || date > to.Date) continue;
                    if (dayValues[i] == 0 && exportValues[i] == 0 && importValues[i] == 0) continue;

                    records.Add(new EnergyRecord
                    {
                        Timestamp = date,
                        EnergyGenerationKwh = dayValues[i],
                        GridExportKwh = exportValues[i],
                        GridImportKwh = importValues[i],
                        SelfConsumptionKwh = Math.Max(0, dayValues[i] - exportValues[i])
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FOX ESS GetHistoricalData failed");
        }
        return records;
    }

    public async Task<List<AlarmRecord>> GetAlarmsAsync(
        ApiCredentials credentials, DateTime from, DateTime to)
    {
        var alarms = new List<AlarmRecord>();
        // FOX ESS OpenAPI doesn't expose a dedicated alarm/fault list endpoint in public spec.
        // Fault definitions can be fetched via /op/v0/device/fault/get but not device-specific events.
        // Return empty list - alarms will come from variable-based health monitoring instead.
        await Task.CompletedTask;
        return alarms;
    }

    public async Task<List<DeviceInfo>> GetDevicesAsync(ApiCredentials credentials)
    {
        var devices = new List<DeviceInfo>();
        try
        {
            var json = await SendPostAsync(credentials, "/op/v0/device/list",
                new { currentPage = 1, pageSize = 100 });

            if (!json.HasValue || GetErrno(json.Value) != 0) return devices;

            var data = json.Value.GetProperty("result").GetProperty("data");
            foreach (var item in data.EnumerateArray())
            {
                devices.Add(new DeviceInfo
                {
                    DeviceId = GetString(item, "deviceSN") ?? "",
                    DeviceName = GetString(item, "stationName") ?? "",
                    DeviceType = GetString(item, "deviceType") ?? "",
                    SerialNumber = GetString(item, "deviceSN"),
                    Model = GetString(item, "productType"),
                    IsOnline = item.TryGetProperty("status", out var s)
                        && s.ValueKind == JsonValueKind.Number
                        && s.TryGetInt32(out var sv) && sv == 1
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FOX ESS GetDevices failed");
        }
        return devices;
    }

    // ══════════════════════════════════════════════════════════════
    //  SIGNED HTTP REQUESTS
    // ══════════════════════════════════════════════════════════════

    private async Task<JsonElement?> SendPostAsync(ApiCredentials credentials, string path, object body)
    {
        var client = await CreateClientAsync(credentials, path);
        if (client == null) return null;

        var response = await client.PostAsJsonAsync(BaseUrl + path, body);
        return await ReadResponseAsync(response, path);
    }

    private async Task<JsonElement?> SendGetAsync(ApiCredentials credentials, string path, string? queryString = null)
    {
        var client = await CreateClientAsync(credentials, path);
        if (client == null) return null;

        var url = BaseUrl + path;
        if (!string.IsNullOrEmpty(queryString))
            url += "?" + queryString;

        var response = await client.GetAsync(url);
        return await ReadResponseAsync(response, path);
    }

    private async Task<HttpClient?> CreateClientAsync(ApiCredentials credentials, string path)
    {
        // Mode 1: Private Token + signature (preferred if ApiKey is set)
        if (!string.IsNullOrEmpty(credentials.ApiKey))
        {
            var client = _httpClientFactory.CreateClient();
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            var signature = GenerateSignature(path, credentials.ApiKey, timestamp);

            client.DefaultRequestHeaders.Add("token", credentials.ApiKey);
            client.DefaultRequestHeaders.Add("timestamp", timestamp);
            client.DefaultRequestHeaders.Add("signature", signature);
            client.DefaultRequestHeaders.Add("lang", "en");
            return client;
        }

        // Mode 2: OAuth Client Credentials
        if (!string.IsNullOrEmpty(credentials.Username) && !string.IsNullOrEmpty(credentials.ApiSecret))
        {
            var accessToken = await GetOAuthTokenAsync(credentials.Username, credentials.ApiSecret);
            if (string.IsNullOrEmpty(accessToken)) return null;

            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
            client.DefaultRequestHeaders.Add("lang", "en");
            return client;
        }

        _logger.LogWarning("FOX ESS: no credentials provided");
        return null;
    }

    /// <summary>
    /// Generate MD5 signature per FOX ESS OpenAPI spec.
    /// IMPORTANT: "\r\n" in the signature string are the LITERAL 4 characters
    /// backslash + r + backslash + n, NOT CR+LF control characters.
    /// </summary>
    private static string GenerateSignature(string path, string token, string timestamp)
    {
        // Literal backslash-r-backslash-n (4 chars): use @"\r\n" verbatim string
        var signText = path + @"\r\n" + token + @"\r\n" + timestamp;
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(signText));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task<string?> GetOAuthTokenAsync(string clientId, string clientSecret)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.PostAsJsonAsync($"{BaseUrl}/oauth/token",
                new { grant_type = "client_credentials", client_id = clientId, client_secret = clientSecret });

            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (json.TryGetProperty("access_token", out var tok)) return tok.GetString();
            if (json.TryGetProperty("data", out var d) && d.TryGetProperty("access_token", out var tok2))
                return tok2.GetString();

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FOX ESS OAuth token failed");
            return null;
        }
    }

    private async Task<JsonElement?> ReadResponseAsync(HttpResponseMessage response, string path)
    {
        var content = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("FOX ESS {Path}: HTTP {Code} - {Body}",
                path, (int)response.StatusCode, Truncate(content, 200));
            return null;
        }

        try
        {
            var json = JsonDocument.Parse(content).RootElement;
            var errno = GetErrno(json);
            if (errno != 0)
            {
                _logger.LogWarning("FOX ESS {Path}: errno={Errno} msg={Msg}",
                    path, errno, GetString(json, "msg"));
            }
            return json;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "FOX ESS {Path}: invalid JSON response", path);
            return null;
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  HELPERS
    // ══════════════════════════════════════════════════════════════

    private static int GetErrno(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return -1;
        if (!el.TryGetProperty("errno", out var v)) return -1;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var n2)) return n2;
        return -1;
    }

    private static string? GetString(JsonElement el, string prop)
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

    private static decimal GetDecimal(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return 0;
        if (!el.TryGetProperty(prop, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(),
            System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d2))
            return d2;
        return 0;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";

    private static IEnumerable<(int Year, int Month)> EnumerateMonths(DateTime from, DateTime to)
    {
        var cur = new DateTime(from.Year, from.Month, 1);
        var end = new DateTime(to.Year, to.Month, 1);
        while (cur <= end)
        {
            yield return (cur.Year, cur.Month);
            cur = cur.AddMonths(1);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  ApiResult<T> methods
    // ═══════════════════════════════════════════════════════════

    public async Task<ApiResult<bool>> TestConnectionResultAsync(ApiCredentials credentials, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var (ok, msg) = await TestConnectionAsync(credentials);
            sw.Stop();
            return ok ? ApiResult<bool>.Ok(true, msg, sw.Elapsed)
                      : ApiResultClassifier.ClassifyErrorMessage<bool>(msg, "FoxESS", sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<bool>(ex, "FoxESS", sw.Elapsed); }
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
                    $"Plant '{credentials.PlantId}' not found on FoxESS account.", duration: sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<PlantInfo>(ex, "FoxESS", sw.Elapsed); }
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
                    "FoxESS API returned no data. Could be server issue or PlantId mismatch.", duration: sw.Elapsed);
            return data.IsOnline
                ? ApiResult<RealtimeData>.Ok(data, "Device online", sw.Elapsed)
                : ApiResult<RealtimeData>.Offline(data,
                    "FoxESS API connected OK, but device reports offline. Check inverter datalogger.",
                    sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<RealtimeData>(ex, "FoxESS", sw.Elapsed); }
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
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<List<EnergyRecord>>(ex, "FoxESS", sw.Elapsed); }
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
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<List<AlarmRecord>>(ex, "FoxESS", sw.Elapsed); }
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
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<List<DeviceInfo>>(ex, "FoxESS", sw.Elapsed); }
    }
}
