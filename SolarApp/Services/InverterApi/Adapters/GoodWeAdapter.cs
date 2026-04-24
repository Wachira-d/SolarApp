using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SolarApp.Models.Enums;
using SolarApp.Services.InverterApi.Common;
using SolarApp.Services.InverterApi.Models;

namespace SolarApp.Services.InverterApi.Adapters;

/// <summary>
/// GoodWe SEMS / SEMS+ Portal Adapter.
///
/// Authentication flow (same for both SEMS and SEMS+):
/// 1. POST /api/v1/Common/CrossLogin (or /api/v2/...) with initial Token header:
///    Token: {"version":"v2.1.0","client":"web","language":"en"}
///    Body: {"account": "email", "pwd": "password"}
///
/// 2. Response returns:
///    {"hasError":false,"code":0,"data":{"uid":"...","timestamp":...,"token":"...","api":"https://xx.semsportal.com/api/"}}
///    The "api" field tells which regional server to use for subsequent requests.
///
/// 3. For subsequent API calls, Token header must be the JSON-serialized data object:
///    Token: {"uid":"...","timestamp":...,"token":"...","client":"web","version":"v2.1.0","language":"en"}
///
/// Credentials mapping:
///   Username = SEMS email
///   Password = SEMS password
///   PlantId  = Power Station ID (GUID from URL)
///   ApiKey   = "SEMSPLUS" (optional flag to use v2 endpoint)
/// </summary>
public class GoodWeAdapter : IInverterApiAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GoodWeAdapter> _logger;

    // Multiple regional login endpoints - SEMS redirects to the correct one
    private const string DefaultLoginUrl = "https://www.semsportal.com/api/v2/Common/CrossLogin";

    public InverterBrand Brand => InverterBrand.GoodWe;

    public GoodWeAdapter(IHttpClientFactory httpClientFactory, ILogger<GoodWeAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(ApiCredentials credentials)
    {
        try
        {
            if (string.IsNullOrEmpty(credentials.Username))
                return (false, "Username (SEMS email) is required");
            if (string.IsNullOrEmpty(credentials.Password))
                return (false, "Password is required");

            var (session, loginError) = await LoginWithDetailAsync(credentials);
            if (session == null)
                return (false, $"Login failed: {loginError}");

            if (!string.IsNullOrEmpty(credentials.PlantId))
            {
                var json = await PostAsync(session, "v2/PowerStation/GetMonitorDetailByPowerstationId",
                    new { powerStationId = credentials.PlantId });
                if (json == null)
                    return (false, $"Login OK but Plant ID '{credentials.PlantId}' not accessible. Check Station ID.");

                if (json.Value.TryGetProperty("hasError", out var he) && he.ValueKind == JsonValueKind.True)
                    return (false, $"Login OK but Plant query error: {GetString(json.Value, "msg") ?? "unknown"}");
            }

            return (true, $"Connected to GoodWe SEMS! API: {session.ApiBase}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GoodWe test connection error");
            return (false, $"Exception: {ex.Message}");
        }
    }

    public async Task<PlantInfo?> GetPlantInfoAsync(ApiCredentials credentials)
    {
        var session = await LoginAsync(credentials);
        if (session == null) return null;

        var json = await PostAsync(session, "v2/PowerStation/GetMonitorDetailByPowerstationId",
            new { powerStationId = credentials.PlantId });
        if (json == null) return null;

        var data = json.Value.TryGetProperty("data", out var d) ? d : default;
        if (data.ValueKind == JsonValueKind.Undefined) return null;

        string? name = null;
        decimal capacity = 0;
        string? address = null;

        if (data.TryGetProperty("info", out var info))
        {
            name = GetString(info, "stationname") ?? GetString(info, "name");
            address = GetString(info, "address");
            capacity = GetDecimal(info, "capacity");
        }

        if (capacity == 0 && data.TryGetProperty("kpi", out var kpi))
            capacity = GetDecimal(kpi, "capacity");

        return new PlantInfo
        {
            PlantId = credentials.PlantId ?? "",
            PlantName = name ?? "",
            CapacityKw = capacity,
            Address = address
        };
    }

    public async Task<RealtimeData?> GetRealtimeDataAsync(ApiCredentials credentials)
    {
        var session = await LoginAsync(credentials);
        if (session == null) return null;

        var json = await PostAsync(session, "v2/PowerStation/GetMonitorDetailByPowerstationId",
            new { powerStationId = credentials.PlantId });
        if (json == null) return null;

        var data = json.Value.TryGetProperty("data", out var d) ? d : default;
        if (data.ValueKind == JsonValueKind.Undefined) return null;

        var kpi = data.TryGetProperty("kpi", out var k) ? k : default;
        var info = data.TryGetProperty("info", out var i) ? i : default;
        var powerflow = data.TryGetProperty("powerflow", out var pf) ? pf : default;
        var soc = data.TryGetProperty("soc", out var socEl) ? socEl : default;

        // Extract power flow values (SEMS uses "gridActivePower", "load", etc.)
        decimal gridExportKw = 0, gridImportKw = 0, selfConsumptionKw = 0;
        decimal? batterySoc = null, batteryPowerKw = null;

        if (powerflow.ValueKind == JsonValueKind.Object)
        {
            // gridStatus: 1=consuming(import), -1=selling(export)
            var gridStatus = GetDecimal(powerflow, "gridStatus");
            var gridPower = GetDecimal(powerflow, "grid") / 1000m;  // usually in Watts
            if (gridStatus < 0) gridExportKw = Math.Abs(gridPower);
            else gridImportKw = gridPower;

            selfConsumptionKw = GetDecimal(powerflow, "load") / 1000m;

            var bettStatus = GetDecimal(powerflow, "bettStatus");
            var bettPower = GetDecimal(powerflow, "bett") / 1000m;
            if (bettPower != 0) batteryPowerKw = bettStatus < 0 ? -bettPower : bettPower;
        }

        if (soc.ValueKind == JsonValueKind.Object)
        {
            batterySoc = GetNullableDecimal(soc, "power");  // SEMS puts SOC% in "power"
        }

        return new RealtimeData
        {
            Timestamp = DateTime.UtcNow,
            CurrentPowerKw = GetDecimal(kpi, "pac") / 1000m,  // SEMS returns Watts
            TodayEnergyKwh = GetDecimal(kpi, "power"),         // already in kWh
            TotalEnergyKwh = GetDecimal(kpi, "total_power"),
            GridExportKw = gridExportKw,
            GridImportKw = gridImportKw,
            BatterySocPercent = batterySoc ?? GetNullableDecimal(kpi, "soc"),
            BatteryPowerKw = batteryPowerKw,
            IsOnline = info.ValueKind == JsonValueKind.Object
                && info.TryGetProperty("status", out var s)
                && (ToInt(s) ?? 0) == 1
        };
    }

    public async Task<List<EnergyRecord>> GetHistoricalDataAsync(
        ApiCredentials credentials, DateTime from, DateTime to)
    {
        var records = new List<EnergyRecord>();
        var session = await LoginAsync(credentials);
        if (session == null) return records;

        var json = await PostAsync(session, "v2/PowerStationMonitor/GetPowerStationPowerAndIncomeByDay",
            new
            {
                powerStationId = credentials.PlantId,
                date = from.ToString("yyyy-MM-dd"),
                count = Math.Min(365, (to - from).Days + 1)
            });

        if (json == null) return records;

        if (json.Value.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                try
                {
                    if (!item.TryGetProperty("date", out var dt)) continue;
                    var date = ToDateTime(dt);
                    if (date == null) continue;

                    records.Add(new EnergyRecord
                    {
                        Timestamp = date.Value,
                        EnergyGenerationKwh = GetDecimal(item, "pv"),
                        GridExportKwh = GetDecimal(item, "sell"),
                        GridImportKwh = GetDecimal(item, "buy"),
                        SelfConsumptionKwh = GetDecimal(item, "load"),
                        BatteryChargeKwh = GetNullableDecimal(item, "charge"),
                        BatteryDischargeKwh = GetNullableDecimal(item, "discharge")
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Skipped bad historical item");
                }
            }
        }

        return records;
    }

    public async Task<List<AlarmRecord>> GetAlarmsAsync(
        ApiCredentials credentials, DateTime from, DateTime to)
    {
        var alarms = new List<AlarmRecord>();
        var session = await LoginAsync(credentials);
        if (session == null) return alarms;

        var json = await PostAsync(session, "v2/Warning/PowerstationWarningsQuery",
            new
            {
                powerstationId = credentials.PlantId,
                pageIndex = 1,
                pageSize = 100,
                range = 0,
                adcode = "",
                condition = ""
            });

        if (json == null) return alarms;

        if (json.Value.TryGetProperty("data", out var data)
            && data.TryGetProperty("list", out var list))
        {
            foreach (var item in list.EnumerateArray())
            {
                try
                {
                    int severity = 3;
                    if (item.TryGetProperty("level", out var lv))
                        severity = ToInt(lv) ?? 3;

                    DateTime occurredAt = DateTime.UtcNow;
                    if (item.TryGetProperty("happentime", out var ht))
                        occurredAt = ToDateTime(ht) ?? DateTime.UtcNow;

                    alarms.Add(new AlarmRecord
                    {
                        AlarmId = GetString(item, "warningid") ?? Guid.NewGuid().ToString(),
                        AlarmName = GetString(item, "warning") ?? "",
                        Severity = severity,
                        OccurredAt = occurredAt,
                        DeviceName = GetString(item, "device_sn"),
                        Description = GetString(item, "warning_desc")
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Skipped bad alarm item");
                }
            }
        }

        return alarms;
    }

    public async Task<List<DeviceInfo>> GetDevicesAsync(ApiCredentials credentials)
    {
        var devices = new List<DeviceInfo>();
        var session = await LoginAsync(credentials);
        if (session == null) return devices;

        var json = await PostAsync(session, "v2/PowerStation/GetMonitorDetailByPowerstationId",
            new { powerStationId = credentials.PlantId });

        if (json == null) return devices;

        if (json.Value.TryGetProperty("data", out var data)
            && data.TryGetProperty("inverter", out var inverters))
        {
            foreach (var item in inverters.EnumerateArray())
            {
                devices.Add(new DeviceInfo
                {
                    DeviceId = GetString(item, "sn") ?? "",
                    DeviceName = GetString(item, "name") ?? GetString(item, "sn") ?? "",
                    DeviceType = "Inverter",
                    SerialNumber = GetString(item, "sn"),
                    Model = GetString(item, "model") ?? GetString(item, "model_type"),
                    IsOnline = item.TryGetProperty("status", out var st)
                        && (ToInt(st) ?? 0) == 1
                });
            }
        }

        return devices;
    }

    // ══════════════════════════════════════════════════════════════
    //  AUTHENTICATION - SEMS CrossLogin flow
    // ══════════════════════════════════════════════════════════════

    private sealed class Session
    {
        public string ApiBase { get; set; } = "";    // e.g. "https://eu.semsportal.com/api/"
        public string TokenJson { get; set; } = "";  // JSON to put in Token header
    }

    private async Task<Session?> LoginAsync(ApiCredentials credentials)
    {
        var (session, _) = await LoginWithDetailAsync(credentials);
        return session;
    }

    private async Task<(Session? Session, string Error)> LoginWithDetailAsync(ApiCredentials credentials)
    {
        if (string.IsNullOrEmpty(credentials.Username) || string.IsNullOrEmpty(credentials.Password))
            return (null, "Username (SEMS email) and Password are required");

        // Step 1: Discover regional server
        // Global entry point returns the actual regional API URL (e.g. eu/us/au)
        var (discResult, discError) = await TryLoginAtUrlAsync(DefaultLoginUrl, credentials);
        if (discResult == null) return (null, discError);

        if (discResult.Value.HasToken)
        {
            // Login succeeded directly (single-step)
            return (BuildSession(discResult.Value), "OK");
        }

        // Step 2: re-login at regional server
        // Response contained an 'api' URL but no token - retry at that URL
        var regionalUrl = discResult.Value.ApiUrl;
        if (string.IsNullOrEmpty(regionalUrl))
            return (null, $"Login discovery returned no token and no api URL. Body: {Truncate(discResult.Value.RawBody, 400)}");

        var regionalLoginUrl = CombineUrl(regionalUrl, "v2/Common/CrossLogin");
        _logger.LogInformation("GoodWe: retrying login at regional server {Url}", regionalLoginUrl);

        var (regResult, regError) = await TryLoginAtUrlAsync(regionalLoginUrl, credentials);
        if (regResult == null) return (null, $"Regional login failed: {regError}");

        if (!regResult.Value.HasToken)
        {
            // Try v1 path if v2 doesn't work
            var v1Url = CombineUrl(regionalUrl, "v1/Common/CrossLogin");
            _logger.LogInformation("GoodWe: retrying login at v1 {Url}", v1Url);
            var (v1Result, v1Error) = await TryLoginAtUrlAsync(v1Url, credentials);
            if (v1Result == null) return (null, $"V1 login failed: {v1Error}");
            if (!v1Result.Value.HasToken)
                return (null, $"Regional login succeeded but no token. Response: {Truncate(v1Result.Value.RawBody, 400)}");
            return (BuildSession(v1Result.Value), "OK");
        }

        return (BuildSession(regResult.Value), "OK");
    }

    private struct LoginResult
    {
        public string Uid;
        public string Token;
        public string ApiUrl;
        public long Timestamp;
        public string RawBody;
        public bool HasToken => !string.IsNullOrEmpty(Token);
    }

    private static Session BuildSession(LoginResult r)
    {
        var tokenJson = JsonSerializer.Serialize(new
        {
            uid = r.Uid,
            timestamp = r.Timestamp,
            token = r.Token,
            client = "web",
            version = "v2.1.0",
            language = "en"
        });

        var apiBase = r.ApiUrl;
        if (string.IsNullOrEmpty(apiBase)) apiBase = "https://www.semsportal.com/api/";
        if (!apiBase.EndsWith("/")) apiBase += "/";

        return new Session { ApiBase = apiBase, TokenJson = tokenJson };
    }

    private static string CombineUrl(string baseUrl, string path)
    {
        if (!baseUrl.EndsWith("/")) baseUrl += "/";
        path = path.TrimStart('/');
        return baseUrl + path;
    }

    private async Task<(LoginResult? Result, string Error)> TryLoginAtUrlAsync(string url, ApiCredentials credentials)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();

            var initialToken = JsonSerializer.Serialize(new
            {
                uid = "",
                timestamp = 0,
                token = "",
                client = "web",
                version = "v2.1.0",
                language = "en"
            });

            var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new { account = credentials.Username, pwd = credentials.Password })
            };
            req.Headers.TryAddWithoutValidation("Token", initialToken);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 SolarApp/1.0");

            _logger.LogInformation("GoodWe: POST {Url} for account={Account}", url, credentials.Username);

            var response = await client.SendAsync(req);
            var body = await response.Content.ReadAsStringAsync();

            _logger.LogInformation("GoodWe {Url} => HTTP {Code}, body length={Len}",
                url, (int)response.StatusCode, body.Length);

            if (!response.IsSuccessStatusCode)
                return (null, $"HTTP {(int)response.StatusCode}: {Truncate(body, 500)}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("hasError", out var he) && he.ValueKind == JsonValueKind.True)
            {
                var msg = GetString(root, "msg") ?? "unknown";
                var code = root.TryGetProperty("code", out var c) ? c.ToString() : "?";
                return (null, $"SEMS error code={code}: {msg}");
            }

            var result = new LoginResult { RawBody = body };

            // Try to extract api URL from multiple possible locations
            // (sometimes at root, sometimes inside data, sometimes inside components)
            result.ApiUrl = GetString(root, "api") ?? "";

            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                result.Uid = GetString(data, "uid") ?? "";
                result.Token = GetString(data, "token") ?? "";
                if (string.IsNullOrEmpty(result.ApiUrl))
                    result.ApiUrl = GetString(data, "api") ?? "";

                if (data.TryGetProperty("timestamp", out var ts))
                {
                    if (ts.ValueKind == JsonValueKind.Number) ts.TryGetInt64(out result.Timestamp);
                    else if (ts.ValueKind == JsonValueKind.String) long.TryParse(ts.GetString(), out result.Timestamp);
                }
            }

            return (result, "OK");
        }
        catch (HttpRequestException ex)
        {
            return (null, $"Network error: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return (null, "Request timed out");
        }
        catch (JsonException ex)
        {
            return (null, $"Invalid JSON response: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (null, $"Exception: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<JsonElement?> PostAsync(Session session, string relativePath, object body)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var url = session.ApiBase + relativePath;

            var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(body)
            };
            req.Headers.TryAddWithoutValidation("Token", session.TokenJson);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 SolarApp/1.0");

            var response = await client.SendAsync(req);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GoodWe {Path} HTTP {Code}: {Body}",
                    relativePath, (int)response.StatusCode, Truncate(content, 300));
                return null;
            }

            var doc = JsonDocument.Parse(content);
            return doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GoodWe {Path} error", relativePath);
            return null;
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  HELPERS
    // ══════════════════════════════════════════════════════════════

    private static string? GetString(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            _ => null
        };
    }

    private static decimal GetDecimal(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return 0;
        if (!el.TryGetProperty(prop, out var v)) return 0;
        return ToDecimal(v) ?? 0;
    }

    private static decimal? GetNullableDecimal(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(prop, out var v)) return null;
        return ToDecimal(v);
    }

    // Safe JSON conversions - check ValueKind BEFORE calling TryGet* methods
    // (System.Text.Json throws InvalidOperationException if kind is wrong)

    private static decimal? ToDecimal(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            if (decimal.TryParse(s, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d2)) return d2;
        }
        return null;
    }

    private static int? ToInt(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.True) return 1;
        if (v.ValueKind == JsonValueKind.False) return 0;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var n2)) return n2;
        return null;
    }

    private static long? ToLong(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var n2)) return n2;
        return null;
    }

    private static DateTime? ToDateTime(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            if (DateTime.TryParse(s, out var dt)) return dt;
        }
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var ms))
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        return null;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";

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
                      : ApiResultClassifier.ClassifyErrorMessage<bool>(msg, "GoodWe", sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<bool>(ex, "GoodWe", sw.Elapsed); }
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
                    $"Station '{credentials.PlantId}' not found on GoodWe account.", duration: sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<PlantInfo>(ex, "GoodWe", sw.Elapsed); }
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
                    "GoodWe API returned no data. Could be server issue or Station ID mismatch.", duration: sw.Elapsed);
            return data.IsOnline
                ? ApiResult<RealtimeData>.Ok(data, "Device online", sw.Elapsed)
                : ApiResult<RealtimeData>.Offline(data,
                    "GoodWe API connected OK, but station reports offline. Check inverter WiFi.",
                    sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<RealtimeData>(ex, "GoodWe", sw.Elapsed); }
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
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<List<EnergyRecord>>(ex, "GoodWe", sw.Elapsed); }
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
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<List<AlarmRecord>>(ex, "GoodWe", sw.Elapsed); }
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
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<List<DeviceInfo>>(ex, "GoodWe", sw.Elapsed); }
    }
}
