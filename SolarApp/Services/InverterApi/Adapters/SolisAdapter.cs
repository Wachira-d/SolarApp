using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SolarApp.Models.Enums;
using SolarApp.Services.InverterApi.Common;
using SolarApp.Services.InverterApi.Models;

namespace SolarApp.Services.InverterApi.Adapters;

/// <summary>
/// Solis Cloud API Adapter.
///
/// Mode 1 — Official HMAC API (preferred, supported by SolisCloud):
///   ApiKey    = API Key ID
///   ApiSecret = API Key Secret
///   Get from  : SolisCloud → User Profile → API Management
///
/// Mode 2 — Reverse Web Login (no API key needed, may break if Solis
/// changes the web portal):
///   Username  = SolisCloud email
///   Password  = SolisCloud password
///   ApiKey    = blank  (or the legacy "SOLISWEB" sentinel — still accepted)
///   ApiSecret = blank
///
/// Mode is auto-detected: any credential set with Username+Password but
/// no ApiKey/ApiSecret is treated as web-login.  The legacy "SOLISWEB"
/// sentinel is still recognised so existing installations don't need a
/// data migration.
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

    /// <summary>
    /// Web-login mode resolution.  Order of precedence:
    ///   1. Explicit ConnectionMethod from the operator (set in the UI
    ///      dropdown) — always wins.  Without this an installation that
    ///      previously saved an ApiKey but now wants web-login would
    ///      forever auto-detect as API mode and fail with "API Secret
    ///      required" no matter how many times the operator re-enters
    ///      Username/Password.
    ///   2. Legacy explicit sentinel "SOLISWEB" in ApiKey field.
    ///   3. Auto-detect: web-login when ApiKey/Secret blank and
    ///      Username/Password populated.
    /// </summary>
    private static bool IsWebMode(ApiCredentials c)
    {
        if (c.ConnectionMethod == ConnectionMethod.UserPassword) return true;
        if (c.ConnectionMethod == ConnectionMethod.OfficialApi)  return false;
        if (string.Equals(c.ApiKey, "SOLISWEB", StringComparison.OrdinalIgnoreCase))
            return true;
        var hasApi = !string.IsNullOrWhiteSpace(c.ApiKey) && !string.IsNullOrWhiteSpace(c.ApiSecret);
        var hasUserPass = !string.IsNullOrWhiteSpace(c.Username) && !string.IsNullOrWhiteSpace(c.Password);
        return !hasApi && hasUserPass;
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(ApiCredentials credentials)
    {
        var hasApiKey = !string.IsNullOrWhiteSpace(credentials.ApiKey)
            && !string.Equals(credentials.ApiKey, "SOLISWEB", StringComparison.OrdinalIgnoreCase);
        var hasApiSecret = !string.IsNullOrWhiteSpace(credentials.ApiSecret);
        var hasUser = !string.IsNullOrWhiteSpace(credentials.Username);
        var hasPass = !string.IsNullOrWhiteSpace(credentials.Password);

        if (!hasApiKey && !hasApiSecret && !hasUser && !hasPass)
            return (false,
                "Solis: enter either (a) API Key + API Secret from SolisCloud → User Profile → API Management, " +
                "or (b) your SolisCloud Username (email) + Password.");

        if (IsWebMode(credentials))
        {
            if (!hasUser)
                return (false, "Solis web-login mode: Username (SolisCloud email) is required.");
            if (!hasPass)
                return (false, "Solis web-login mode: Password (SolisCloud password) is required.");
        }
        else
        {
            if (!hasApiKey)
                return (false, "Solis API mode: API Key ID is required (or leave blank and provide Username/Password).");
            if (!hasApiSecret)
                return (false, "Solis API mode: API Secret is required.");
        }

        try
        {
            if (IsWebMode(credentials))
            {
                var token = await WebLoginAsync(credentials);
                if (string.IsNullOrEmpty(token))
                {
                    var diagnostic = _lastWebLoginError ?? "No diagnostic info";
                    return (false,
                        "Solis web-login failed.  SolisCloud has progressively locked down their " +
                        "web portal endpoints, so password login can break without warning when " +
                        "they ship a portal update.\n\n" +
                        "▶ RECOMMENDED: switch to Official API mode\n" +
                        "  1. Login to https://www.soliscloud.com\n" +
                        "  2. Go to: My Account → API Management → Apply for API Key\n" +
                        "  3. Wait for approval (usually same-day)\n" +
                        "  4. Get the Key ID and Key Secret\n" +
                        "  5. In SolarApp, edit this installation:\n" +
                        "     • API Key    = your Key ID\n" +
                        "     • API Secret = your Key Secret\n" +
                        "     • Username/Password may be left blank\n\n" +
                        $"Diagnostic from login attempts:\n{Truncate(diagnostic, 1200)}");
                }

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
    private DateTime _webTokenExpiry = DateTime.MinValue;
    private string? _webTokenUser;
    private string? _lastWebLoginError;
    private static readonly TimeSpan WebTokenTtl = TimeSpan.FromMinutes(25);

    internal string? LastWebLoginError => _lastWebLoginError;

    private async Task<string?> WebLoginAsync(ApiCredentials credentials, bool forceRefresh = false)
    {
        // Use cached token only if: not expired, same user, not forced refresh
        if (!forceRefresh
            && !string.IsNullOrEmpty(_webToken)
            && _webTokenUser == credentials.Username
            && DateTime.UtcNow < _webTokenExpiry)
            return _webToken;

        _lastWebLoginError = null;
        var attemptLog = new List<string>();

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
            client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
            client.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Origin", WebBaseUrl);
            client.DefaultRequestHeaders.Add("Referer", $"{WebBaseUrl}/");
            client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");

            var passwordMd5 = ComputeMd5(credentials.Password ?? "");

            // Ordered from most-likely-to-work to fallback.
            // /u/login = actual web portal login (no signature required)
            // /api/... endpoints require HMAC-SHA1 signature (official API scheme,
            // not usable for password login). We try them only as a last resort
            // and expect 403 "Date can not be empty" — that's the signal to
            // tell the user: switch to official API mode.
            var endpoints = new[]
            {
                $"{WebBaseUrl}/u/login",        // Real web-portal login
            };

            var passwordVariants = new[]
            {
                ("MD5", passwordMd5),
                ("plain", credentials.Password ?? ""),
            };

            var fieldVariants = new (string UserField, string PwField)[]
            {
                ("userInfo", "passWord"),       // soliscloud.com web form
                ("username", "password"),       // some regional variants
            };

            foreach (var endpoint in endpoints)
            {
                foreach (var (pwLabel, pw) in passwordVariants)
                {
                    foreach (var (userField, pwField) in fieldVariants)
                    {
                        var tag = $"{endpoint.Replace(WebBaseUrl, "")} [{userField}/{pwField}/{pwLabel}]";
                        try
                        {
                            var payload = new Dictionary<string, string>
                            {
                                [userField] = credentials.Username ?? "",
                                [pwField] = pw
                            };

                            var response = await client.PostAsJsonAsync(endpoint, payload);
                            var body = await response.Content.ReadAsStringAsync();
                            var code = (int)response.StatusCode;

                            if (!response.IsSuccessStatusCode)
                            {
                                attemptLog.Add($"{tag} → HTTP {code}: {Truncate(body, 120)}");
                                continue;
                            }

                            JsonElement json;
                            try { json = JsonDocument.Parse(body).RootElement; }
                            catch
                            {
                                attemptLog.Add($"{tag} → HTTP {code} non-JSON: {Truncate(body, 120)}");
                                continue;
                            }

                            // Check explicit error: code != "0" or success=false
                            var respCode = SafeGetString(json, "code");
                            var respMsg = SafeGetString(json, "msg") ?? SafeGetString(json, "message");
                            if (respCode != null && respCode != "0" && respCode != "200")
                            {
                                attemptLog.Add($"{tag} → {code} code={respCode} msg={respMsg}");
                                // If "invalid user" type error, no need to keep trying password variants
                                if (respMsg != null && (respMsg.Contains("密码", StringComparison.OrdinalIgnoreCase)
                                    || respMsg.Contains("password", StringComparison.OrdinalIgnoreCase)
                                    || respMsg.Contains("login", StringComparison.OrdinalIgnoreCase)))
                                {
                                    // Keep trying other payloads
                                }
                                continue;
                            }

                            // Extract token
                            string? token = null;
                            if (json.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                            {
                                token = SafeGetString(data, "token")
                                    ?? SafeGetString(data, "csrfToken")
                                    ?? SafeGetString(data, "sessionId")
                                    ?? SafeGetString(data, "Access-Token")
                                    ?? SafeGetString(data, "access_token");
                            }
                            token ??= SafeGetString(json, "token") ?? SafeGetString(json, "csrfToken");

                            // Check response cookies for session token
                            if (string.IsNullOrEmpty(token)
                                && response.Headers.TryGetValues("Set-Cookie", out var cookies))
                            {
                                foreach (var c in cookies)
                                {
                                    if (c.StartsWith("token=", StringComparison.OrdinalIgnoreCase)
                                        || c.StartsWith("SESSION=", StringComparison.OrdinalIgnoreCase))
                                    {
                                        token = c.Split(';')[0].Split('=')[1];
                                        break;
                                    }
                                }
                            }

                            if (!string.IsNullOrEmpty(token))
                            {
                                _webToken = token;
                                _webTokenExpiry = DateTime.UtcNow.Add(WebTokenTtl);
                                _webTokenUser = credentials.Username;
                                _logger.LogInformation("Solis web login OK via {Tag}", tag);
                                return _webToken;
                            }

                            attemptLog.Add($"{tag} → {code} OK but no token field. Body: {Truncate(body, 150)}");
                        }
                        catch (Exception attemptEx)
                        {
                            attemptLog.Add($"{tag} → exception: {attemptEx.Message}");
                        }
                    }
                }
            }

            _lastWebLoginError = "All login attempts failed:\n" + string.Join("\n", attemptLog);
            _logger.LogWarning("Solis web login exhausted all variants:\n{Log}", _lastWebLoginError);
            return null;
        }
        catch (Exception ex)
        {
            _lastWebLoginError = $"Exception: {ex.Message}";
            _logger.LogError(ex, "Solis web login failed");
            return null;
        }
    }

    private static string ComputeMd5(string input)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private async Task<JsonElement?> SendWebRequestAsync(ApiCredentials credentials, string path, string body)
    {
        try
        {
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var forceRefresh = attempt == 2;
                var token = await WebLoginAsync(credentials, forceRefresh);
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
                _logger.LogDebug("Solis web {Path}: HTTP {Code} (attempt {Attempt})", path, (int)response.StatusCode, attempt);

                // On 401/403 — invalidate cache and retry once
                if ((response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                     response.StatusCode == System.Net.HttpStatusCode.Forbidden) && attempt == 1)
                {
                    _logger.LogWarning("Solis web {Path}: auth rejected, invalidating cached token and retrying", path);
                    _webToken = null;
                    _webTokenExpiry = DateTime.MinValue;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Solis web {Path}: HTTP {Code}: {Body}", path, (int)response.StatusCode, Truncate(content, 200));
                    return null;
                }

                return JsonDocument.Parse(content).RootElement;
            }
            return null;
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
                      : ApiResultClassifier.ClassifyErrorMessage<bool>(msg, "Solis", sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<bool>(ex, "Solis", sw.Elapsed); }
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
                    $"Plant '{credentials.PlantId}' not found on Solis account.", duration: sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<PlantInfo>(ex, "Solis", sw.Elapsed); }
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
                    "Solis API returned no data. Could be server issue or PlantId mismatch.", duration: sw.Elapsed);
            return data.IsOnline
                ? ApiResult<RealtimeData>.Ok(data, "Device online", sw.Elapsed)
                : ApiResult<RealtimeData>.Offline(data,
                    "Solis API connected OK, but station reports offline. Check datalogger at site.",
                    sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<RealtimeData>(ex, "Solis", sw.Elapsed); }
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
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<List<EnergyRecord>>(ex, "Solis", sw.Elapsed); }
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
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<List<AlarmRecord>>(ex, "Solis", sw.Elapsed); }
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
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<List<DeviceInfo>>(ex, "Solis", sw.Elapsed); }
    }
}
