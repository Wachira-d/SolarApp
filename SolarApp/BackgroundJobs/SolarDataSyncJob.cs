using Hangfire;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Hubs;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.InverterApi;
using SolarApp.Services.InverterApi.Adapters;
using SolarApp.Services.InverterApi.Models;
using SolarApp.Services.Security;

namespace SolarApp.BackgroundJobs;

public class SolarDataSyncJob
{
    private readonly ApplicationDbContext _context;
    private readonly IInverterApiAdapterFactory _adapterFactory;
    private readonly ILogger<SolarDataSyncJob> _logger;
    private readonly IHubContext<DashboardHub> _hub;
    private readonly ICredentialProtector _credProtector;

    public SolarDataSyncJob(
        ApplicationDbContext context,
        IInverterApiAdapterFactory adapterFactory,
        ILogger<SolarDataSyncJob> logger,
        IHubContext<DashboardHub> hub,
        ICredentialProtector credProtector)
    {
        _context = context;
        _adapterFactory = adapterFactory;
        _logger = logger;
        _hub = hub;
        _credProtector = credProtector;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 1800)]
    public async Task ExecuteAsync()
    {
        // Respect per-site service scope AND per-site sync interval.
        //   * IncludesMonitoring=false → partner-monitored site, skip entirely.
        //   * NextSyncDueAt in the future → site is on a longer cadence than
        //     this Hangfire run; skip until its due time arrives.
        var now = DateTime.UtcNow;
        var installations = await _context.Installations
            .Where(i => i.Status != InstallationStatus.Inactive
                        && i.IncludesMonitoring
                        && i.PlantId != null && i.PlantId != ""
                        && (i.NextSyncDueAt == null || i.NextSyncDueAt <= now))
            .ToListAsync();

        _logger.LogInformation("Solar data sync: starting for {Count} installations", installations.Count);

        int totalRealtime = 0, totalDaily = 0, totalAlarms = 0;
        int totalApiFailures = 0, totalDeviceOffline = 0;
        int totalRateLimitDeferred = 0;

        // ──────────────────────────────────────────────────────────────────
        // Huawei batch: multiple installations sharing the same Northbound
        // account can be fetched in a SINGLE getStationRealKpi call by
        // passing comma-separated stationCodes.  This eliminates the
        // "1 account × 3 stations → 2 always deferred" problem entirely.
        // ──────────────────────────────────────────────────────────────────
        var huaweiInstallations = installations
            .Where(i => i.InverterBrand == InverterBrand.Huawei)
            .ToList();

        // Per-installation batch info: classified outcome + data (data may be null
        // even on Success if Huawei didn't return that station).
        var huaweiBatch = new Dictionary<int, (HuaweiAdapter.BatchOutcome Outcome, RealtimeData? Data, string? Error)>();

        if (huaweiInstallations.Count > 0)
        {
            // Group by (decrypted username + decrypted password fingerprint) so two
            // installations with the same username but different passwords don't
            // share a batch call. Drop installations with empty username — they
            // can't authenticate anyway and would all collide under "(none)".
            var validHuawei = new List<(Installation inst, string username, string? password)>();
            foreach (var inst in huaweiInstallations)
            {
                var u = _credProtector.Unprotect(inst.CloudUsername)?.Trim();
                if (string.IsNullOrEmpty(u))
                {
                    huaweiBatch[inst.Id] = (HuaweiAdapter.BatchOutcome.AuthFailed, null, "Cloud username is empty");
                    continue;
                }
                validHuawei.Add((inst, u.ToLowerInvariant(), _credProtector.Unprotect(inst.CloudPassword)));
            }

            // Group by (username + password length) so same-username/different-password
            // tenants don't collide. Length is a stable proxy without revealing secrets.
            var groups = validHuawei
                .GroupBy(x => $"{x.username}|{x.password?.Length ?? 0}")
                .ToList();

            foreach (var group in groups)
            {
                try
                {
                    var first = group.First();
                    var credentials = new ApiCredentials
                    {
                        Username = _credProtector.Unprotect(first.inst.CloudUsername),
                        Password = _credProtector.Unprotect(first.inst.CloudPassword),
                        ApiKey = _credProtector.Unprotect(first.inst.CloudApiKey),
                        ApiSecret = _credProtector.Unprotect(first.inst.CloudApiSecret),
                        ApiBaseUrl = first.inst.CloudApiBaseUrl,
                    };
                    var plantIds = group.Select(x => x.inst.PlantId!).ToList();

                    var adapter = (HuaweiAdapter)_adapterFactory.GetAdapter(InverterBrand.Huawei);
                    var batchResult = await adapter.GetBatchRealtimeDataAsync(credentials, plantIds);

                    foreach (var x in group)
                    {
                        var data = batchResult.Stations.TryGetValue(x.inst.PlantId!, out var d) ? d : null;
                        huaweiBatch[x.inst.Id] = (batchResult.Outcome, data, batchResult.ErrorDetail);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Huawei batch sync threw for account group ({Count} stations)", group.Count());
                    foreach (var x in group)
                        huaweiBatch[x.inst.Id] = (HuaweiAdapter.BatchOutcome.ServerError, null, ex.Message);
                }
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Huawei batch alarms: same idea — fetch alarms for ALL stations on
        // each account in 1 /getAlarmList call. Only run if the per-installation
        // hourly cooldown allows. Skipped entries fall through to per-installation
        // GetAlarmsAsync (which is gated and likely deferred).
        // ──────────────────────────────────────────────────────────────────
        var huaweiBatchAlarms = new Dictionary<int, List<AlarmRecord>>();
        var huaweiAlarmsHandled = new HashSet<int>();
        if (huaweiInstallations.Count > 0)
        {
            // Find which Huawei installations are eligible for alarm fetch this cycle
            var alarmCutoff = DateTime.UtcNow.AddMinutes(-55);
            var lastAlarmTimes = await _context.InverterApiLogs
                .Where(l => huaweiInstallations.Select(i => i.Id).Contains(l.InstallationId)
                            && l.Endpoint == "GetAlarms"
                            && l.IsSuccess)
                .GroupBy(l => l.InstallationId)
                .Select(g => new { InstallationId = g.Key, Last = g.Max(l => l.RequestTime) })
                .ToListAsync();
            var lastByInst = lastAlarmTimes.ToDictionary(x => x.InstallationId, x => x.Last);

            // Group eligible installations by account (re-use the same key as realtime batch)
            var eligibleByGroup = huaweiInstallations
                .Where(i => !lastByInst.TryGetValue(i.Id, out var t) || t < alarmCutoff)
                .Where(i => !string.IsNullOrEmpty(_credProtector.Unprotect(i.CloudUsername)?.Trim()))
                .GroupBy(i =>
                {
                    var u = _credProtector.Unprotect(i.CloudUsername)!.Trim().ToLowerInvariant();
                    var p = _credProtector.Unprotect(i.CloudPassword);
                    return $"{u}|{p?.Length ?? 0}";
                })
                .ToList();

            foreach (var group in eligibleByGroup)
            {
                try
                {
                    var first = group.First();
                    var credentials = new ApiCredentials
                    {
                        Username = _credProtector.Unprotect(first.CloudUsername),
                        Password = _credProtector.Unprotect(first.CloudPassword),
                        ApiKey = _credProtector.Unprotect(first.CloudApiKey),
                        ApiSecret = _credProtector.Unprotect(first.CloudApiSecret),
                        ApiBaseUrl = first.CloudApiBaseUrl,
                    };
                    var plantIds = group.Select(i => i.PlantId!).ToList();

                    var hAdapter = (HuaweiAdapter)_adapterFactory.GetAdapter(InverterBrand.Huawei);
                    var batchAlarms = await hAdapter.GetBatchAlarmsAsync(
                        credentials, plantIds,
                        DateTime.UtcNow.AddHours(-24), DateTime.UtcNow);

                    foreach (var inst in group)
                    {
                        huaweiAlarmsHandled.Add(inst.Id);
                        huaweiBatchAlarms[inst.Id] = batchAlarms.TryGetValue(inst.PlantId!, out var list)
                            ? list : new List<AlarmRecord>();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Huawei batch alarms failed for account group ({Count} stations)", group.Count());
                }
            }
        }

        int processedSinceLastSave = 0;
        foreach (var installation in installations)
        {
            try
            {
                var adapter = _adapterFactory.GetAdapter(installation.InverterBrand);
                var credentials = new ApiCredentials
                {
                    ApiKey = _credProtector.Unprotect(installation.CloudApiKey),
                    ApiSecret = _credProtector.Unprotect(installation.CloudApiSecret),
                    Username = _credProtector.Unprotect(installation.CloudUsername),
                    Password = _credProtector.Unprotect(installation.CloudPassword),
                    PlantId = installation.PlantId,
                    ApiBaseUrl = installation.CloudApiBaseUrl
                };

                // ─── 1. Realtime snapshot ───
                // Huawei: use pre-fetched batch result (1 API call for ALL stations
                // on the same account), avoiding the 1 req/min bottleneck entirely.
                // Other brands: per-installation call as before.
                ApiResult<RealtimeData> realtimeResult;
                if (installation.InverterBrand == InverterBrand.Huawei
                    && huaweiBatch.TryGetValue(installation.Id, out var batch))
                {
                    switch (batch.Outcome)
                    {
                        case HuaweiAdapter.BatchOutcome.Success when batch.Data != null:
                            realtimeResult = batch.Data.IsOnline
                                ? ApiResult<RealtimeData>.Ok(batch.Data, duration: TimeSpan.Zero)
                                : ApiResult<RealtimeData>.Offline(batch.Data, "Device offline (batch)", duration: TimeSpan.Zero);
                            break;

                        case HuaweiAdapter.BatchOutcome.Success:
                            // Call succeeded but Huawei didn't include this station in the
                            // response. Treat as device offline rather than a sync failure —
                            // cloud is reachable, just no data for this station.
                            installation.LastOnlineAt = DateTime.UtcNow;
                            if (installation.Status == InstallationStatus.Offline)
                                installation.Status = InstallationStatus.Active;
                            totalDeviceOffline++;
                            LogApiCall(installation, "GetRealtimeData", 200, true,
                                "[HUAWEI BATCH] No row for this stationCode in batch response. " +
                                "Cloud is healthy; station may be offline or PlantId mis-configured.");
                            continue;

                        case HuaweiAdapter.BatchOutcome.Deferred:
                        case HuaweiAdapter.BatchOutcome.RateLimitedByServer:
                            totalRateLimitDeferred++;
                            installation.LastOnlineAt = DateTime.UtcNow;
                            if (installation.Status == InstallationStatus.Offline)
                                installation.Status = InstallationStatus.Active;
                            LogApiCall(installation, "GetRealtimeData", 0, true,
                                $"[RATE LIMIT-DEFERRED] {batch.Error}. Will retry next cycle.");
                            continue;

                        case HuaweiAdapter.BatchOutcome.AuthFailed:
                            totalApiFailures++;
                            LogApiCall(installation, "GetRealtimeData", 401, false,
                                $"[AUTH FAILED] Huawei batch login rejected: {batch.Error}. " +
                                "Re-enter Northbound API credentials in Installation settings.");
                            continue;

                        case HuaweiAdapter.BatchOutcome.ServerError:
                        default:
                            totalApiFailures++;
                            LogApiCall(installation, "GetRealtimeData", 0, false,
                                $"[API FAILURE] Huawei batch error: {batch.Error}");
                            continue;
                    }
                }
                else
                {
                    realtimeResult = await adapter.GetRealtimeResultAsync(credentials);
                }

                if (realtimeResult.IsApiFailure)
                {
                    // ─── Rate-limit deferral is NOT an outage ───
                    // Huawei's 1 req/min ceiling is something WE manage via HuaweiRateGate.
                    // When the gate defers a call, the cloud is fine — we just chose
                    // not to call it this cycle. Don't spam alerts, don't mark offline.
                    if (realtimeResult.Kind == Services.InverterApi.Models.ApiResultKind.RateLimited)
                    {
                        totalRateLimitDeferred++;
                        installation.LastOnlineAt = DateTime.UtcNow; // cloud reachable; we deferred
                        if (installation.Status == InstallationStatus.Offline)
                            installation.Status = InstallationStatus.Active;

                        LogApiCall(installation, "GetRealtimeData",
                            realtimeResult.HttpStatusCode ?? 0, true,
                            $"[RATE LIMIT-DEFERRED] {realtimeResult.Message}. " +
                            "Skipped this cycle to respect Huawei NBI 1-req/min budget. Will retry next cycle.");
                        continue;
                    }

                    totalApiFailures++;
                    var kindTag = realtimeResult.Kind switch
                    {
                        Services.InverterApi.Models.ApiResultKind.Timeout => "[TIMEOUT]",
                        Services.InverterApi.Models.ApiResultKind.NetworkError => "[NETWORK ERROR]",
                        Services.InverterApi.Models.ApiResultKind.AuthenticationFailed => "[AUTH FAILED]",
                        Services.InverterApi.Models.ApiResultKind.CloudServerError => "[CLOUD 5xx]",
                        Services.InverterApi.Models.ApiResultKind.ParseError => "[PARSE ERROR]",
                        Services.InverterApi.Models.ApiResultKind.PlantNotFound => "[PLANT NOT FOUND]",
                        _ => "[API FAILURE]"
                    };
                    LogApiCall(installation, "GetRealtimeData",
                        realtimeResult.HttpStatusCode ?? 0, false,
                        $"{kindTag} {realtimeResult.Message}" +
                        (realtimeResult.TechnicalDetail != null ? $" | {realtimeResult.TechnicalDetail}" : "") +
                        $" | Duration: {realtimeResult.Duration.TotalMilliseconds:0}ms");
                    continue;
                }

                var realtime = realtimeResult.Data!;
                var isDeviceOnline = realtimeResult.Kind == Services.InverterApi.Models.ApiResultKind.Success;

                _context.EnergyData.Add(new EnergyData
                {
                    InstallationId = installation.Id,
                    Timestamp = DateTime.UtcNow,
                    Granularity = DataGranularity.Realtime,
                    PowerGenerationKw = realtime.CurrentPowerKw,
                    EnergyGenerationKwh = realtime.TodayEnergyKwh,
                    GridExportKwh = realtime.GridExportKw ?? 0,
                    GridImportKwh = realtime.GridImportKw ?? 0,
                    SelfConsumptionKwh = Math.Max(0, realtime.TodayEnergyKwh - (realtime.GridExportKw ?? 0)),
                    BatterySocPercent = realtime.BatterySocPercent
                });
                totalRealtime++;

                // IMPORTANT: LastOnlineAt = "last time we successfully contacted the cloud",
                // NOT "last time device was producing". The API call succeeded here (kind
                // is either Success or DeviceOffline), so update the timestamp either way.
                // Otherwise nighttime/standby inverters would falsely get Status=Offline
                // just because their cloud reports status=0 briefly.
                installation.LastOnlineAt = DateTime.UtcNow;
                if (installation.Status == InstallationStatus.Offline)
                    installation.Status = InstallationStatus.Active;

                if (realtime.Health == Services.InverterApi.Models.HealthState.Faulty)
                {
                    // Station is in FAULT state (Grid Loss / OverTemp / Insulation / etc.)
                    // Cloud is reachable, but the inverter is reporting a hard fault.
                    // Surface this as a Critical alert so LINE notifies + dashboard turns red.
                    installation.Status = InstallationStatus.Maintenance;                    LogApiCall(installation, "GetRealtimeData", 200, true,
                        $"[FAULTY] {installation.InverterBrand} reports station faulty. " +
                        $"Reason: {realtime.FaultReason ?? "unknown"}. " +
                        $"Today={realtime.TodayEnergyKwh:0.0}kWh");

                    // De-dup: only one open Fault alert per installation per day
                    var faultTitle = $"Station Faulty: {installation.SiteName}";
                    var faultDedupSince = DateTime.UtcNow.Date;
                    var existingFault = await _context.SystemAlerts.AnyAsync(a =>
                        a.InstallationId == installation.Id
                        && a.AlertType == AlertType.Error
                        && a.ResolvedAt == null
                        && a.Title == faultTitle
                        && a.OccurredAt >= faultDedupSince);

                    if (!existingFault)
                    {
                        // Pull the most recent OPEN per-alarm records so the
                        // generic Station Faulty alert references the actual
                        // Huawei alarm(s) — not our placeholder text.  Anything
                        // ingested by GetAlarmsAsync in a previous sync round
                        // is in SystemAlerts already.
                        var since = DateTime.UtcNow.AddHours(-24);
                        var openAlarmTitles = await _context.SystemAlerts
                            .Where(a => a.InstallationId == installation.Id
                                && a.AlertType == AlertType.Error
                                && a.ResolvedAt == null
                                && a.OccurredAt >= since
                                && a.Title != faultTitle)         // exclude the previous-day Station Faulty itself
                            .OrderByDescending(a => a.OccurredAt)
                            .Take(5)
                            .Select(a => a.Title)
                            .ToListAsync();

                        var alarmSummary = openAlarmTitles.Count > 0
                            ? "\n\nรหัส alarm ที่ Huawei รายงานล่าสุด:\n• " + string.Join("\n• ", openAlarmTitles)
                            : "\n\n(ยังไม่มีรหัส alarm จาก getAlarmList — รอ sync รอบถัดไป หรือเปิด FusionSolar เพื่อดู)";

                        _context.SystemAlerts.Add(new SystemAlert
                        {
                            InstallationId = installation.Id,
                            AlertType = AlertType.Error,
                            Severity = AlertSeverity.Critical,
                            Title = faultTitle,
                            // No "(Grid Loss / Insulation / OverTemp / ฯลฯ)" enumeration here —
                            // it was getting matched by InverterKnowledgeBase.MatchFaultCode
                            // as evidence of those faults, producing fake "Huawei 2063 DC
                            // insulation" diagnostics on a site that actually had a network
                            // disconnection alarm.  Only the real alarm titles from
                            // getAlarmList belong in the visible message.
                            Message = $"[{installation.InverterBrand}] {installation.SiteName} รายงานสถานะ FAULTY ผ่าน real_health_state=1. " +
                                "กรุณาเปิดแอป FusionSolar เพื่อดูรหัส alarm จริงและเข้าตรวจสอบหน้างาน" +
                                alarmSummary,
                            RootCauseAnalysis = "⚠ STATION-LEVEL FAULT detected via Huawei NBI real_health_state=1.\n" +
                                "Cloud API ปกติ แต่ inverter รายงานว่ามีปัญหา " +
                                "(สาเหตุที่แท้จริงให้ดูจากรหัส alarm ที่ Huawei รายงานข้างต้น — อย่าเดาจากรายการตัวอย่าง).\n" +
                                "การตรวจสอบเบื้องต้น:\n" +
                                "  1. เปิดแอป FusionSolar → ดูรายการ Alarm ของไซต์นี้เพื่อทราบสาเหตุที่แท้จริง\n" +
                                "  2. ตรวจ AC breaker / DC isolator\n" +
                                "  3. ตรวจหน้าจอ inverter หาเลข error",
                            OccurredAt = DateTime.UtcNow,
                            IsNotified = false
                        });
                    }
                }
                else if (isDeviceOnline)
                {
                    if (installation.Status == InstallationStatus.Maintenance)
                        installation.Status = InstallationStatus.Active; // recovered from fault
                    LogApiCall(installation, "GetRealtimeData", 200, true,
                        $"[ONLINE] Power={realtime.CurrentPowerKw:0.00}kW, Today={realtime.TodayEnergyKwh:0.0}kWh");

                    // Auto-resolve any open station-fault alerts for this installation
                    var openFaults = await _context.SystemAlerts
                        .Where(a => a.InstallationId == installation.Id
                            && a.AlertType == AlertType.Error
                            && a.ResolvedAt == null
                            && a.Title.StartsWith("Station Faulty:"))
                        .ToListAsync();
                    foreach (var f in openFaults)
                    {
                        f.ResolvedAt = DateTime.UtcNow;
                    }
                }
                else
                {
                    // Cloud reachable, inverter sleeping/offline but NOT faulty.
                    // If we were stuck in Maintenance from a previous transient fault
                    // (e.g. brief Grid Loss that cleared itself), recover now.
                    // Before this fix: recovery required isDeviceOnline=true (health=2),
                    // so a night-time sync after a cleared fault would stay Maintenance
                    // indefinitely until the next daytime sync returned health=2.
                    if (installation.Status == InstallationStatus.Maintenance
                        && realtime.Health != Services.InverterApi.Models.HealthState.Faulty)
                    {
                        installation.Status = InstallationStatus.Active;
                        LogApiCall(installation, "GetRealtimeData", 200, true,
                            $"[RECOVERED] Station no longer faulty (health={realtime.Health}). " +
                            "Status changed from Maintenance → Active.");

                        var openFaults = await _context.SystemAlerts
                            .Where(a => a.InstallationId == installation.Id
                                && a.AlertType == AlertType.Error
                                && a.ResolvedAt == null
                                && a.Title.StartsWith("Station Faulty:"))
                            .ToListAsync();
                        foreach (var f in openFaults)
                            f.ResolvedAt = DateTime.UtcNow;
                    }

                    totalDeviceOffline++;
                    LogApiCall(installation, "GetRealtimeData", 200, true,
                        $"[DEVICE OFFLINE] API connected OK, but inverter reports offline/sleeping. " +
                        $"Power={realtime.CurrentPowerKw:0.00}kW. " +
                        "This is normal at night / early morning. LastOnlineAt still updated because cloud is reachable.");
                }

                // Push real-time update to dashboard
                try
                {
                    var payload = new
                    {
                        installation.Id, installation.SiteName,
                        PowerKw = realtime.CurrentPowerKw,
                        TodayKwh = realtime.TodayEnergyKwh,
                        BatterySoc = realtime.BatterySocPercent,
                        Timestamp = DateTime.UtcNow,
                        realtime.IsOnline
                    };
                    await _hub.Clients.Group("all").SendAsync("RealtimeUpdate", payload);
                    if (installation.PartnerId.HasValue)
                        await _hub.Clients.Group($"partner-{installation.PartnerId}").SendAsync("RealtimeUpdate", payload);
                    await _hub.Clients.Group($"customer-{installation.CustomerId}").SendAsync("RealtimeUpdate", payload);
                }
                catch (Exception hubEx)
                {
                    _logger.LogDebug(hubEx, "SignalR broadcast failed (non-critical, sync continues)");
                }

                // ─── 2. Daily aggregate (upsert today) ───
                var today = DateTime.UtcNow.Date;
                var todayRecord = await _context.EnergyData.FirstOrDefaultAsync(e =>
                    e.InstallationId == installation.Id
                    && e.Granularity == DataGranularity.Daily
                    && e.Timestamp.Date == today);

                if (todayRecord == null)
                {
                    _context.EnergyData.Add(new EnergyData
                    {
                        InstallationId = installation.Id,
                        Timestamp = today,
                        Granularity = DataGranularity.Daily,
                        EnergyGenerationKwh = realtime.TodayEnergyKwh,
                        GridExportKwh = realtime.GridExportKw ?? 0,
                        GridImportKwh = realtime.GridImportKw ?? 0,
                        SelfConsumptionKwh = Math.Max(0, realtime.TodayEnergyKwh - (realtime.GridExportKw ?? 0)),
                        BatterySocPercent = realtime.BatterySocPercent
                    });
                    totalDaily++;
                }
                else
                {
                    todayRecord.EnergyGenerationKwh = realtime.TodayEnergyKwh;
                    if (realtime.GridExportKw.HasValue) todayRecord.GridExportKwh = realtime.GridExportKw.Value;
                    if (realtime.GridImportKw.HasValue) todayRecord.GridImportKwh = realtime.GridImportKw.Value;
                    todayRecord.SelfConsumptionKwh = Math.Max(0, realtime.TodayEnergyKwh - todayRecord.GridExportKwh);
                    todayRecord.BatterySocPercent = realtime.BatterySocPercent;
                }

                // ─── 3. Historical backfill (only once per day) ───
                var hasYesterday = await _context.EnergyData.AnyAsync(e =>
                    e.InstallationId == installation.Id
                    && e.Granularity == DataGranularity.Daily
                    && e.Timestamp.Date == today.AddDays(-1));

                if (!hasYesterday)
                {
                    var historical = await adapter.GetHistoricalDataAsync(credentials,
                        today.AddDays(-7), today.AddDays(-1));

                    if (historical.Any())
                    {
                        // Batch-load existing dates in one query (eliminates N+1)
                        var fromDate = historical.Min(h => h.Timestamp.Date);
                        var toDate = historical.Max(h => h.Timestamp.Date);
                        var existingDates = await _context.EnergyData
                            .Where(e => e.InstallationId == installation.Id
                                        && e.Granularity == DataGranularity.Daily
                                        && e.Timestamp >= fromDate
                                        && e.Timestamp <= toDate)
                            .Select(e => e.Timestamp.Date)
                            .ToListAsync();
                        var existingSet = new HashSet<DateTime>(existingDates);

                        foreach (var h in historical)
                        {
                            if (existingSet.Contains(h.Timestamp.Date)) continue;
                            _context.EnergyData.Add(new EnergyData
                            {
                                InstallationId = installation.Id,
                                Timestamp = h.Timestamp.Date,
                                Granularity = DataGranularity.Daily,
                                EnergyGenerationKwh = h.EnergyGenerationKwh,
                                GridExportKwh = h.GridExportKwh,
                                GridImportKwh = h.GridImportKwh,
                                SelfConsumptionKwh = h.SelfConsumptionKwh,
                                BatteryChargeKwh = h.BatteryChargeKwh,
                                BatteryDischargeKwh = h.BatteryDischargeKwh
                            });
                            existingSet.Add(h.Timestamp.Date);
                            totalDaily++;
                        }
                    }
                }

                // ─── 4. Alarms (last 24 hours) — Huawei uses pre-fetched batch
                // (1 /getAlarmList call per account). Other brands fetch per-installation
                // at most hourly to keep within their per-account budget. ───
                List<AlarmRecord>? alarmsForThisInstallation = null;

                if (installation.InverterBrand == InverterBrand.Huawei
                    && huaweiAlarmsHandled.Contains(installation.Id))
                {
                    // Already fetched as part of the batch
                    alarmsForThisInstallation = huaweiBatchAlarms.TryGetValue(installation.Id, out var list)
                        ? list : new List<AlarmRecord>();
                    LogApiCall(installation, "GetAlarms", 200, true,
                        $"[ALARMS-BATCH] {alarmsForThisInstallation.Count} alarm(s) from batch fetch.");
                }
                else
                {
                    var lastAlarmFetch = await _context.InverterApiLogs
                        .Where(l => l.InstallationId == installation.Id
                                    && l.Endpoint == "GetAlarms"
                                    && l.IsSuccess)
                        .OrderByDescending(l => l.RequestTime)
                        .Select(l => (DateTime?)l.RequestTime)
                        .FirstOrDefaultAsync();
                    var skipAlarms = lastAlarmFetch.HasValue
                        && DateTime.UtcNow - lastAlarmFetch.Value < TimeSpan.FromMinutes(55);

                    if (!skipAlarms)
                    {
                        try
                        {
                            alarmsForThisInstallation = await adapter.GetAlarmsAsync(credentials,
                                DateTime.UtcNow.AddHours(-24), DateTime.UtcNow);
                            LogApiCall(installation, "GetAlarms", 200, true,
                                $"[ALARMS] Fetched {alarmsForThisInstallation.Count} alarm(s).");
                        }
                        catch (Exception alarmEx)
                        {
                            _logger.LogWarning(alarmEx, "Failed to fetch alarms for installation {Id}", installation.Id);
                        }
                    }
                }

                if (alarmsForThisInstallation != null)
                {
                    try
                    {
                        // Batch dedup: pull all existing (title, occurredAt) tuples for
                        // this installation in ONE query, then check in-memory.
                        // Before: N AnyAsync calls per installation (100 alarms = 100 round-trips).
                        // After:  1 query + N HashSet lookups.
                        var alarmKeys = alarmsForThisInstallation
                            .Select(a => new { a.AlarmName, a.OccurredAt })
                            .ToList();
                        var titles = alarmKeys.Select(k => k.AlarmName).Distinct().ToList();
                        var earliest = alarmKeys.Min(k => k.OccurredAt);

                        var existingKeys = await _context.SystemAlerts
                            .Where(x => x.InstallationId == installation.Id
                                && x.OccurredAt >= earliest
                                && titles.Contains(x.Title))
                            .Select(x => new { x.Title, x.OccurredAt })
                            .ToListAsync();

                        var existingSet = new HashSet<(string, DateTime)>(
                            existingKeys.Select(k => (k.Title, k.OccurredAt)));

                        foreach (var a in alarmsForThisInstallation)
                        {
                            if (existingSet.Contains((a.AlarmName, a.OccurredAt))) continue;

                            _context.SystemAlerts.Add(new SystemAlert
                            {
                                InstallationId = installation.Id,
                                AlertType = AlertType.Error,
                                Severity = a.Severity switch
                                {
                                    1 => AlertSeverity.Critical,
                                    2 => AlertSeverity.High,
                                    3 => AlertSeverity.Medium,
                                    _ => AlertSeverity.Low
                                },
                                Title = a.AlarmName,
                                Message = $"{a.Description ?? a.AlarmName} (Device: {a.DeviceName ?? "N/A"})",
                                OccurredAt = a.OccurredAt,
                                ResolvedAt = a.ClearedAt,
                                IsNotified = false
                            });
                            // Prevent dup-add within same batch (same alarm appearing twice in source)
                            existingSet.Add((a.AlarmName, a.OccurredAt));
                            totalAlarms++;
                        }
                    }
                    catch (Exception alarmEx)
                    {
                        // Alarms are non-critical, don't fail the whole sync
                        _logger.LogWarning(alarmEx, "Failed to fetch alarms for installation {Id}", installation.Id);
                    }
                }

            }
            catch (TaskCanceledException)
            {
                totalApiFailures++;
                _logger.LogWarning("Sync timeout for {Id} ({Brand})", installation.Id, installation.InverterBrand);
                LogApiCall(installation, "Sync", 0, false,
                    $"[TIMEOUT] Sync timed out for {installation.SiteName}. " +
                    $"The {installation.InverterBrand} cloud server is too slow or unreachable. " +
                    "This is an API CONNECTION issue, NOT an inverter offline issue.");
            }
            catch (HttpRequestException hre)
            {
                totalApiFailures++;
                _logger.LogWarning(hre, "Network error syncing {Id} ({Brand})", installation.Id, installation.InverterBrand);
                LogApiCall(installation, "Sync", 0, false,
                    $"[NETWORK ERROR] Cannot reach {installation.InverterBrand} cloud: {hre.Message}. " +
                    "Check SolarApp server's internet connection and DNS. " +
                    "This is an API CONNECTION issue, NOT an inverter offline issue.");
            }
            catch (Exception ex)
            {
                totalApiFailures++;
                _logger.LogError(ex, "Sync failed for {Id} ({Brand})", installation.Id, installation.InverterBrand);
                LogApiCall(installation, "Sync", 500, false,
                    $"[UNEXPECTED ERROR] {ex.GetType().Name}: {ex.Message}");
            }

            // Schedule next sync per-installation cadence. The Hangfire job
            // runs every 5 min globally; this gate keeps us from re-syncing
            // a 1-hour-interval site until its hour is up.
            var interval = installation.SyncIntervalMinutes <= 0 ? 15 : installation.SyncIntervalMinutes;
            installation.NextSyncDueAt = DateTime.UtcNow.AddMinutes(interval);

            // Batch save every 10 installations to reduce DB round-trips
            // (was: SaveChangesAsync per installation = 100+ round-trips per sync)
            processedSinceLastSave++;
            if (processedSinceLastSave >= 10)
            {
                await _context.SaveChangesAsync();
                processedSinceLastSave = 0;
            }
        }

        // Final save for remaining items
        if (processedSinceLastSave > 0)
            await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Sync complete: {Total} installations | {Realtime} realtime OK | {Daily} daily records | {Alarms} alarms | " +
            "{ApiFailures} API failures | {DeviceOffline} device-offline | {RateLimited} rate-limit-deferred",
            installations.Count, totalRealtime, totalDaily, totalAlarms,
            totalApiFailures, totalDeviceOffline, totalRateLimitDeferred);
    }

    private void LogApiCall(Installation installation, string endpoint, int statusCode, bool success, string? error = null)
    {
        _context.InverterApiLogs.Add(new InverterApiLog
        {
            InstallationId = installation.Id,
            ApiProvider = installation.InverterBrand.ToString(),
            Endpoint = endpoint,
            ResponseStatusCode = statusCode,
            IsSuccess = success,
            ErrorMessage = error,
            RequestTime = DateTime.UtcNow
        });
    }
}
