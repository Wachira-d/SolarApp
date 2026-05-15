using System.Net.Http.Json;
using System.Text.Json;
using SolarApp.Models.Enums;
using SolarApp.Services.InverterApi.Common;
using SolarApp.Services.InverterApi.Models;

namespace SolarApp.Services.InverterApi.Adapters;

/// <summary>
/// Hoymiles S-Miles Cloud API adapter — V3 (post-Jan 2025).
///
/// Hoymiles migrated from global.hoymiles.com/platform/api/gateway to
/// neapi.hoymiles.com in January 2025. The old URL returns 404.
///
/// V3 Authentication flow:
///   1. POST /iam/pub/0/c/region_c  (detect user's region → get login_url)
///   2. POST {login_url}/iam/pub/0/c/login_c  (username + hashed password → token)
///
/// Password encoding: MD5(password) + "." + Base64(SHA256(password))
///
/// Credentials mapping:
///   Username = S-Miles Cloud email / phone
///   Password = S-Miles Cloud password (plain text — we hash it)
///   PlantId  = Station ID (sid)
/// </summary>
public class HoymilesAdapter : IInverterApiAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HoymilesAdapter> _logger;

    // V3 API base — replaced old global.hoymiles.com/platform/api/gateway
    private const string RegionApiBase = "https://euapi.hoymiles.com";
    private const string DefaultApiBase = "https://neapi.hoymiles.com";

    public InverterBrand Brand => InverterBrand.Hoymiles;

    // Static session cache
    private static readonly Dictionary<string, (string Token, string ApiBase, DateTime ExpiresAt)> _tokenCache = new();
    private static readonly object _tokenLock = new();
    private static readonly TimeSpan TokenTtl = TimeSpan.FromMinutes(30);

    public HoymilesAdapter(IHttpClientFactory httpClientFactory, ILogger<HoymilesAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // ══════════════════════════════════════════════════════════════
    //  Authentication
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// V3 login flow:
    ///   1. Detect user's region via /iam/pub/0/c/region_c → get login_url
    ///   2. Login via {login_url}/iam/pub/0/c/login_c with hashed password → get token
    ///
    /// Password encoding (observed from web client):
    ///   MD5(password).Base64(SHA256(password))
    /// </summary>
    private async Task<(string? Token, string? ApiBase, string Error)> LoginAsync(ApiCredentials credentials)
    {
        var cacheKey = $"hoymiles|{credentials.Username}|{credentials.Password?.Length ?? 0}";
        lock (_tokenLock)
        {
            if (_tokenCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.ExpiresAt)
                return (cached.Token, cached.ApiBase, "OK (cached)");
        }

        try
        {
            var client = CreateClient();

            // ── Step 1: Detect region → get login_url ──
            var regionBody = new { email = credentials.Username };
            var regionResp = await client.PostAsJsonAsync($"{RegionApiBase}/iam/pub/0/c/region_c", regionBody);
            var regionContent = await regionResp.Content.ReadAsStringAsync();
            string loginUrl = RegionApiBase;
            string apiBase = DefaultApiBase;

            if (regionResp.IsSuccessStatusCode)
            {
                try
                {
                    var regionJson = JsonDocument.Parse(regionContent).RootElement;
                    if (regionJson.TryGetProperty("data", out var regionData))
                    {
                        loginUrl = SafeGetString(regionData, "login_url") ?? loginUrl;
                        apiBase = SafeGetString(regionData, "api_url")
                               ?? SafeGetString(regionData, "data_url")
                               ?? apiBase;
                    }
                }
                catch { /* use defaults */ }
            }
            _logger.LogDebug("Hoymiles region: loginUrl={Url}, apiBase={Base}", loginUrl, apiBase);

            // ── Step 2: Hash password (MD5 + "." + Base64(SHA256)) ──
            var password = credentials.Password ?? "";
            string hashedPassword;
            using (var md5 = System.Security.Cryptography.MD5.Create())
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var pwBytes = System.Text.Encoding.UTF8.GetBytes(password);
                var md5Hash = BitConverter.ToString(md5.ComputeHash(pwBytes)).Replace("-", "").ToLowerInvariant();
                var shaHash = Convert.ToBase64String(sha.ComputeHash(pwBytes));
                hashedPassword = $"{md5Hash}.{shaHash}";
            }

            // ── Step 3: Login ──
            var loginBody = new { user_name = credentials.Username, password = hashedPassword };
            var loginResp = await client.PostAsJsonAsync($"{loginUrl}/iam/pub/0/c/login_c", loginBody);
            var loginContent = await loginResp.Content.ReadAsStringAsync();

            if (!loginResp.IsSuccessStatusCode)
            {
                // Fallback: try legacy V0 login (some older accounts)
                return await LegacyLoginAsync(credentials);
            }

            var loginJson = JsonDocument.Parse(loginContent).RootElement;
            var status = SafeGetString(loginJson, "status") ?? SafeGetInt(loginJson, "status")?.ToString();
            var msg = SafeGetString(loginJson, "message") ?? SafeGetString(loginJson, "msg");

            if (status != "0")
            {
                // Maybe password hashing not needed for this account — try plain
                var plainBody = new { user_name = credentials.Username, password };
                var plainResp = await client.PostAsJsonAsync($"{loginUrl}/iam/pub/0/c/login_c", plainBody);
                if (plainResp.IsSuccessStatusCode)
                {
                    var plainJson = JsonDocument.Parse(await plainResp.Content.ReadAsStringAsync()).RootElement;
                    if (SafeGetString(plainJson, "status") == "0" || SafeGetInt(plainJson, "status") == 0)
                    {
                        loginJson = plainJson;
                        status = "0";
                    }
                }

                if (status != "0")
                    return await LegacyLoginAsync(credentials);
            }

            string? token = null;
            if (loginJson.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                token = SafeGetString(data, "token")
                     ?? SafeGetString(data, "access_token");
            }

            if (string.IsNullOrEmpty(token))
                return (null, null, $"Login OK but no token found. Response: {Truncate(loginContent, 300)}");

            lock (_tokenLock)
            {
                _tokenCache[cacheKey] = (token, apiBase, DateTime.UtcNow.Add(TokenTtl));
            }
            return (token, apiBase, "OK");
        }
        catch (TaskCanceledException)
        {
            return (null, null, "Login timeout — S-Miles Cloud did not respond.");
        }
        catch (HttpRequestException ex)
        {
            return (null, null, $"Network error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (null, null, $"Login error: {ex.Message}");
        }
    }

    /// <summary>
    /// Legacy V0 login for older accounts or regions that haven't migrated to V3.
    /// Uses the old body/header JSON wrapper pattern on global.hoymiles.com.
    /// </summary>
    private async Task<(string? Token, string? ApiBase, string Error)> LegacyLoginAsync(ApiCredentials credentials)
    {
        var cacheKey = $"hoymiles|{credentials.Username}|{credentials.Password?.Length ?? 0}";
        try
        {
            var client = CreateClient();

            // Try multiple known base URLs
            var bases = new[]
            {
                "https://neapi.hoymiles.com",
                "https://global.hoymiles.com/platform/api/gateway",
                "https://intl.hoymiles.com/platform/api/gateway"
            };

            foreach (var baseUrl in bases)
            {
                try
                {
                    var body = new
                    {
                        body = new { user_name = credentials.Username, password = credentials.Password },
                        header = new { language = "en" }
                    };

                    var response = await client.PostAsJsonAsync(
                        baseUrl.Contains("neapi") ? $"{baseUrl}/pvm-iam/api/0/iam/auth_login" : $"{baseUrl}/iam/auth_login",
                        body);

                    if (!response.IsSuccessStatusCode) continue;

                    var content = await response.Content.ReadAsStringAsync();
                    var json = JsonDocument.Parse(content).RootElement;
                    var st = SafeGetString(json, "status") ?? SafeGetInt(json, "status")?.ToString();
                    if (st != "0" && st != "200" && st != "success") continue;

                    string? token = null;
                    if (json.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                    {
                        token = SafeGetString(data, "token")
                             ?? SafeGetString(data, "access_token")
                             ?? SafeGetString(data, "sid");
                    }
                    if (string.IsNullOrEmpty(token)) continue;

                    var apiBase = baseUrl.Contains("neapi") ? baseUrl : DefaultApiBase;
                    lock (_tokenLock) { _tokenCache[cacheKey] = (token, apiBase, DateTime.UtcNow.Add(TokenTtl)); }
                    _logger.LogInformation("Hoymiles legacy login OK via {Base}", baseUrl);
                    return (token, apiBase, $"OK (legacy via {baseUrl})");
                }
                catch { continue; }
            }
            return (null, null, "All login endpoints failed. Check S-Miles Cloud email/password.");
        }
        catch (Exception ex)
        {
            return (null, null, $"Legacy login error: {ex.Message}");
        }
    }

    private async Task<JsonElement?> CallApiAsync(string token, string apiBase, string path, object requestBody)
    {
        try
        {
            var client = CreateClient();

            // V3 uses Authorization header + flat JSON body
            // Legacy uses body/header wrapper
            var url = $"{apiBase}/{path.TrimStart('/')}";
            client.DefaultRequestHeaders.Remove("Authorization");
            client.DefaultRequestHeaders.Add("Authorization", token);

            var response = await client.PostAsJsonAsync(url, requestBody);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                // Fallback: try legacy body/header wrapper format
                client.DefaultRequestHeaders.Remove("Authorization");
                var legacyBody = new
                {
                    body = requestBody,
                    header = new { language = "en", token }
                };
                response = await client.PostAsJsonAsync(url, legacyBody);
                content = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Hoymiles {Path}: HTTP {Code}", path, (int)response.StatusCode);
                    return null;
                }
            }

            var json = JsonDocument.Parse(content).RootElement;
            var status = SafeGetString(json, "status") ?? SafeGetInt(json, "status")?.ToString();

            if (status == "100" || status == "401" || status == "token_expired")
            {
                lock (_tokenLock) { _tokenCache.Clear(); }
                _logger.LogWarning("Hoymiles {Path}: token expired, cache cleared", path);
                return null;
            }

            if (status != "0" && status != "200" && status != "success")
            {
                _logger.LogWarning("Hoymiles {Path}: status={Status} msg={Msg}",
                    path, status, SafeGetString(json, "message"));
                return null;
            }

            return json.TryGetProperty("data", out var data) ? data : json;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hoymiles {Path} failed", path);
            return null;
        }
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient("default");
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 SolarApp/2.0");
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    // ══════════════════════════════════════════════════════════════
    //  IInverterApiAdapter — Legacy methods
    // ══════════════════════════════════════════════════════════════

    public async Task<(bool Success, string Message)> TestConnectionAsync(ApiCredentials credentials)
    {
        if (string.IsNullOrEmpty(credentials.Username))
            return (false, "Username (S-Miles Cloud email/phone) is required.");
        if (string.IsNullOrEmpty(credentials.Password))
            return (false, "Password (S-Miles Cloud password) is required.");

        var (token, apiBase, loginError) = await LoginAsync(credentials);
        if (string.IsNullOrEmpty(token))
            return (false, $"Hoymiles login failed: {loginError}");

        // Verify by fetching station list
        var data = await CallApiAsync(token, apiBase ?? DefaultApiBase, "pvmc/api/0/station/select_by_page_c",
            new { page = 1, page_size = 10 });
        if (data == null)
            return (false, "Hoymiles: logged in but station list call failed.");

        return (true, "Connected to Hoymiles S-Miles Cloud!");
    }

    public async Task<PlantInfo?> GetPlantInfoAsync(ApiCredentials credentials)
    {
        var (token, apiBase, _) = await LoginAsync(credentials);
        if (token == null) return null;

        var data = await CallApiAsync(token, apiBase ?? DefaultApiBase, "pvmc/api/0/station/select_by_page_c",
            new { page = 1, page_size = 50 });
        if (data == null || data.Value.ValueKind != JsonValueKind.Object) return null;

        // data.list is array of stations
        if (!data.Value.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            // Alternative: data might be direct array
            if (data.Value.ValueKind == JsonValueKind.Array)
                list = data.Value;
            else
                return null;
        }

        // Find the matching station by PlantId, or take first
        foreach (var station in list.EnumerateArray())
        {
            if (station.ValueKind != JsonValueKind.Object) continue;
            var sid = SafeGetString(station, "id")
                   ?? SafeGetString(station, "station_id")
                   ?? SafeGetString(station, "sid");

            if (!string.IsNullOrEmpty(credentials.PlantId)
                && !string.Equals(sid, credentials.PlantId, StringComparison.OrdinalIgnoreCase))
                continue;

            return new PlantInfo
            {
                PlantId = sid ?? "",
                PlantName = SafeGetString(station, "station_name")
                         ?? SafeGetString(station, "name") ?? "",
                CapacityKw = SafeGetDecimal(station, "installed_capacity")
                           + SafeGetDecimal(station, "capacity"),
                Address = SafeGetString(station, "address")
                       ?? SafeGetString(station, "location"),
                Latitude = SafeGetDouble(station, "latitude"),
                Longitude = SafeGetDouble(station, "longitude")
            };
        }
        return null;
    }

    public async Task<RealtimeData?> GetRealtimeDataAsync(ApiCredentials credentials)
    {
        if (string.IsNullOrEmpty(credentials.PlantId)) return null;
        var (token, apiBase, _) = await LoginAsync(credentials);
        if (token == null) return null;

        // Try station-level realtime data
        var data = await CallApiAsync(token, apiBase ?? DefaultApiBase, "pvmc/api/0/station/find_c",
            new { sid = long.TryParse(credentials.PlantId, out var sidLong) ? (object)sidLong : credentials.PlantId });
        if (data == null)
        {
            // Alternative endpoint
            data = await CallApiAsync(token, apiBase ?? DefaultApiBase, "pvmc/api/0/station_data/real_g_c",
                new { sid = credentials.PlantId });
        }
        if (data == null || data.Value.ValueKind != JsonValueKind.Object) return null;

        var d = data.Value;
        var currentPower = SafeGetDecimal(d, "real_power")
                        + SafeGetDecimal(d, "power")
                        + SafeGetDecimal(d, "last_data_power");
        var todayEnergy = SafeGetDecimal(d, "today_eq")
                       + SafeGetDecimal(d, "day_eq")
                       + SafeGetDecimal(d, "today_energy");
        var totalEnergy = SafeGetDecimal(d, "total_eq")
                       + SafeGetDecimal(d, "month_eq_total")
                       + SafeGetDecimal(d, "total_energy");
        var capacityW = SafeGetDecimal(d, "installed_capacity");

        // Online detection: power > 0 or explicit status field
        var statusVal = SafeGetInt(d, "status") ?? SafeGetInt(d, "connect_status") ?? -1;
        var isOnline = statusVal == 1 || currentPower > 0;

        // Power might be in watts from some endpoints
        if (currentPower > 500) currentPower /= 1000m; // likely watts
        if (todayEnergy > 10000) todayEnergy /= 1000m; // likely Wh
        if (totalEnergy > 10000000) totalEnergy /= 1000m; // likely Wh

        return new RealtimeData
        {
            Timestamp = DateTime.UtcNow,
            CurrentPowerKw = currentPower,
            TodayEnergyKwh = todayEnergy,
            TotalEnergyKwh = totalEnergy,
            IsOnline = isOnline
        };
    }

    public async Task<List<EnergyRecord>> GetHistoricalDataAsync(
        ApiCredentials credentials, DateTime from, DateTime to)
    {
        var records = new List<EnergyRecord>();
        if (string.IsNullOrEmpty(credentials.PlantId)) return records;
        var (token, apiBase, _) = await LoginAsync(credentials);
        if (token == null) return records;

        try
        {
            var data = await CallApiAsync(token, apiBase ?? DefaultApiBase, "pvmc/api/0/station/data/count_playback_power_by_day",
                new
                {
                    sid = credentials.PlantId,
                    start_date = from.ToString("yyyy-MM-dd"),
                    end_date = to.ToString("yyyy-MM-dd"),
                    type = 1 // daily
                });

            if (data == null) return records;

            // Try to find array in response
            JsonElement arr;
            if (data.Value.ValueKind == JsonValueKind.Array)
                arr = data.Value;
            else if (data.Value.TryGetProperty("list", out var l) && l.ValueKind == JsonValueKind.Array)
                arr = l;
            else if (data.Value.TryGetProperty("data", out var d2) && d2.ValueKind == JsonValueKind.Array)
                arr = d2;
            else
                return records;

            foreach (var item in arr.EnumerateArray())
            {
                try
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var dateStr = SafeGetString(item, "date")
                               ?? SafeGetString(item, "time")
                               ?? SafeGetString(item, "day");
                    if (dateStr == null || !DateTime.TryParse(dateStr, out var date)) continue;

                    var energy = SafeGetDecimal(item, "eq")
                              + SafeGetDecimal(item, "energy")
                              + SafeGetDecimal(item, "day_eq");
                    if (energy > 10000) energy /= 1000m; // Wh → kWh

                    records.Add(new EnergyRecord
                    {
                        Timestamp = date,
                        EnergyGenerationKwh = energy,
                        GridExportKwh = SafeGetDecimal(item, "grid_sell_eq"),
                        GridImportKwh = SafeGetDecimal(item, "grid_buy_eq"),
                        SelfConsumptionKwh = SafeGetDecimal(item, "self_use_eq"),
                    });
                }
                catch (Exception itemEx)
                {
                    _logger.LogWarning(itemEx, "Hoymiles: skipping malformed history record");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hoymiles GetHistoricalData failed");
        }
        return records;
    }

    public async Task<List<AlarmRecord>> GetAlarmsAsync(
        ApiCredentials credentials, DateTime from, DateTime to)
    {
        var alarms = new List<AlarmRecord>();
        if (string.IsNullOrEmpty(credentials.PlantId)) return alarms;
        var (token, apiBase, _) = await LoginAsync(credentials);
        if (token == null) return alarms;

        try
        {
            var data = await CallApiAsync(token, apiBase ?? DefaultApiBase, "pvmc/api/0/alarm/select_by_page",
                new
                {
                    sid = credentials.PlantId,
                    page = 1,
                    page_size = 100,
                    start_date = from.ToString("yyyy-MM-dd"),
                    end_date = to.ToString("yyyy-MM-dd")
                });

            if (data == null) return alarms;

            JsonElement arr;
            if (data.Value.ValueKind == JsonValueKind.Array) arr = data.Value;
            else if (data.Value.TryGetProperty("list", out var l)) arr = l;
            else return alarms;

            foreach (var item in arr.EnumerateArray())
            {
                try
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    alarms.Add(new AlarmRecord
                    {
                        AlarmId = SafeGetString(item, "alarm_id") ?? SafeGetString(item, "id") ?? "",
                        AlarmName = SafeGetString(item, "alarm_name")
                                 ?? SafeGetString(item, "alarm_code")
                                 ?? SafeGetString(item, "name") ?? "",
                        Severity = SafeGetInt(item, "alarm_level") ?? SafeGetInt(item, "level") ?? 3,
                        OccurredAt = DateTime.TryParse(SafeGetString(item, "alarm_time")
                            ?? SafeGetString(item, "start_time"), out var dt) ? dt : DateTime.UtcNow,
                        ClearedAt = DateTime.TryParse(SafeGetString(item, "end_time")
                            ?? SafeGetString(item, "clear_time"), out var ct) ? ct : null,
                        DeviceName = SafeGetString(item, "mi_sn")
                                  ?? SafeGetString(item, "device_sn")
                    });
                }
                catch (Exception itemEx)
                {
                    _logger.LogWarning(itemEx, "Hoymiles: skipping malformed alarm");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hoymiles GetAlarms failed");
        }
        return alarms;
    }

    public async Task<List<DeviceInfo>> GetDevicesAsync(ApiCredentials credentials)
    {
        var devices = new List<DeviceInfo>();
        if (string.IsNullOrEmpty(credentials.PlantId)) return devices;
        var (token, apiBase, _) = await LoginAsync(credentials);
        if (token == null) return devices;

        try
        {
            var data = await CallApiAsync(token, apiBase ?? DefaultApiBase, "pvmc/api/0/device/select_by_page",
                new { sid = credentials.PlantId, page = 1, page_size = 100 });
            if (data == null) return devices;

            JsonElement arr;
            if (data.Value.ValueKind == JsonValueKind.Array) arr = data.Value;
            else if (data.Value.TryGetProperty("list", out var l)) arr = l;
            else return devices;

            foreach (var item in arr.EnumerateArray())
            {
                try
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    devices.Add(new DeviceInfo
                    {
                        DeviceId = SafeGetString(item, "device_id")
                                ?? SafeGetString(item, "mi_sn")
                                ?? SafeGetString(item, "sn") ?? "",
                        DeviceName = SafeGetString(item, "device_name")
                                  ?? SafeGetString(item, "mi_name") ?? "",
                        DeviceType = SafeGetString(item, "device_type") ?? "Microinverter",
                        Model = SafeGetString(item, "model")
                             ?? SafeGetString(item, "mi_model")
                             ?? SafeGetString(item, "product_model"),
                        SerialNumber = SafeGetString(item, "mi_sn")
                                    ?? SafeGetString(item, "sn"),
                        FirmwareVersion = SafeGetString(item, "firmware_version")
                                       ?? SafeGetString(item, "fw_version"),
                        IsOnline = (SafeGetInt(item, "status") ?? 0) == 1,
                        RatedPowerKw = SafeGetNullableDecimal(item, "rated_power")
                    });
                }
                catch (Exception itemEx)
                {
                    _logger.LogWarning(itemEx, "Hoymiles: skipping malformed device");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hoymiles GetDevices failed");
        }
        return devices;
    }

    // ══════════════════════════════════════════════════════════════
    //  ApiResult<T> methods
    // ══════════════════════════════════════════════════════════════

    public async Task<ApiResult<bool>> TestConnectionResultAsync(ApiCredentials credentials, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var (ok, msg) = await TestConnectionAsync(credentials);
            sw.Stop();
            return ok ? ApiResult<bool>.Ok(true, msg, sw.Elapsed)
                      : ApiResultClassifier.ClassifyErrorMessage<bool>(msg, "Hoymiles", sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<bool>(ex, "Hoymiles", sw.Elapsed); }
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
                    $"Station '{credentials.PlantId}' not found on Hoymiles account.", duration: sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<PlantInfo>(ex, "Hoymiles", sw.Elapsed); }
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
                    "Hoymiles API returned no data.", duration: sw.Elapsed);
            return data.IsOnline
                ? ApiResult<RealtimeData>.Ok(data, "Device online", sw.Elapsed)
                : ApiResult<RealtimeData>.Offline(data,
                    "Hoymiles API OK, but microinverter reports offline. Normal at night/cloudy.",
                    sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<RealtimeData>(ex, "Hoymiles", sw.Elapsed); }
    }

    public async Task<ApiResult<List<EnergyRecord>>> GetHistoricalResultAsync(ApiCredentials credentials, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var list = await GetHistoricalDataAsync(credentials, from, to);
            sw.Stop();
            return ApiResult<List<EnergyRecord>>.Ok(list, $"{list.Count} record(s)", sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<List<EnergyRecord>>(ex, "Hoymiles", sw.Elapsed); }
    }

    public async Task<ApiResult<List<AlarmRecord>>> GetAlarmsResultAsync(ApiCredentials credentials, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var list = await GetAlarmsAsync(credentials, from, to);
            sw.Stop();
            return ApiResult<List<AlarmRecord>>.Ok(list, $"{list.Count} alarm(s)", sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<List<AlarmRecord>>(ex, "Hoymiles", sw.Elapsed); }
    }

    public async Task<ApiResult<List<DeviceInfo>>> GetDevicesResultAsync(ApiCredentials credentials, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var list = await GetDevicesAsync(credentials);
            sw.Stop();
            return ApiResult<List<DeviceInfo>>.Ok(list, $"{list.Count} device(s)", sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<List<DeviceInfo>>(ex, "Hoymiles", sw.Elapsed); }
    }

    // ══════════════════════════════════════════════════════════════
    //  Safe JSON helpers
    // ══════════════════════════════════════════════════════════════

    private static string? SafeGetString(JsonElement el, string prop)
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

    private static decimal SafeGetDecimal(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return 0;
        if (!el.TryGetProperty(prop, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String
            && decimal.TryParse(v.GetString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var n2)) return n2;
        return 0;
    }

    private static decimal? SafeGetNullableDecimal(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n)) return n;
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

    private static double? SafeGetDouble(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String
            && double.TryParse(v.GetString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var n2)) return n2;
        return null;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
