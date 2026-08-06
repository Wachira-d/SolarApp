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
/// PlantId accepts EITHER:
///   • a plant/station id  → every inverter in that plant is queried and
///     summed (correct for multi-inverter sites), or
///   • a single device serial number → only that inverter.
/// FoxESS's data endpoints are device-scoped, so the adapter resolves the
/// configured value to a device list via ResolveDeviceSnsAsync and
/// aggregates. Leaving it blank uses every device on the account.
///
/// Docs: https://www.foxesscloud.com/public/i18n/en/OpenApiDocument.html
/// </summary>
public class FoxEssAdapter : IInverterApiAdapter, IPlantDiscovery
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FoxEssAdapter> _logger;
    private const string BaseUrl = "https://www.foxesscloud.com";

    // OAuth (Mode 2) token cache — static + thread-safe, keyed by clientId.
    // Mode 1 (private token + MD5 signature) is stateless so needs none, but
    // the OAuth path previously re-fetched a token on every single call.
    private static readonly Dictionary<string, (string Token, DateTime ExpiresAt)> _oauthCache = new();
    private static readonly object _oauthLock = new();
    private static readonly TimeSpan OAuthTtl = TimeSpan.FromMinutes(25);

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
            // pageSize must be 10+ — FoxESS rejects a smaller page with
            // errno 40257 "Parameters do not meet expectations".  This call
            // used pageSize=1 to stay light, which made Test Connection fail
            // on accounts where every real call (which uses 10/100) worked
            // fine.  Match the working calls.
            var json = await SendPostAsync(credentials, "/op/v0/plant/list",
                new { currentPage = 1, pageSize = 10 });
            var ok = json.HasValue && GetErrno(json.Value) == 0;
            return (ok, ok ? "Connected to FOX ESS!" : DescribeError(json));
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
        try
        {
            // One call covers every inverter in the plant — real/query takes a
            // list of serial numbers — so multi-inverter sites cost no extra
            // requests against the 10/min budget.
            var sns = await ResolveDeviceSnsAsync(credentials);
            if (sns.Count == 0) return null;

            var json = await SendPostAsync(credentials, "/op/v1/device/real/query", new
            {
                sns = sns.ToArray(),
                variables = new[]
                {
                    "pvPower", "generationPower", "feedinPower",
                    "gridConsumptionPower", "SoC", "batChargePower",
                    "batDischargePower", "generationTotal", "todayYield"
                }
            });

            if (!json.HasValue || GetErrno(json.Value) != 0) return null;

            // Sum power/energy across devices; SoC is a percentage so average
            // it rather than adding (two 50% batteries are not 100%).
            var values = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            var socReadings = new List<decimal>();
            long? timeMs = null;
            var deviceCount = 0;

            foreach (var device in json.Value.GetProperty("result").EnumerateArray())
            {
                if (device.ValueKind != JsonValueKind.Object) continue;
                deviceCount++;

                if (device.TryGetProperty("datas", out var datas) && datas.ValueKind == JsonValueKind.Array)
                {
                    foreach (var d in datas.EnumerateArray())
                    {
                        if (d.ValueKind != JsonValueKind.Object) continue;
                        var name = GetString(d, "variable");
                        if (string.IsNullOrEmpty(name)) continue;
                        if (!d.TryGetProperty("value", out var vv) || vv.ValueKind != JsonValueKind.Number)
                            continue;
                        var dec = GetDecimal(d, "value");

                        if (name.Equals("SoC", StringComparison.OrdinalIgnoreCase))
                            socReadings.Add(dec);
                        else
                            values[name] = values.TryGetValue(name, out var prev) ? prev + dec : dec;
                    }
                }

                // Keep the most recent reading time across the devices.
                if (device.TryGetProperty("time", out var t)
                    && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var tms))
                    timeMs = timeMs.HasValue ? Math.Max(timeMs.Value, tms) : tms;
            }

            if (deviceCount == 0) return null;

            decimal Get(string k) => values.TryGetValue(k, out var v) ? v : 0;

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
                BatterySocPercent = socReadings.Count > 0
                    ? Math.Round(socReadings.Average(), 1)
                    : null,
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
        Common.AdapterCallState.Reset();

        // Resolve the plant to its inverters.  report/query is per-device, so
        // a multi-inverter site needs one call per device per month and the
        // results summed — querying only one device silently under-reported
        // the site's production.
        var deviceSns = await ResolveDeviceSnsAsync(credentials);
        if (deviceSns.Count == 0)
        {
            Common.AdapterCallState.Fail(
                "FoxESS: could not determine which inverters belong to this site. Set Plant/Station ID to the " +
                "plant id (all its inverters are then summed) or to a single inverter serial number, then retry.");
            return records;
        }

        int monthsOk = 0, monthsFailed = 0;
        string? lastMonthError = null;
        try
        {
            // history/query has 24h max window, so use report/query for daily data instead
            var years = EnumerateMonths(from, to);

            foreach (var ym in years)
            {
                // Accumulate every device's output for this month before
                // turning it into day records.
                var monthGeneration = new decimal[DateTime.DaysInMonth(ym.Year, ym.Month)];
                var monthExport = new decimal[monthGeneration.Length];
                var monthImport = new decimal[monthGeneration.Length];
                var anyDeviceOk = false;

                foreach (var sn in deviceSns)
                {
                    var json = await SendPostAsync(credentials, "/op/v0/device/report/query", new
                    {
                        sn,
                        year = ym.Year,
                        month = ym.Month,
                        dimension = "month",
                        variables = new[] { "generation", "feedin", "gridConsumption" }
                    });

                    if (!json.HasValue || GetErrno(json.Value) != 0)
                    {
                        lastMonthError = !json.HasValue
                            ? "no/failed response"
                            : DescribeError(json);
                        continue;
                    }
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
                            "generation" => monthGeneration,
                            "feedin" => monthExport,
                            "gridConsumption" => monthImport,
                            _ => null
                        };
                        if (target == null) continue;
                        // ADD, don't assign — this device's output joins the others'.
                        for (int i = 0; i < Math.Min(arr.Length, target.Length); i++)
                            target[i] += arr[i];
                    }
                } // end per-device loop

                if (anyDeviceOk) monthsOk++; else monthsFailed++;

                for (int i = 0; i < monthGeneration.Length; i++)
                {
                    var date = new DateTime(ym.Year, ym.Month, i + 1);
                    if (date < from.Date || date > to.Date) continue;
                    if (monthGeneration[i] == 0 && monthExport[i] == 0 && monthImport[i] == 0) continue;

                    records.Add(new EnergyRecord
                    {
                        Timestamp = date,
                        EnergyGenerationKwh = monthGeneration[i],
                        GridExportKwh = monthExport[i],
                        GridImportKwh = monthImport[i],
                        SelfConsumptionKwh = Math.Max(0, monthGeneration[i] - monthExport[i])
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FOX ESS GetHistoricalData failed");
            Common.AdapterCallState.Fail($"FoxESS historical error: {ex.GetType().Name}: {ex.Message}");
            return records;
        }
        // Every month we asked for failed AND we got nothing → real failure,
        // not "no production".  (If even one month succeeded, an empty result
        // legitimately means no generation in the window.)
        if (records.Count == 0 && monthsOk == 0 && monthsFailed > 0)
            Common.AdapterCallState.Fail($"FoxESS: all {monthsFailed} month-queries failed ({lastMonthError}).");
        return records;
    }

    /// <summary>
    /// Admin diagnostic: call each plausible FoxESS endpoint/parameter
    /// combination and report exactly what the cloud answered (errno + the
    /// vendor's own msg + a slice of the payload).  Written because the
    /// spec alone couldn't settle questions like "which API version does
    /// this account serve?" and "what is the minimum pageSize?" — the
    /// server is the authority, so we ask it rather than guess.
    ///
    /// Every probe is read-only.  Device-scoped probes are skipped when no
    /// Plant/Station ID (device SN) is configured yet.
    /// </summary>
    public async Task<List<object>> ProbeEndpointsAsync(ApiCredentials credentials)
    {
        var results = new List<object>();

        async Task ProbeAsync(string label, string path, object body, bool isGet = false, string? query = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var json = isGet
                    ? await SendGetAsync(credentials, path, query)
                    : await SendPostAsync(credentials, path, body);
                sw.Stop();

                if (!json.HasValue)
                {
                    results.Add(new
                    {
                        label, path, ok = false, errno = (int?)null,
                        msg = "no response (HTTP error / non-JSON / network)",
                        durationMs = (int)sw.ElapsedMilliseconds, sample = (string?)null,
                    });
                    return;
                }

                var errno = GetErrno(json.Value);
                var msg = GetString(json.Value, "msg");
                // Keep a short slice of the payload so the operator can see
                // real values (e.g. a device SN) without dumping everything.
                string? sample = null;
                if (json.Value.TryGetProperty("result", out var res))
                {
                    var raw = res.ToString();
                    sample = raw.Length > 600 ? raw[..600] + "…(truncated)" : raw;
                }

                results.Add(new
                {
                    label, path,
                    ok = errno == 0,
                    errno,
                    msg = string.IsNullOrWhiteSpace(msg) ? (errno == 0 ? "OK" : "(no msg)") : msg,
                    durationMs = (int)sw.ElapsedMilliseconds,
                    sample,
                });
            }
            catch (Exception ex)
            {
                sw.Stop();
                results.Add(new
                {
                    label, path, ok = false, errno = (int?)null,
                    msg = $"{ex.GetType().Name}: {ex.Message}",
                    durationMs = (int)sw.ElapsedMilliseconds, sample = (string?)null,
                });
            }
        }

        // ── 1. Auth + pageSize behaviour ─────────────────────────────
        // Same endpoint, three page sizes.  If 1 fails but 10 succeeds we
        // have proof of a minimum-page-size rule rather than an auth fault.
        await ProbeAsync("plant/list pageSize=1",   "/op/v0/plant/list", new { currentPage = 1, pageSize = 1 });
        await ProbeAsync("plant/list pageSize=10",  "/op/v0/plant/list", new { currentPage = 1, pageSize = 10 });
        await ProbeAsync("plant/list pageSize=100", "/op/v0/plant/list", new { currentPage = 1, pageSize = 100 });

        // ── 2. API version check ─────────────────────────────────────
        await ProbeAsync("v1 plant/list", "/op/v1/plant/list", new { currentPage = 1, pageSize = 10 });

        // ── 3. Device list — this is where the inverter SN comes from ─
        await ProbeAsync("device/list", "/op/v0/device/list", new { currentPage = 1, pageSize = 100 });
        await ProbeAsync("v1 device/list", "/op/v1/device/list", new { currentPage = 1, pageSize = 100 });

        // ── 4. Device-scoped calls (need the SN) ─────────────────────
        if (!string.IsNullOrWhiteSpace(credentials.PlantId))
        {
            var sn = credentials.PlantId!.Trim();
            await ProbeAsync("v1 device/real/query", "/op/v1/device/real/query", new
            {
                sns = new[] { sn },
                variables = new[] { "pvPower", "generationPower", "SoC" },
            });
            await ProbeAsync("v0 device/real/query", "/op/v0/device/real/query", new
            {
                sns = new[] { sn },
                variables = new[] { "pvPower", "generationPower", "SoC" },
            });
            var now = DateTime.UtcNow;
            await ProbeAsync("device/report/query (month)", "/op/v0/device/report/query", new
            {
                sn,
                year = now.Year,
                month = now.Month,
                dimension = "month",
                variables = new[] { "generation" },
            });
        }
        else
        {
            results.Add(new
            {
                label = "device-scoped probes",
                path = "(skipped)",
                ok = false,
                errno = (int?)null,
                msg = "Plant/Station ID is empty — set it to the inverter Serial Number (SN) to probe realtime/history. " +
                      "The SN appears in the device/list sample above.",
                durationMs = 0,
                sample = (string?)null,
            });
        }

        return results;
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

    /// <summary>
    /// Lists the account's plants and inverters so the operator can pick a
    /// Plant/Station ID instead of digging one out of a portal URL.  Plants
    /// are marked Recommended because choosing one aggregates every inverter
    /// beneath it; a device is offered too for pinning a single unit.
    /// </summary>
    public async Task<List<PlantOption>> DiscoverAsync(ApiCredentials credentials, CancellationToken ct = default)
    {
        var options = new List<PlantOption>();

        // Plants first — the recommended choice.
        try
        {
            var plants = await SendPostAsync(credentials, "/op/v0/plant/list",
                new { currentPage = 1, pageSize = 100 });
            if (plants.HasValue && GetErrno(plants.Value) == 0
                && plants.Value.TryGetProperty("result", out var pr)
                && pr.TryGetProperty("data", out var pdata)
                && pdata.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in pdata.EnumerateArray())
                {
                    var id = GetString(p, "stationID");
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    options.Add(new PlantOption(
                        id!,
                        GetString(p, "name") ?? "(unnamed plant)",
                        "plant",
                        "รวมผลผลิตของ inverter ทุกตัวในไซต์นี้",
                        Recommended: true));
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "FoxESS discover: plant list failed"); }

        // Then individual devices, for pinning one inverter.
        try
        {
            var devices = await SendPostAsync(credentials, "/op/v0/device/list",
                new { currentPage = 1, pageSize = 100 });
            if (devices.HasValue && GetErrno(devices.Value) == 0
                && devices.Value.TryGetProperty("result", out var dr)
                && dr.TryGetProperty("data", out var ddata)
                && ddata.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in ddata.EnumerateArray())
                {
                    var sn = GetString(d, "deviceSN");
                    if (string.IsNullOrWhiteSpace(sn)) continue;
                    var model = GetString(d, "productType");
                    var station = GetString(d, "stationName");
                    var bits = new[] { model, station }.Where(x => !string.IsNullOrWhiteSpace(x));
                    options.Add(new PlantOption(
                        sn!,
                        string.IsNullOrWhiteSpace(model) ? sn! : $"{model} — {sn}",
                        "device",
                        bits.Any() ? string.Join(" · ", bits) : null));
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "FoxESS discover: device list failed"); }

        return options;
    }

    // ══════════════════════════════════════════════════════════════
    //  DEVICE RESOLUTION
    // ══════════════════════════════════════════════════════════════

    // Resolved SN lists, cached briefly.  FoxESS allows only 10 req/min, and
    // resolving costs a device/list call, so we must not repeat it per query.
    private static readonly Dictionary<string, (List<string> Sns, DateTime ExpiresAt)> _snCache = new();
    private static readonly object _snLock = new();
    private static readonly TimeSpan SnCacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Works out which inverter serial numbers make up this installation.
    ///
    /// FoxESS's data endpoints are device-scoped (real/query, report/query
    /// take serial numbers), but a site is a PLANT that may contain several
    /// inverters.  Keying an installation to a single SN silently reported
    /// only one inverter's output — a 3-inverter site would under-report by
    /// two thirds.  So Plant/Station ID is interpreted as a plant, and every
    /// inverter inside it is queried and summed.
    ///
    /// Accepted values, in priority order:
    ///   1. A device serial number → that one device (explicit override, and
    ///      keeps existing single-inverter setups working unchanged).
    ///   2. A plant/station id → all devices belonging to that plant.
    ///   3. Empty → all devices on the account (single-plant accounts).
    /// </summary>
    private async Task<List<string>> ResolveDeviceSnsAsync(ApiCredentials credentials)
    {
        var plantId = credentials.PlantId?.Trim() ?? "";
        var cacheKey = $"{credentials.ApiKey}|{plantId}";

        lock (_snLock)
        {
            if (_snCache.TryGetValue(cacheKey, out var hit) && DateTime.UtcNow < hit.ExpiresAt)
                return hit.Sns;
        }

        var resolved = new List<string>();
        try
        {
            var json = await SendPostAsync(credentials, "/op/v0/device/list",
                new { currentPage = 1, pageSize = 100 });

            if (json.HasValue && GetErrno(json.Value) == 0
                && json.Value.TryGetProperty("result", out var result)
                && result.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Array)
            {
                var all = new List<(string Sn, string? StationId, string? StationName)>();
                foreach (var item in data.EnumerateArray())
                {
                    var sn = GetString(item, "deviceSN");
                    if (string.IsNullOrWhiteSpace(sn)) continue;
                    all.Add((sn!, GetString(item, "stationID"), GetString(item, "stationName")));
                }

                // 1. Exact device match — the operator pinned one inverter.
                if (!string.IsNullOrEmpty(plantId)
                    && all.Any(d => string.Equals(d.Sn, plantId, StringComparison.OrdinalIgnoreCase)))
                {
                    resolved.Add(all.First(d =>
                        string.Equals(d.Sn, plantId, StringComparison.OrdinalIgnoreCase)).Sn);
                }
                // 2. Plant match — sum every inverter in that plant.
                else if (!string.IsNullOrEmpty(plantId))
                {
                    resolved.AddRange(all
                        .Where(d => string.Equals(d.StationId, plantId, StringComparison.OrdinalIgnoreCase))
                        .Select(d => d.Sn));
                }
                // 3. Nothing configured — use the whole account.
                else
                {
                    resolved.AddRange(all.Select(d => d.Sn));
                }

                if (resolved.Count > 1)
                    _logger.LogInformation(
                        "FoxESS: plant '{PlantId}' resolved to {Count} devices — output will be summed across {Sns}",
                        plantId, resolved.Count, string.Join(", ", resolved));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FoxESS: device resolution failed for '{PlantId}'", plantId);
        }

        // Fall back to treating the configured value as a serial number so a
        // resolution outage doesn't take the site offline entirely.
        if (resolved.Count == 0 && !string.IsNullOrEmpty(plantId))
            resolved.Add(plantId);

        lock (_snLock)
        {
            _snCache[cacheKey] = (resolved, DateTime.UtcNow.Add(SnCacheTtl));
        }
        return resolved;
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
        if (!string.IsNullOrWhiteSpace(credentials.ApiKey))
        {
            // TRIM is essential, not cosmetic: .NET strips surrounding
            // whitespace when it serialises a header value, so a key pasted
            // with a stray space/newline would be SIGNED with the whitespace
            // but SENT without it — the signature then never matches and the
            // vendor rejects every call with an auth error.
            var apiKey = credentials.ApiKey.Trim();
            var client = _httpClientFactory.CreateClient();
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            var signature = GenerateSignature(path, apiKey, timestamp);

            client.DefaultRequestHeaders.Add("token", apiKey);
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

    /// <summary>
    /// Turn a FoxESS error response into something an operator can act on.
    /// The vendor's own "msg" field is the authoritative text, so it is always
    /// included when present; the extra hints below are practical guidance for
    /// the auth-family codes we see most often, not a copy of the vendor's
    /// error table.  Check the portal's "API documentation" for the full list.
    /// </summary>
    private static string DescribeError(JsonElement? json)
    {
        if (json is null) return "FOX ESS: no response (network/timeout).";
        var errno = GetErrno(json.Value);
        var msg = GetString(json.Value, "msg");

        var hint = errno switch
        {
            // 40257 is a REQUEST-BODY complaint ("Parameters do not meet
            // expectations"), not an auth failure — e.g. a pageSize below the
            // allowed minimum, or a device SN that isn't on this account.
            40257 => " — พารามิเตอร์ของคำขอไม่ถูกต้อง: ถ้าเป็นการดึงข้อมูลไซต์ ตรวจว่า Plant/Station ID " +
                     "เป็น Serial Number (SN) ของ inverter (ไม่ใช่ GUID จาก URL ของเว็บ portal)",
            40256 or 40258 =>
                " — ปัญหา API key / signature: ตรวจว่าใช้ Private Token ที่เพิ่ง generate (ค่าเดิมถูกยกเลิกทันทีที่ generate ใหม่), " +
                "ใส่ครบไม่มีช่องว่าง, และเวลาเครื่องเซิร์ฟเวอร์ตรง (signature ผูกกับ timestamp)",
            40400 => " — ไม่พบ resource: ตรวจว่า Plant/Station ID เป็น Serial Number (SN) ของ inverter",
            41807 or 41808 => " — เกินโควตาเรียก API ของวันนี้",
            _ => "",
        };

        var msgPart = string.IsNullOrWhiteSpace(msg) ? "" : $": {msg}";
        return $"FOX ESS errno={errno}{msgPart}{hint}";
    }

    private async Task<string?> GetOAuthTokenAsync(string clientId, string clientSecret)
    {
        lock (_oauthLock)
        {
            if (_oauthCache.TryGetValue(clientId, out var cached) && DateTime.UtcNow < cached.ExpiresAt)
                return cached.Token;
        }
        try
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.PostAsJsonAsync($"{BaseUrl}/oauth/token",
                new { grant_type = "client_credentials", client_id = clientId, client_secret = clientSecret });

            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            string? token = null;
            if (json.TryGetProperty("access_token", out var tok)) token = tok.GetString();
            else if (json.TryGetProperty("data", out var d) && d.TryGetProperty("access_token", out var tok2))
                token = tok2.GetString();

            if (!string.IsNullOrEmpty(token))
            {
                lock (_oauthLock) { _oauthCache[clientId] = (token, DateTime.UtcNow.Add(OAuthTtl)); }
                return token;
            }
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
            var fail = Common.AdapterCallState.Take();
            if (list.Count == 0 && fail != null)
                return ApiResult<List<EnergyRecord>>.Fail(
                    ApiResultKind.CloudServerError, "FoxESS historical fetch failed.", detail: fail, duration: sw.Elapsed);
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
