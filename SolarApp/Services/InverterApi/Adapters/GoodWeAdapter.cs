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
public class GoodWeAdapter : IInverterApiAdapter, IPlantDiscovery
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GoodWeAdapter> _logger;

    // Multiple regional login endpoints - SEMS redirects to the correct one
    private const string DefaultLoginUrl = "https://www.semsportal.com/api/v2/Common/CrossLogin";

    public InverterBrand Brand => InverterBrand.GoodWe;

    // AsyncLocal scratch pad — internal data-fetch methods write a
    // human-readable "why did I return null/empty?" string here so the
    // *ResultAsync wrappers can surface it as ApiResult.TechnicalDetail.
    // Surfaces things like "API returned data:null + here are the
    // powerstation IDs your account CAN see: A,B,C" so the operator
    // can fix a typo'd PlantId from the probe output.
    //
    // The holder is a mutable box installed by the *ResultAsync wrapper.
    // A bare AsyncLocal<string> never worked here: a value set inside an
    // awaited async method doesn't flow back out to its caller, so every
    // TakeDiag() returned null and the reason was lost.
    private sealed class DiagBox { public string? Text; }
    private static readonly AsyncLocal<DiagBox?> _diagBox = new();
    private static void BeginDiag() => _diagBox.Value = new DiagBox();
    private static void SetDiag(string s) { if (_diagBox.Value is { } box) box.Text = s; }
    private static string? TakeDiag()
    {
        var box = _diagBox.Value;
        var s = box?.Text;
        if (box != null) box.Text = null;
        return s;
    }

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
            // Same key LoginAsync uses, so this test shares the per-account caches.
            session.AccountKey = $"{credentials.Username}|{credentials.Password?.Length ?? 0}";

            if (!string.IsNullOrEmpty(credentials.PlantId))
            {
                BeginDiag();
                var detail = await GetStationDetailAsync(session, credentials.PlantId);
                if (detail == null)
                {
                    var why = TakeDiag() ?? "ไม่ทราบสาเหตุ";
                    // The station is in the account's list with live figures:
                    // monitoring works, just without the per-inverter detail.
                    var fromList = await RealtimeFromListAsync(session, credentials.PlantId);
                    if (fromList != null)
                        return (true, $"Connected — ใช้ข้อมูลจากรายการสถานี (สถานะ/กำลังผลิต/ผลิตวันนี้) " +
                                      $"เพราะ SEMS ไม่ส่งรายละเอียดสถานีนี้ให้บัญชีนี้ — ไม่มีข้อมูลรายอินเวอร์เตอร์/กริด. ({why})");
                    return (false, $"เข้าสู่ระบบได้ แต่ดึงข้อมูลสถานีไม่ได้: {why}");
                }
                if (!StationIdMatches(detail.Value.StationId, credentials.PlantId) || detail.Value.StationId != credentials.PlantId.Trim())
                    return (true, $"Connected — ใช้ Station ID '{detail.Value.StationId}' (ที่บันทึกไว้: '{credentials.PlantId}')");
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
        if (session == null) { SetDiag("เข้าสู่ระบบ SEMS ไม่สำเร็จ"); return null; }

        var detail = await GetStationDetailAsync(session, credentials.PlantId);
        if (detail == null) return null;
        var data = detail.Value.Data;

        string? name = null;
        decimal capacity = 0;
        string? address = null;

        if (data.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            name = GetString(info, "stationname") ?? GetString(info, "name");
            address = GetString(info, "address");
            capacity = GetDecimal(info, "capacity");
        }

        if (capacity == 0 && data.TryGetProperty("kpi", out var kpi))
            capacity = GetDecimal(kpi, "capacity");

        return new PlantInfo
        {
            PlantId = detail.Value.StationId,
            PlantName = name ?? "",
            CapacityKw = capacity,
            Address = address
        };
    }

    /// <summary>
    /// Fetches the user's full station list via the SEMS portal's
    /// HistoryPage endpoint (which is what the web UI uses).  Falls back
    /// through a couple of endpoint names because different SEMS regions
    /// host this list under slightly different paths.
    /// Returns an empty list rather than throwing on any error.
    /// </summary>
    /// <summary>
    /// Lists the SEMS account's power stations so the operator can pick the
    /// GUID rather than copy it out of a portal URL — the mismatch that
    /// makes GoodWe look "connected but returning no data".
    /// </summary>
    public async Task<List<PlantOption>> DiscoverAsync(ApiCredentials credentials, CancellationToken ct = default)
    {
        var options = new List<PlantOption>();
        var session = await LoginAsync(credentials);
        if (session == null) return options;

        foreach (var (id, name) in await ListStationsAsync(session))
            options.Add(new PlantOption(id, name, "plant", Recommended: true));

        return options;
    }

    private async Task<List<(string Id, string Name)>> ListStationsAsync(Session session)
        => (await ListStationRowsAsync(session)).Select(r => (r.Id, r.Name)).ToList();

    /// <summary>One station as the account-wide list reports it.</summary>
    private sealed record StationRow(string Id, string Name, int? Status, decimal? PacW, decimal? TodayKwh,
        decimal? TotalKwh = null, decimal? CapacityKw = null, string? Raw = null)
    {
        /// <summary>The row carries live figures, not just a name.</summary>
        public bool HasLiveData => Status.HasValue || PacW.HasValue || TodayKwh.HasValue;
    }

    /// <summary>
    /// Every station on the account, from whichever list endpoint this SEMS
    /// region answers, paged so accounts with more than 50 stations aren't
    /// cut off. One call covers all of an account's sites, which is what
    /// makes resolving every site's ID in a sync round cheap.
    /// </summary>
    private async Task<List<StationRow>> ListStationRowsAsync(Session session)
    {
        // One scan per account per 10 minutes. A wrong ID used to re-list the
        // whole account (up to 4 endpoints x 10 pages) on every call — three
        // at a time in a batch, every round.
        lock (_sessionCacheLock)
        {
            if (session.AccountKey.Length > 0
                && _stationListCache.TryGetValue(session.AccountKey, out var hit) && DateTime.UtcNow < hit.ExpiresAt)
                return hit.Rows;
        }
        var rows = await ScanStationRowsAsync(session);
        if (session.AccountKey.Length > 0 && rows.Count > 0)
            lock (_sessionCacheLock) _stationListCache[session.AccountKey] = (rows, DateTime.UtcNow.Add(StationListTtl));
        return rows;
    }

    private async Task<List<StationRow>> ScanStationRowsAsync(Session session)
    {
        var results = new List<StationRow>();
        var endpoints = new[]
        {
            "v2/HistoryData/QueryPowerStationByHistory",
            "v2/PowerStation/QueryPowerStation",
            "v3/PowerStationMonitor/QueryPowerStationMonitor",
            "v2/PowerStationMonitor/QueryPowerStationMonitorForApp",
        };

        foreach (var path in endpoints)
        {
            for (var page = 1; page <= 10; page++)
            {
                var json = await PostAsync(session, path,
                    new { page_index = page, page_size = 50, pageIndex = page, pageSize = 50, order_by = "", condition = "", key = "" });
                if (json == null || !json.Value.TryGetProperty("data", out var d)) break;

                // Two response shapes: { data: { list: [...] } } or { data: [...] }
                JsonElement list;
                if (d.ValueKind == JsonValueKind.Array) list = d;
                else if (d.ValueKind == JsonValueKind.Object
                         && d.TryGetProperty("list", out var l) && l.ValueKind == JsonValueKind.Array)
                    list = l;
                else break;

                var before = results.Count;
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var id = FirstString(item, "powerstation_id", "powerStationId", "powerstationId", "stationId", "id");
                    if (string.IsNullOrEmpty(id) || results.Any(r => StationIdMatches(r.Id, id))) continue;
                    var name = FirstString(item, "stationname", "station_name", "stationName", "name") ?? "(unnamed)";
                    int? status = item.TryGetProperty("status", out var st) ? ToInt(st) : null;
                    results.Add(new StationRow(id, name, status,
                        FirstDecimal(item, "pac", "out_pac", "currentPower", "current_power"),
                        FirstDecimal(item, "eday", "today_power", "todayPower", "power", "eDay"),
                        FirstDecimal(item, "etotal", "total_power", "totalPower", "eTotal"),
                        FirstDecimal(item, "capacity", "installedCapacity"),
                        Truncate(item.GetRawText(), 700)));
                }
                // A page with nothing new is the end (or an endpoint that
                // ignores paging and returns the same page every time).
                if (results.Count == before || list.GetArrayLength() < 50) break;
            }
            if (results.Count > 0) return results;
        }
        return results;
    }

    /// <summary>
    /// Lenient station-id comparison.  SEMS stores IDs as lower-case
    /// hyphenated UUIDs ("04acf5fb-3843-4b9f-aff3-c67796bd91c2"); operators
    /// occasionally store them upper-cased, without hyphens, or with
    /// surrounding whitespace.  All of those should still match.
    /// </summary>
    private static bool StationIdMatches(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        static string Normalize(string s) => s.Trim().Replace("-", "").Replace("{", "").Replace("}", "").ToLowerInvariant();
        return Normalize(a) == Normalize(b);
    }

    public async Task<RealtimeData?> GetRealtimeDataAsync(ApiCredentials credentials)
    {
        var session = await LoginAsync(credentials);
        if (session == null) { SetDiag("เข้าสู่ระบบ SEMS ไม่สำเร็จ"); return null; }

        var detail = await GetStationDetailAsync(session, credentials.PlantId);
        if (detail != null) return ParseRealtime(detail.Value.Data);

        var fromList = await RealtimeFromListAsync(session, credentials.PlantId);
        if (fromList != null)
            _logger.LogInformation("GoodWe {Sid}: detail empty, using the station list's live figures", credentials.PlantId);
        return fromList;
    }

    /// <summary>
    /// GetMonitorDetailByPowerstationId → RealtimeData.
    ///
    /// Before, a response with no usable station data still produced a
    /// RealtimeData of all zeros with IsOnline=false — so a wrong station ID,
    /// a region mismatch or a SEMS error was reported as "OFFLINE, 0 kW" on a
    /// site that SEMS itself showed running at 57 kW. Unusable responses now
    /// stop at GetStationDetailAsync and surface as an API error with the
    /// reason; only real station data reaches this method.
    ///
    /// Online is decided from what the station actually reports — any
    /// inverter communicating, the station status, or power being produced —
    /// not from info.status == 1 alone, which some SEMS versions omit.
    /// </summary>
    private static RealtimeData ParseRealtime(JsonElement data)
    {
        var kpi = data.TryGetProperty("kpi", out var k) ? k : default;
        var info = data.TryGetProperty("info", out var i) ? i : default;
        var powerflow = data.TryGetProperty("powerflow", out var pf) ? pf : default;
        var soc = data.TryGetProperty("soc", out var socEl) ? socEl : default;

        // Station totals first; the per-inverter rows fill any gap.
        decimal pacW = GetDecimal(kpi, "pac");
        decimal todayKwh = GetDecimal(kpi, "power");
        decimal totalKwh = GetDecimal(kpi, "total_power");

        decimal invPacW = 0, invToday = 0, invTotal = 0;
        int invCount = 0, invConnected = 0;
        if (data.TryGetProperty("inverter", out var invs) && invs.ValueKind == JsonValueKind.Array)
        {
            foreach (var inv in invs.EnumerateArray())
            {
                if (inv.ValueKind != JsonValueKind.Object) continue;
                invCount++;
                var full = inv.TryGetProperty("invert_full", out var f) ? f : default;
                invPacW += FirstDecimal(inv, "out_pac", "pac") ?? FirstDecimal(full, "pac") ?? 0;
                invToday += FirstDecimal(inv, "eday") ?? FirstDecimal(full, "eday") ?? 0;
                invTotal += FirstDecimal(inv, "etotal") ?? FirstDecimal(full, "etotal") ?? 0;
                var st = (inv.TryGetProperty("status", out var s1) ? ToInt(s1) : null)
                         ?? (full.ValueKind == JsonValueKind.Object && full.TryGetProperty("status", out var s2) ? ToInt(s2) : null);
                // SEMS: -1 offline, 0 waiting, 1 generating, 2 fault. Only -1
                // means the inverter isn't talking to the cloud.
                if (st.HasValue && st.Value != -1) invConnected++;
            }
        }
        if (pacW <= 0 && invPacW > 0) pacW = invPacW;
        if (todayKwh <= 0 && invToday > 0) todayKwh = invToday;
        if (totalKwh <= 0 && invTotal > 0) totalKwh = invTotal;

        // powerflow values are strings with units — "57209(W)" — which the
        // old number parser read as 0, so grid and load were always zero.
        if (pacW <= 0 && powerflow.ValueKind == JsonValueKind.Object)
            pacW = ParseWatts(powerflow, "pv") ?? 0;

        decimal gridExportKw = 0, gridImportKw = 0;
        decimal? batterySoc = null, batteryPowerKw = null;
        if (powerflow.ValueKind == JsonValueKind.Object)
        {
            // gridStatus: 1 = buying (import), -1 = selling (export)
            var gridStatus = GetDecimal(powerflow, "gridStatus");
            var gridKw = (ParseWatts(powerflow, "grid") ?? 0) / 1000m;
            if (gridStatus < 0) gridExportKw = Math.Abs(gridKw);
            else gridImportKw = Math.Abs(gridKw);

            var bettStatus = GetDecimal(powerflow, "bettStatus");
            var bettKw = (ParseWatts(powerflow, "bett") ?? 0) / 1000m;
            if (bettKw != 0) batteryPowerKw = bettStatus < 0 ? -Math.Abs(bettKw) : Math.Abs(bettKw);
        }
        if (soc.ValueKind == JsonValueKind.Object)
            batterySoc = GetNullableDecimal(soc, "power");  // SEMS puts SOC% in "power"

        int? stationStatus = info.ValueKind == JsonValueKind.Object && info.TryGetProperty("status", out var ss)
            ? ToInt(ss) : null;
        var isOnline = invConnected > 0
                       || (stationStatus.HasValue && stationStatus.Value != -1)
                       || pacW > 0;

        return new RealtimeData
        {
            Timestamp = DateTime.UtcNow,
            CurrentPowerKw = pacW / 1000m,   // SEMS returns Watts
            TodayEnergyKwh = todayKwh,       // already kWh
            TotalEnergyKwh = totalKwh,
            GridExportKw = gridExportKw,
            GridImportKw = gridImportKw,
            BatterySocPercent = batterySoc ?? GetNullableDecimal(kpi, "soc"),
            BatteryPowerKw = batteryPowerKw,
            IsOnline = isOnline,
            Health = isOnline ? HealthState.Healthy : HealthState.Disconnected,
        };
    }

    /// <summary>
    /// The station's detail payload, or null with the reason in the diag
    /// scratch pad. Resolves the stored ID against the account's station list
    /// when the direct lookup comes back empty (case, hyphens, a typo), and
    /// remembers the resolution for the session.
    /// </summary>
    private async Task<(JsonElement Data, string StationId)?> GetStationDetailAsync(Session session, string? plantId)
    {
        if (string.IsNullOrWhiteSpace(plantId))
        {
            SetDiag("ยังไม่ได้ใส่ Plant/Station ID ของ GoodWe");
            return null;
        }

        var cacheKey = session.AccountKey + "|" + plantId.Trim().ToLowerInvariant();
        string stationId;
        lock (_sessionCacheLock)
            stationId = _resolvedStations.TryGetValue(cacheKey, out var known) ? known : plantId.Trim();

        var (data, error) = await FetchDetailAnyAsync(session, stationId);
        if (data != null)
        {
            lock (_sessionCacheLock) _resolvedStations[cacheKey] = stationId;
            return (data.Value, stationId);
        }

        // The direct lookup failed: is the ID on this account under another spelling?
        var stations = await ListStationRowsAsync(session);
        var match = stations.FirstOrDefault(r => StationIdMatches(r.Id, plantId));
        if (match != null && match.Id != stationId)
        {
            var (data2, error2) = await FetchDetailAnyAsync(session, match.Id);
            if (data2 != null)
            {
                _logger.LogInformation("GoodWe: PlantId '{Stored}' resolved to '{Found}' from the account's station list",
                    plantId, match.Id);
                lock (_sessionCacheLock) _resolvedStations[cacheKey] = match.Id;
                return (data2.Value, match.Id);
            }
            error = error2;
        }
        if (match != null)
            lock (_sessionCacheLock) _resolvedStations[cacheKey] = match.Id;

        var available = stations.Count == 0
            ? "บัญชีนี้ไม่มีสถานีให้เห็นเลย (หรือ SEMS ไม่ส่งรายการสถานีมา)"
            : "สถานีที่บัญชีนี้เห็น: " + string.Join(", ", stations.Take(10).Select(r => $"{r.Name} [{r.Id}]"));
        SetDiag(match == null
            ? $"ไม่พบ Station ID '{plantId}' ในบัญชี SEMS นี้ — {available}. ({error})"
            : $"พบสถานีแต่ SEMS ไม่ส่งข้อมูลรายละเอียดกลับมา: {error}. แถวในรายการสถานี: {match.Raw}");
        return null;
    }

    /// <summary>
    /// Detail endpoints, in the order tried. The v2 monitor-detail call is
    /// what most accounts answer; stations shared into an account from
    /// another organisation, or on SEMS+, came back with "data": {} there —
    /// the station is in the account's list but the legacy call won't
    /// describe it — so the newer plant-detail call and the v1/v3 variants
    /// are tried before giving up.
    /// </summary>
    private static readonly string[] DetailPaths =
    {
        "v2/PowerStation/GetMonitorDetailByPowerstationId",
        "v3/PowerStation/GetPlantDetailByPowerstationId",
        "v1/PowerStation/GetMonitorDetailByPowerstationId",
        "v3/PowerStation/GetMonitorDetailByPowerstationId",
    };

    /// <summary>Which detail path answered for an account (guarded by _sessionCacheLock).</summary>
    private static readonly Dictionary<string, string> _detailPathByAccount = new();

    /// <summary>Stations whose detail is empty on every path — skipped for an hour, then retried.</summary>
    private static readonly Dictionary<string, DateTime> _detailEmptyUntil = new();

    private async Task<(JsonElement? Data, string Error)> FetchDetailAnyAsync(Session session, string stationId)
    {
        var emptyKey = session.AccountKey + "|" + stationId.ToLowerInvariant();
        string? preferred;
        lock (_sessionCacheLock)
        {
            if (_detailEmptyUntil.TryGetValue(emptyKey, out var until) && DateTime.UtcNow < until)
                return (null, "ทุก endpoint รายละเอียดตอบว่างในรอบก่อน (จะลองใหม่ภายใน 1 ชม.)");
            _detailPathByAccount.TryGetValue(session.AccountKey, out preferred);
        }

        var order = preferred == null ? DetailPaths : DetailPaths.OrderBy(x => x == preferred ? 0 : 1).ToArray();
        var errors = new List<string>();
        foreach (var path in order)
        {
            var (data, error) = await FetchDetailAsync(session, stationId, path);
            if (data != null)
            {
                lock (_sessionCacheLock) _detailPathByAccount[session.AccountKey] = path;
                return (data, "");
            }
            errors.Add($"{path.Split('/').Last()}@{path.Split('/')[0]}: {error}");
        }
        lock (_sessionCacheLock) _detailEmptyUntil[emptyKey] = DateTime.UtcNow.AddHours(1);
        return (null, string.Join(" | ", errors));
    }

    /// <summary>
    /// Live figures from the account's station list, for a station whose
    /// detail endpoints return nothing. Coarser than the detail (no grid or
    /// battery split, no per-inverter rows) but it is what SEMS shows in its
    /// own station list: status, current power, today's and total energy.
    /// Read fresh — the cached list is for ID resolution, not live values.
    /// </summary>
    private async Task<RealtimeData?> RealtimeFromListAsync(Session session, string? plantId,
        List<StationRow>? freshRows = null)
    {
        var rows = freshRows ?? await ScanStationRowsAsync(session);
        var row = rows.FirstOrDefault(r => StationIdMatches(r.Id, plantId));
        if (row == null || !row.HasLiveData) return null;

        var pacW = row.PacW ?? 0;
        // SEMS list "status": -1 offline, 0 waiting, 1 generating, 2 fault.
        var online = (row.Status.HasValue && row.Status.Value != -1) || pacW > 0;
        return new RealtimeData
        {
            Timestamp = DateTime.UtcNow,
            CurrentPowerKw = pacW / 1000m,
            TodayEnergyKwh = row.TodayKwh ?? 0,
            TotalEnergyKwh = row.TotalKwh ?? 0,
            IsOnline = online,
            Health = online ? HealthState.Healthy : HealthState.Disconnected,
        };
    }

    /// <summary>
    /// One GetMonitorDetailByPowerstationId call, judged honestly: an error
    /// flag, a non-zero code or a payload with no station data is a failure
    /// with a reason — not a station that happens to read zero.
    /// </summary>
    private async Task<(JsonElement? Data, string Error)> FetchDetailAsync(Session session, string stationId, string path)
    {
        var json = await PostAsync(session, path, new { powerStationId = stationId });
        if (json == null) return (null, "SEMS ไม่ตอบกลับ / HTTP error");

        var root = json.Value;
        if (root.TryGetProperty("hasError", out var he) && he.ValueKind == JsonValueKind.True)
            return (null, $"SEMS error: {GetString(root, "msg") ?? "unknown"} (code {GetString(root, "code") ?? "?"})");

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return (null, $"SEMS ตอบกลับโดยไม่มีข้อมูลสถานี (data={(root.TryGetProperty("data", out var dd) ? dd.ValueKind.ToString() : "missing")}, msg={GetString(root, "msg") ?? "-"})");

        bool HasObj(string name) => data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object;
        bool HasRows(string name) => data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0;
        if (!HasObj("kpi") && !HasObj("info") && !HasRows("inverter"))
        {
            var keys = string.Join(",", data.EnumerateObject().Select(p => p.Name).Take(20));
            return (null, $"ข้อมูลสถานีว่าง (ไม่มี kpi/info/inverter; keys: {keys})");
        }
        return (data.Clone(), "");
    }

    public async Task<List<EnergyRecord>> GetHistoricalDataAsync(
        ApiCredentials credentials, DateTime from, DateTime to)
    {
        var records = new List<EnergyRecord>();
        var session = await LoginAsync(credentials);
        if (session == null) { SetDiag("Login returned null session"); return records; }

        // Resolve PlantId via the user's station list if the stored one
        // doesn't match (case / hyphens / typo).  Same logic as
        // GetPlantInfoAsync, just inlined so the historical call uses the
        // right ID without needing PlantInfo to succeed first.
        var resolvedPlantId = (await GetStationDetailAsync(session, credentials.PlantId))?.StationId
                              ?? credentials.PlantId ?? "";

        // Endpoint catalog — try each in order until one returns rows.
        // The first is what the SEMS web UI uses on EU/global; the others
        // are alternates used by various regions / older portals.  Each
        // accepts powerStationId + date and returns daily rollup data.
        var dateStr = from.ToString("yyyy-MM-dd");
        var dayCount = Math.Min(365, (to - from).Days + 1);
        var attempts = new (string Path, object Body)[]
        {
            ("v2/PowerStationMonitor/GetPowerStationPowerAndIncomeByDay",
                new { powerStationId = resolvedPlantId, date = dateStr, count = dayCount }),
            ("v2/HistoryData/QueryPowerStationByHistory",
                new { powerStationId = resolvedPlantId, date = dateStr, count = dayCount }),
            ("v1/PowerStationMonitor/GetPowerStationPowerAndIncomeByDay",
                new { powerStationId = resolvedPlantId, date = dateStr, count = dayCount }),
            ("v2/Charts/GetPlantPowerChart",
                new { id = resolvedPlantId, date = dateStr, full_script = false }),
        };

        var trace = new List<string>();
        foreach (var (path, body) in attempts)
        {
            var json = await PostAsync(session, path, body);
            if (json == null) { trace.Add($"{path}:null"); continue; }

            // Pull rows out of any of the known shapes
            // - { data: [...] }
            // - { data: { list: [...] } }
            // - { data: { lines: [{ xy: [{ x, y }] }] } } (Chart endpoint)
            JsonElement rowsArr = default;
            bool foundArr = false;
            if (json.Value.TryGetProperty("data", out var d))
            {
                if (d.ValueKind == JsonValueKind.Array) { rowsArr = d; foundArr = true; }
                else if (d.ValueKind == JsonValueKind.Object)
                {
                    if (d.TryGetProperty("list", out var l) && l.ValueKind == JsonValueKind.Array)
                    { rowsArr = l; foundArr = true; }
                    else if (d.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
                    {
                        // Chart format — flatten lines[].xy[] into our row shape.
                        var before = records.Count;
                        foreach (var line in lines.EnumerateArray())
                        {
                            if (!line.TryGetProperty("xy", out var xy) || xy.ValueKind != JsonValueKind.Array) continue;
                            foreach (var pt in xy.EnumerateArray())
                            {
                                if (pt.ValueKind != JsonValueKind.Object) continue;
                                var date = ToDateTime(pt.TryGetProperty("x", out var xv) ? xv : default);
                                var val = GetDecimal(pt, "y");
                                if (date == null) continue;
                                records.Add(new EnergyRecord
                                {
                                    Timestamp = date.Value,
                                    EnergyGenerationKwh = val,
                                });
                            }
                        }
                        trace.Add($"{path}:chart={records.Count - before}");
                        if (records.Count > 0) break;
                        continue;
                    }
                }
            }
            if (!foundArr) { trace.Add($"{path}:no-rows"); continue; }

            int beforeRows = records.Count;
            foreach (var item in rowsArr.EnumerateArray())
            {
                try
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
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
                    _logger.LogWarning(ex, "GoodWe: skipped bad historical item from {Path}", path);
                }
            }
            var added = records.Count - beforeRows;
            trace.Add($"{path}:+{added}");
            if (added > 0) break;
        }

        if (records.Count == 0)
        {
            SetDiag($"All {attempts.Length} historical endpoints returned 0 rows for resolved plantId={resolvedPlantId}. " +
                    $"Trace: {string.Join(" → ", trace)}");
        }
        _logger.LogInformation(
            "GoodWe GetHistoricalData: collected {Count} rows for {Sid} {From} → {To}. Trace: {Trace}",
            records.Count, resolvedPlantId, from.Date, to.Date, string.Join(" → ", trace));
        return records;
    }

    public async Task<List<AlarmRecord>> GetAlarmsAsync(
        ApiCredentials credentials, DateTime from, DateTime to)
    {
        var session = await LoginAsync(credentials);
        if (session == null) return new List<AlarmRecord>();

        var stationId = (await GetStationDetailAsync(session, credentials.PlantId))?.StationId ?? credentials.PlantId;
        var byStation = await QueryWarningsAsync(session, stationId)
            ?? throw new InvalidOperationException("SEMS warning query failed");
        if (byStation.TryGetValue(Norm(stationId), out var mine)) return mine;
        if (!byStation.TryGetValue("", out var unnamed) || unnamed.Count == 0) return new List<AlarmRecord>();

        // Rows that name no station are this station's only if SEMS honoured
        // the filter. If the same rows come back for the whole account and
        // the account has other stations, the filter was ignored and they
        // could be anyone's — the way another site's fault landed on a
        // healthy one. Drop them rather than guess.
        var account = await QueryWarningsAsync(session, null);
        var stations = await ListStationRowsAsync(session);
        if (account != null && stations.Count > 1
            && account.TryGetValue("", out var accountUnnamed)
            && SameAlarms(unnamed, accountUnnamed))
        {
            _logger.LogWarning("GoodWe: warning query for {Sid} returned the account's unattributed alarms — ignored", stationId);
            return new List<AlarmRecord>();
        }
        return unnamed;
    }

    private static bool SameAlarms(List<AlarmRecord> a, List<AlarmRecord> b) =>
        a.Count == b.Count && a.Select(x => x.AlarmId).ToHashSet().SetEquals(b.Select(x => x.AlarmId));

    /// <summary>
    /// Warnings for every station on the account in one query, keyed by
    /// normalised station ID. Rows that don't say which station they belong
    /// to are dropped — on an account-wide query they could be anyone's,
    /// which is how another site's fault ended up on a healthy one.
    /// </summary>
    public async Task<Dictionary<string, List<AlarmRecord>>> GetAccountAlarmsAsync(ApiCredentials credentials)
    {
        var session = await LoginAsync(credentials);
        if (session == null) return new Dictionary<string, List<AlarmRecord>>();
        var all = await QueryWarningsAsync(session, null)
            ?? throw new InvalidOperationException("SEMS warning query failed");
        all.Remove("");
        return all;
    }

    private static string Norm(string? id) =>
        (id ?? "").Trim().Replace("-", "").Replace("{", "").Replace("}", "").ToLowerInvariant();

    /// <summary>
    /// PowerstationWarningsQuery, grouped by station. With a station ID, rows
    /// naming a different station are discarded (in case the filter is
    /// ignored); rows naming none are kept under "".
    /// </summary>
    private async Task<Dictionary<string, List<AlarmRecord>>?> QueryWarningsAsync(Session session, string? stationId)
    {
        // Null = the query failed. An empty result would be logged as a
        // successful fetch and block the retry for an hour.
        var result = new Dictionary<string, List<AlarmRecord>>();
        var json = await PostAsync(session, "v2/Warning/PowerstationWarningsQuery",
            new
            {
                // Both spellings: SEMS versions differ, unknown keys are ignored.
                powerstationId = stationId ?? "",
                stationid = stationId ?? "",
                pageIndex = 1,
                pageSize = 200,
                range = 0,
                adcode = "",
                condition = ""
            });
        if (json == null) return null;
        if (json.Value.TryGetProperty("hasError", out var he) && he.ValueKind == JsonValueKind.True) return null;
        var wantedNorm = stationId == null ? null : Norm(stationId);
        var alarms = new List<(string Station, AlarmRecord Alarm)>();

        if (!json.Value.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("list", out var list)
            || list.ValueKind != JsonValueKind.Array)
            return result;

        var dayFirst = DetectDayFirst(list);

        // SEMS groups warnings by station: data.list[] is one entry per
        // station with its warnings in a nested array. The old parser read
        // each station entry as if it were a warning, so it never found the
        // id, name, device, time or recovery state — and filled the gaps with
        // a random Guid and DateTime.UtcNow. Every hourly sync therefore
        // produced a "new", still-open, nameless alarm with a fresh code:
        // a phantom fault on a site that was running normally. A flat list
        // of warnings is still accepted, in case the response varies.
        var skipped = 0;
        string? skippedSample = null;
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;

            // When the station is named on the entry, it must be ours — the
            // query is per station, but a filter the API ignores would hand
            // us every station on the account.
            var entryStation = FirstString(entry, "stationId", "stationid", "powerstationId",
                "powerstation_id", "pw_id", "pwId");
            if (entryStation != null && wantedNorm != null && Norm(entryStation) != wantedNorm)
                continue;

            var nested = FirstArray(entry, "warning", "warnings", "warninglist", "warningList", "list", "data");
            var items = nested.HasValue ? nested.Value.EnumerateArray().ToList() : new List<JsonElement> { entry };

            foreach (var item in items)
            {
                try
                {
                    var alarm = ParseWarning(item, dayFirst);
                    if (alarm == null)
                    {
                        skipped++;
                        skippedSample ??= Truncate(item.GetRawText(), 800);
                        continue;
                    }
                    // The station can be named on the group or on the row itself.
                    var rowStation = FirstString(item, "stationId", "stationid", "powerstationId",
                        "powerstation_id", "pw_id", "pwId") ?? entryStation;
                    if (rowStation != null && wantedNorm != null && Norm(rowStation) != wantedNorm)
                        continue;
                    alarms.Add((rowStation == null ? "" : Norm(rowStation), alarm));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Skipped bad alarm item");
                }
            }
        }

        // An alarm we can't identify is dropped rather than invented. The raw
        // shape is logged so the field names can be checked against SEMS.
        if (skipped > 0)
            _logger.LogWarning(
                "GoodWe alarms for station {Sid}: skipped {Skipped} item(s) without a recognisable name/time. Sample: {Sample}",
                stationId ?? "(account)", skipped, skippedSample);

        foreach (var g in alarms.GroupBy(a => a.Station))
            result[g.Key] = g.Select(a => a.Alarm).ToList();

        return result;
    }

    /// <summary>Everything one sync round needs for the stations on one SEMS account.</summary>
    public sealed record AccountBatch(
        Dictionary<string, ApiResult<RealtimeData>> Realtime,
        Dictionary<string, List<AlarmRecord>>? Alarms);

    /// <summary>
    /// All of an account's stations in one pass: one login, the station list
    /// once (to resolve every stored ID), each station's detail a few at a
    /// time, and — when asked — the account's warnings in a single query
    /// shared out by station. Before, every site logged in, probed and
    /// queried warnings on its own, so ten sites on one account cost ten of
    /// each and tripped SEMS's back-to-back throttling.
    ///
    /// SEMS has no endpoint that returns full live data for many stations in
    /// one call, so the per-station detail stays — but it now shares the
    /// session and runs a few at a time instead of strictly one by one.
    /// Keys of both dictionaries are the PlantIds as passed in.
    /// </summary>
    public async Task<AccountBatch> GetAccountBatchAsync(
        ApiCredentials credentials, IReadOnlyCollection<string> plantIds, bool includeAlarms)
    {
        var realtime = new Dictionary<string, ApiResult<RealtimeData>>(StringComparer.OrdinalIgnoreCase);
        var session = await LoginAsync(credentials);
        if (session == null)
        {
            foreach (var pid in plantIds)
                realtime[pid] = ApiResult<RealtimeData>.Fail(ApiResultKind.AuthenticationFailed,
                    "เข้าสู่ระบบ SEMS ไม่สำเร็จ — ตรวจอีเมล/รหัสผ่าน SEMS");
            return new AccountBatch(realtime, null);
        }

        var resolved = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Scanned at most once per round, and only if some station needs it.
        var freshList = new Lazy<Task<List<StationRow>>>(() => ScanStationRowsAsync(session));
        using var gate = new SemaphoreSlim(3);
        var tasks = plantIds.Distinct(StringComparer.OrdinalIgnoreCase).Select(async pid =>
        {
            await gate.WaitAsync();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                BeginDiag();
                var detail = await GetStationDetailAsync(session, pid);
                if (detail == null)
                {
                    var rows = await freshList.Value;
                    var fromList = await RealtimeFromListAsync(session, pid, rows);
                    if (fromList != null)
                    {
                        sw.Stop();
                        var match = rows.FirstOrDefault(r => StationIdMatches(r.Id, pid));
                        if (match != null) resolved[pid] = match.Id;
                        return (pid, fromList.IsOnline
                            ? ApiResult<RealtimeData>.Ok(fromList, "Device online (station list)", sw.Elapsed)
                            : ApiResult<RealtimeData>.Offline(fromList, "Station list reports offline", sw.Elapsed));
                    }
                }
                sw.Stop();
                if (detail == null)
                {
                    var why = TakeDiag() ?? "GoodWe API returned no data.";
                    return (pid, ApiResult<RealtimeData>.Fail(
                        why.StartsWith("ไม่พบ Station ID") ? ApiResultKind.PlantNotFound : ApiResultKind.CloudServerError,
                        why, detail: why, duration: sw.Elapsed));
                }
                resolved[pid] = detail.Value.StationId;
                var rt = ParseRealtime(detail.Value.Data);
                return (pid, rt.IsOnline
                    ? ApiResult<RealtimeData>.Ok(rt, "Device online", sw.Elapsed)
                    : ApiResult<RealtimeData>.Offline(rt,
                        "GoodWe API connected OK, but every inverter reports offline. Check inverter WiFi.", sw.Elapsed));
            }
            catch (Exception ex)
            {
                sw.Stop();
                return (pid, ApiResultClassifier.ClassifyException<RealtimeData>(ex, "GoodWe", sw.Elapsed));
            }
            finally { gate.Release(); }
        });
        foreach (var (pid, result) in await Task.WhenAll(tasks))
            realtime[pid] = result;

        Dictionary<string, List<AlarmRecord>>? alarms = null;
        if (includeAlarms)
        {
            var account = await QueryWarningsAsync(session, null);
            if (account != null)
            {
                alarms = new Dictionary<string, List<AlarmRecord>>(StringComparer.OrdinalIgnoreCase);
                var attributed = account.Keys.Any(k => k != "");
                var accountUnnamed = account.TryGetValue("", out var au) ? au : new List<AlarmRecord>();
                foreach (var pid in plantIds)
                {
                    var sid = resolved.TryGetValue(pid, out var r) ? r : pid;
                    if (attributed || accountUnnamed.Count == 0)
                    {
                        alarms[pid] = account.TryGetValue(Norm(sid), out var list) ? list : new List<AlarmRecord>();
                        continue;
                    }

                    // The account-wide answer didn't say which station each row
                    // belongs to — ask per station. If that returns the very
                    // same rows, SEMS ignored the filter and they can't be
                    // attributed; better none than another site's.
                    var mine = await QueryWarningsAsync(session, sid);
                    if (mine == null) { alarms.Remove(pid); continue; }
                    if (mine.TryGetValue(Norm(sid), out var l1)) { alarms[pid] = l1; continue; }
                    var unnamed = mine.TryGetValue("", out var l2) ? l2 : new List<AlarmRecord>();
                    alarms[pid] = plantIds.Count > 1 && unnamed.Count > 0 && SameAlarms(unnamed, accountUnnamed)
                        ? new List<AlarmRecord>()
                        : unnamed;
                }
            }
        }

        return new AccountBatch(realtime, alarms);
    }

    public async Task<List<DeviceInfo>> GetDevicesAsync(ApiCredentials credentials)
    {
        var devices = new List<DeviceInfo>();
        var session = await LoginAsync(credentials);
        if (session == null) { SetDiag("เข้าสู่ระบบ SEMS ไม่สำเร็จ"); return devices; }

        var detail = await GetStationDetailAsync(session, credentials.PlantId);
        var why = TakeDiag();
        string stationId;
        lock (_sessionCacheLock)
            stationId = _resolvedStations.TryGetValue(session.AccountKey + "|" + (credentials.PlantId ?? "").Trim().ToLowerInvariant(), out var r)
                ? r : (credentials.PlantId ?? "").Trim();
        if (detail != null) stationId = detail.Value.StationId;

        if (detail != null
            && detail.Value.Data.TryGetProperty("inverter", out var inverters) && inverters.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in inverters.EnumerateArray())
            {
                var dev = ParseInverter(item);
                if (dev != null) devices.Add(dev);
            }
        }

        // SEMS+ plant detail (the v3 call that answers for shared / SEMS+
        // stations) carries info and kpi but no inverter rows. Inverters may
        // sit elsewhere in that payload, or behind a device endpoint whose
        // name differs by SEMS version — so: search the detail first, then
        // each candidate endpoint, taking any object that carries a serial.
        var probe = new List<string>();
        if (devices.Count == 0 && detail != null)
        {
            devices.AddRange(FindInverters(detail.Value.Data));
            if (devices.Count > 0) probe.Add("detail: found nested");
        }
        if (devices.Count == 0)
        {
            var candidates = new (string Path, object Body)[]
            {
                ("v2/PowerStation/GetInverterAllPoint", new { powerStationId = stationId }),
                ("v1/PowerStation/GetInverterAllPoint", new { powerStationId = stationId }),
                ("v3/PowerStation/GetInverterAllPoint", new { powerStationId = stationId }),
                ("v3/PowerStation/GetDeviceListByPowerStationId", new { powerStationId = stationId, pageIndex = 1, pageSize = 50 }),
                ("v2/Device/GetInverterListByPowerStationId", new { powerStationId = stationId }),
                ("v3/Device/GetDeviceList", new { powerStationId = stationId, stationId, pageIndex = 1, pageSize = 50 }),
                ("v2/PowerStation/GetMonitorDetailByPowerstationId", new { powerStationId = stationId }),
            };
            foreach (var (path, body) in candidates)
            {
                var json = await PostAsync(session, path, body);
                if (json == null) { probe.Add($"{path}: no reply"); continue; }
                if (json.Value.TryGetProperty("hasError", out var he) && he.ValueKind == JsonValueKind.True)
                { probe.Add($"{path}: error {GetString(json.Value, "msg")}"); continue; }
                if (!json.Value.TryGetProperty("data", out var d)) { probe.Add($"{path}: no data"); continue; }
                var found = FindInverters(d);
                probe.Add($"{path}: {(found.Count > 0 ? $"{found.Count} found" : DescribeShape(d))}");
                if (found.Count == 0) continue;
                devices.AddRange(found);
                break;
            }
        }

        if (devices.Count == 0)
        {
            SetDiag((detail == null
                    ? $"{why ?? "ดึงรายละเอียดสถานีไม่ได้"} — "
                    : $"SEMS ส่งข้อมูลสถานี {stationId} มา (keys: {string.Join(",", detail.Value.Data.EnumerateObject().Select(p => p.Name))}) แต่ไม่มีรายการอินเวอร์เตอร์ — ")
                + "ลอง endpoint อุปกรณ์แล้ว: " + string.Join(" | ", probe));
        }
        else
        {
            _logger.LogInformation("GoodWe {Sid}: {Count} inverter(s) via {Probe}", stationId, devices.Count, string.Join(" | ", probe));
        }
        return devices;
    }

    /// <summary>
    /// Every object in a payload that carries an inverter serial — wherever
    /// it sits. SEMS versions put inverter rows under different keys
    /// ("inverter", "inverterPoints", "list", "devices"…) and at different
    /// depths; the serial is the one thing they share.
    /// </summary>
    private static List<DeviceInfo> FindInverters(JsonElement root)
    {
        var found = new List<DeviceInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(JsonElement e, int depth)
        {
            if (depth > 6) return;
            if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in e.EnumerateArray()) Walk(x, depth + 1);
                return;
            }
            if (e.ValueKind != JsonValueKind.Object) return;
            var sn = FirstString(e, "sn", "inverterSn", "inverter_sn", "serialNumber", "deviceSn", "device_sn");
            if (sn != null && sn.Length >= 8 && seen.Add(sn) && ParseInverter(e) is { } dev)
            {
                found.Add(dev);
                return;
            }
            foreach (var p in e.EnumerateObject()) Walk(p.Value, depth + 1);
        }
        Walk(root, 0);
        return found;
    }

    /// <summary>A short description of a payload's shape for the diagnostic ("object{a,b,c}", "array[3]").</summary>
    private static string DescribeShape(JsonElement d) => d.ValueKind switch
    {
        JsonValueKind.Object => "object{" + string.Join(",", d.EnumerateObject().Select(p => p.Name).Take(12)) + "}",
        JsonValueKind.Array => $"array[{d.GetArrayLength()}]",
        _ => d.ValueKind.ToString().ToLowerInvariant()
    };

    private static DeviceInfo? ParseInverter(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        var full = item.TryGetProperty("invert_full", out var f) ? f : default;
        var sn = FirstString(item, "sn", "inverterSn", "inverter_sn", "serialNumber", "deviceSn", "device_sn") ?? FirstString(full, "sn");
        if (string.IsNullOrWhiteSpace(sn)) return null;
        var status = (item.TryGetProperty("status", out var s1) ? ToInt(s1) : null)
                     ?? (full.ValueKind == JsonValueKind.Object && full.TryGetProperty("status", out var s2) ? ToInt(s2) : null);
        return new DeviceInfo
        {
            DeviceId = sn,
            DeviceName = FirstString(item, "name", "relation_name", "inverterName") ?? FirstString(full, "name") ?? sn,
            DeviceType = "Inverter",
            SerialNumber = sn,
            Model = FirstString(item, "model", "model_type", "type", "inverterType") ?? FirstString(full, "model_type", "model"),
            FirmwareVersion = FirstString(item, "firmwareversion", "firmware_version", "fireware") ?? FirstString(full, "firmwareversion", "fireware"),
            RatedPowerKw = FirstDecimal(item, "capacity") ?? FirstDecimal(full, "capacity"),
            IsOnline = status.HasValue && status.Value != -1
        };
    }

    // ══════════════════════════════════════════════════════════════
    //  AUTHENTICATION - SEMS CrossLogin flow
    // ══════════════════════════════════════════════════════════════

    private sealed class Session
    {
        public string ApiBase { get; set; } = "";    // e.g. "https://eu.semsportal.com/api/"
        public string TokenJson { get; set; } = "";  // JSON to put in Token header
        /// <summary>Same key as the session cache — stable across re-logins, so per-account caches stay bounded.</summary>
        public string AccountKey { get; set; } = "";
    }

    /// <summary>The account's station list, reused for 10 minutes (guarded by _sessionCacheLock).</summary>
    private static readonly Dictionary<string, (List<StationRow> Rows, DateTime ExpiresAt)> _stationListCache = new();
    private static readonly TimeSpan StationListTtl = TimeSpan.FromMinutes(10);

    // Process-wide session cache: 6 API calls per installation × N installations
    // shouldn't re-login each time. GoodWe SEMS portal is rate-sensitive for
    // back-to-back logins (empty-data responses when hammered).
    // Keyed by account+password length to avoid leaking secrets; expires 20 min.
    private static readonly Dictionary<string, (Session Session, DateTime ExpiresAt)> _sessionCache = new();
    private static readonly object _sessionCacheLock = new();
    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(20);

    /// <summary>Stored PlantId → the station ID SEMS answers to, per session (guarded by _sessionCacheLock).</summary>
    private static readonly Dictionary<string, string> _resolvedStations = new();

    private static void InvalidateSession(string cacheKey)
    {
        lock (_sessionCacheLock) { _sessionCache.Remove(cacheKey); }
    }

    private async Task<Session?> LoginAsync(ApiCredentials credentials)
    {
        var cacheKey = $"{credentials.Username}|{credentials.Password?.Length ?? 0}";
        lock (_sessionCacheLock)
        {
            if (_sessionCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.ExpiresAt)
                return cached.Session;
        }

        var (session, _) = await LoginWithDetailAsync(credentials);
        if (session != null)
        {
            session.AccountKey = cacheKey;
            lock (_sessionCacheLock)
            {
                _sessionCache[cacheKey] = (session, DateTime.UtcNow.Add(SessionTtl));
            }
        }
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

            // Don't log the customer's account email (PII).  Mask to first 2
            // chars so it's still useful for correlating without leaking it.
            _logger.LogInformation("GoodWe: POST {Url} for account={Account}", url, MaskAccount(credentials.Username));

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
                // On auth failures invalidate the cached session so the next
                // call re-logins instead of reusing a dead session.
                if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403)
                {
                    // We don't have credentials here, so invalidate ALL sessions —
                    // only costs one re-login per affected account.
                    lock (_sessionCacheLock) { _sessionCache.Clear(); }
                }
                return null;
            }

            var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            // SEMS returns {code, msg, hasError, data} — 100/100001/100002 = expired/invalid session
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("code", out var codeEl))
            {
                var code = codeEl.ValueKind == JsonValueKind.Number ? codeEl.GetInt32()
                    : codeEl.ValueKind == JsonValueKind.String && int.TryParse(codeEl.GetString(), out var c) ? c
                    : 0;
                if (code == 100 || code == 100001 || code == 100002)
                {
                    _logger.LogWarning("GoodWe {Path} session invalid (code={Code}) — clearing cache",
                        relativePath, code);
                    lock (_sessionCacheLock) { _sessionCache.Clear(); }
                    return null;
                }
            }

            return root.Clone();
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

    /// <summary>
    /// One SEMS warning → AlarmRecord, or null when it doesn't carry enough to
    /// be a real, identifiable alarm (no name/code, or no time it happened).
    /// Field names vary between SEMS versions, so each is read from a short
    /// list of known spellings.
    /// </summary>
    private static AlarmRecord? ParseWarning(JsonElement item, bool dayFirst)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        var name = FirstString(item, "warningname", "warning_name", "warningName", "alarmname",
            "alarmName", "name", "warning");
        var code = FirstString(item, "warning_code", "warningcode", "warningCode", "errorcode",
            "error_code", "code");
        var desc = FirstString(item, "warning_desc", "warningdesc", "description", "desc");
        var device = FirstString(item, "deviceName", "devicename", "device_name", "deviceSN",
            "device_sn", "sn", "inverter_sn");

        DateTime? happened = null;
        foreach (var k in new[] { "happentime", "happen_time", "happenTime", "occurtime", "starttime", "startTime" })
            if (item.TryGetProperty(k, out var hv) && (happened = ToVendorUtc(hv, dayFirst)) != null) break;

        DateTime? recovered = null;
        foreach (var k in new[] { "recoverytime", "recovery_time", "recoveryTime", "endtime", "endTime" })
            if (item.TryGetProperty(k, out var rv) && (recovered = ToVendorUtc(rv, dayFirst)) != null) break;

        // Nothing to call it, or no idea when it happened: not storable. The
        // old code stored these anyway, stamped "now", once an hour.
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(desc))
            return null;
        if (happened == null) return null;

        // Recovered per the vendor even when it gives no recovery time:
        // a status word ("Recovered", "恢复", "cleared") or a boolean flag.
        if (recovered == null)
        {
            var status = FirstString(item, "status", "warningstatus", "warning_status", "statusName", "status_name");
            if (status != null && (status.Contains("recover", StringComparison.OrdinalIgnoreCase)
                                   || status.Contains("clear", StringComparison.OrdinalIgnoreCase)
                                   || status.Contains("恢复")))
                recovered = happened;
            foreach (var flag in new[] { "isRecovery", "is_recovery", "isrecovery", "recovered" })
                if (item.TryGetProperty(flag, out var fv)
                    && (fv.ValueKind == JsonValueKind.True || (ToInt(fv) ?? 0) == 1))
                    recovered = happened;
        }

        var levelText = FirstString(item, "warninglevel", "warning_level", "warningLevel", "level");
        int severity = levelText?.Trim().ToLowerInvariant() switch
        {
            null or "" => 3,
            "fault" or "error" => 2,
            "alarm" or "warning" => 3,
            "notice" or "info" => 4,
            var t when int.TryParse(t, out var n) && n is >= 1 and <= 4 => n,
            _ => 3
        };

        // Stable identity: the vendor id when there is one, otherwise what
        // makes the alarm itself unique. Never a random Guid.
        var id = FirstString(item, "warningid", "warning_id", "warningId", "id")
                 ?? $"{device}|{code ?? name}|{happened:O}";

        return new AlarmRecord
        {
            AlarmId = id,
            AlarmName = name ?? "",
            AlarmCode = code,
            Severity = severity,
            OccurredAt = happened.Value,
            ClearedAt = recovered,
            DeviceName = device,
            Description = desc
        };
    }

    private static string? FirstString(JsonElement el, params string[] props)
    {
        foreach (var p in props)
        {
            var v = GetString(el, p);
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        return null;
    }

    private static JsonElement? FirstArray(JsonElement el, params string[] props)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in props)
            if (el.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Array)
                return v;
        return null;
    }

    /// <summary>
    /// Vendor timestamp → UTC. SEMS sends either epoch milliseconds or a
    /// station-local "dd/MM/yyyy HH:mm:ss"-style string. Strings are parsed
    /// with the invariant culture: under th-TH, DateTime.TryParse reads the
    /// year as Buddhist Era. A string without an offset is site-local time.
    /// </summary>
    /// <summary>
    /// Whether this response writes dates day-first. Decided once for the
    /// whole response — per value, "05/11/2026" read as 11 May today and as
    /// 5 November next week, which changed OccurredAt and duplicated the
    /// alert. Any value with a first field over 12 means dd/MM; a second
    /// field over 12 means MM/dd; with nothing to go on, dd/MM (Thai usage).
    /// </summary>
    private static bool DetectDayFirst(JsonElement root)
    {
        bool? found = null;
        void Walk(JsonElement e)
        {
            if (found.HasValue) return;
            if (e.ValueKind == JsonValueKind.Object) foreach (var p in e.EnumerateObject()) Walk(p.Value);
            else if (e.ValueKind == JsonValueKind.Array) foreach (var x in e.EnumerateArray()) Walk(x);
            else if (e.ValueKind == JsonValueKind.String)
            {
                var m = System.Text.RegularExpressions.Regex.Match(e.GetString() ?? "", @"^(\d{1,2})/(\d{1,2})/\d{4}");
                if (!m.Success) return;
                var a = int.Parse(m.Groups[1].Value);
                var b = int.Parse(m.Groups[2].Value);
                if (a > 12) found = true;
                else if (b > 12) found = false;
            }
        }
        Walk(root);
        return found ?? true;
    }

    private static DateTime? ToVendorUtc(JsonElement v, bool dayFirst = true)
    {
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var num) && num > 0)
            return num > 100_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(num).UtcDateTime
                : DateTimeOffset.FromUnixTimeSeconds(num).UtcDateTime;
        if (v.ValueKind != JsonValueKind.String) return null;

        var s = v.GetString()?.Trim();
        if (string.IsNullOrEmpty(s) || s.StartsWith("0001") || s == "0") return null;
        var ci = System.Globalization.CultureInfo.InvariantCulture;

        if ((s.EndsWith("Z") || s.Contains('+') || System.Text.RegularExpressions.Regex.IsMatch(s, @"T.*-\d{2}:\d{2}$"))
            && DateTimeOffset.TryParse(s, ci, System.Globalization.DateTimeStyles.None, out var dto))
            return dto.UtcDateTime;

        var iso = new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy/MM/dd HH:mm:ss", "yyyy-MM-dd HH:mm" };
        if (DateTime.TryParseExact(s, iso, ci, System.Globalization.DateTimeStyles.None, out var isoLocal))
            return SolarApp.Services.SiteTime.ToUtc(isoLocal);

        // "10/09/2026" is 10 September or 9 October depending on the
        // account's language; the caller decides once per response.
        var dm = new[] { "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm", "d/M/yyyy H:mm:ss", "d/M/yyyy H:mm" };
        var md = new[] { "MM/dd/yyyy HH:mm:ss", "MM/dd/yyyy HH:mm", "M/d/yyyy H:mm:ss", "M/d/yyyy H:mm" };
        var styles = System.Globalization.DateTimeStyles.None;
        if (DateTime.TryParseExact(s, dayFirst ? dm : md, ci, styles, out var first))
            return SolarApp.Services.SiteTime.ToUtc(first);
        if (DateTime.TryParseExact(s, dayFirst ? md : dm, ci, styles, out var second))
            return SolarApp.Services.SiteTime.ToUtc(second);
        return null;
    }

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
            // SEMS writes some values with their unit — "57209(W)", "1,234.5kWh".
            var m = System.Text.RegularExpressions.Regex.Match(s?.Replace(",", "") ?? "", @"^\s*(-?\d+(?:\.\d+)?)");
            if (m.Success && decimal.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d3)) return d3;
        }
        return null;
    }

    private static decimal? FirstDecimal(JsonElement el, params string[] props)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in props)
            if (el.TryGetProperty(p, out var v) && ToDecimal(v) is { } d) return d;
        return null;
    }

    /// <summary>A powerflow value in watts; "5.2(kW)" is scaled, "5200(W)" or a bare number is taken as W.</summary>
    private static decimal? ParseWatts(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v)) return null;
        var n = ToDecimal(v);
        if (n == null) return null;
        var raw = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        return raw.Contains("kW", StringComparison.OrdinalIgnoreCase) ? n * 1000m : n;
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

    /// <summary>Mask an account/email for logs: "jo•••@•••" style, no PII leak.</summary>
    private static string MaskAccount(string? account)
    {
        if (string.IsNullOrWhiteSpace(account)) return "(none)";
        var a = account.Trim();
        return a.Length <= 2 ? "••" : a[..2] + "•••";
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
                      : ApiResultClassifier.ClassifyErrorMessage<bool>(msg, "GoodWe", sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<bool>(ex, "GoodWe", sw.Elapsed); }
    }

    public async Task<ApiResult<PlantInfo>> GetPlantInfoResultAsync(ApiCredentials credentials, CancellationToken ct = default)
    {
        BeginDiag();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var info = await GetPlantInfoAsync(credentials);
            sw.Stop();
            var diag = TakeDiag();
            return info != null
                ? ApiResult<PlantInfo>.Ok(info, "Plant info retrieved", sw.Elapsed)
                : ApiResult<PlantInfo>.Fail(
                    diag != null && diag.StartsWith("เข้าสู่ระบบ") ? ApiResultKind.AuthenticationFailed : ApiResultKind.PlantNotFound,
                    diag ?? $"Station '{credentials.PlantId}' not found on GoodWe account.",
                    detail: diag, duration: sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<PlantInfo>(ex, "GoodWe", sw.Elapsed); }
    }

    public async Task<ApiResult<RealtimeData>> GetRealtimeResultAsync(ApiCredentials credentials, CancellationToken ct = default)
    {
        BeginDiag();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var data = await GetRealtimeDataAsync(credentials);
            sw.Stop();
            if (data == null)
            {
                // Not "offline": we never got station data, so we know nothing
                // about the inverters. Say why instead.
                var why = TakeDiag() ?? "GoodWe API returned no data.";
                var kind = why.StartsWith("ไม่พบ Station ID") ? ApiResultKind.PlantNotFound
                         : why.StartsWith("เข้าสู่ระบบ") ? ApiResultKind.AuthenticationFailed
                         : ApiResultKind.CloudServerError;
                return ApiResult<RealtimeData>.Fail(kind, why, detail: why, duration: sw.Elapsed);
            }
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
        BeginDiag();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var list = await GetHistoricalDataAsync(credentials, from, to);
            sw.Stop();
            var diag = TakeDiag();
            // Even though we got zero rows, this is not "Fail" — the call
            // succeeded.  Attach diag as TechnicalDetail so the probe
            // surfaces the per-endpoint attempt trace.
            return new ApiResult<List<EnergyRecord>>
            {
                Kind = ApiResultKind.Success,
                Data = list,
                Message = $"{list.Count} record(s) fetched",
                Duration = sw.Elapsed,
                HttpStatusCode = 200,
                TechnicalDetail = diag,
            };
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<List<EnergyRecord>>(ex, "GoodWe", sw.Elapsed); }
    }

    public async Task<ApiResult<List<AlarmRecord>>> GetAlarmsResultAsync(ApiCredentials credentials, DateTime from, DateTime to, CancellationToken ct = default)
    {
        BeginDiag();
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
        BeginDiag();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var list = await GetDevicesAsync(credentials);
            sw.Stop();
            if (list.Count == 0)
            {
                var why = TakeDiag() ?? "SEMS ไม่ส่งรายการอินเวอร์เตอร์มา";
                return ApiResult<List<DeviceInfo>>.Fail(
                    why.StartsWith("ไม่พบ Station ID") ? ApiResultKind.PlantNotFound : ApiResultKind.ParseError,
                    why, detail: why, duration: sw.Elapsed);
            }
            return ApiResult<List<DeviceInfo>>.Ok(list, $"{list.Count} device(s) fetched", sw.Elapsed);
        }
        catch (Exception ex) { sw.Stop(); return ApiResultClassifier.ClassifyException<List<DeviceInfo>>(ex, "GoodWe", sw.Elapsed); }
    }
}
