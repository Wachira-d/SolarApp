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
        // The inverter resets its TodayEnergyKwh counter at LOCAL midnight,
        // not UTC midnight.  For a Thailand deployment (UTC+7) this means
        // from 00:00-07:00 local Thai time, the inverter has already started
        // a new day while DateTime.UtcNow.Date still points at the previous
        // day.  If we keyed daily rows by UtcNow.Date, the post-midnight
        // sync would overwrite YESTERDAY's row with the new day's tiny
        // accumulator (~0 kWh) — clobbering the real end-of-day value.
        // SolarApp.Services.SiteTime.Today handles this centrally.
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
        // Concurrent because the account groups below are fetched in parallel.
        var huaweiBatch = new System.Collections.Concurrent.ConcurrentDictionary<
            int, (HuaweiAdapter.BatchOutcome Outcome, RealtimeData? Data, string? Error)>();

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

            // Accounts in parallel. HuaweiRateGate is keyed per account, so two
            // different Huawei accounts never shared a rate budget — but this
            // loop made the second wait for the first anyway, and Huawei's
            // 1-req/min gate means that wait was measured in minutes. Nothing
            // here touches the DbContext (credentials decrypt in memory, the
            // adapter does HTTP, results land in a dictionary), which is the
            // only reason it's safe to run concurrently: EF's context is not
            // thread-safe and the per-site loop below still runs in order.
            var realtimeTasks = groups.Select(async group =>
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
            });

            await Task.WhenAll(realtimeTasks);
        }

        // ──────────────────────────────────────────────────────────────────
        // Huawei batch alarms: same idea — fetch alarms for ALL stations on
        // each account in 1 /getAlarmList call. Only run if the per-installation
        // hourly cooldown allows. Skipped entries fall through to per-installation
        // GetAlarmsAsync (which is gated and likely deferred).
        // ──────────────────────────────────────────────────────────────────
        // Concurrent for the same reason as huaweiBatch — the account groups
        // below are fetched in parallel. Presence of a key IS "handled": the
        // dictionary is only written for stations the batch call covered.
        var huaweiBatchAlarms = new System.Collections.Concurrent.ConcurrentDictionary<int, List<AlarmRecord>>();
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

            var alarmTasks = eligibleByGroup.Select(async group =>
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
                        huaweiBatchAlarms[inst.Id] = batchAlarms.TryGetValue(inst.PlantId!, out var list)
                            ? list : new List<AlarmRecord>();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Huawei batch alarms failed for account group ({Count} stations)", group.Count());
                }
            });

            await Task.WhenAll(alarmTasks);
        }

        int processedSinceLastSave = 0;
        foreach (var installation in installations)
        {
            // ── Circuit breaker ──
            // Skip a site that has failed to sync many times in a row, for a
            // cooldown, instead of hammering a hopeless endpoint every round.
            // Huawei sites already resolved via the batch above are exempt
            // (their outcome is handled from the batch result, no per-site
            // call to gate).
            if (Services.InverterApi.Common.SyncCircuitBreaker.IsOpen(installation.Id))
            {
                var openUntil = Services.InverterApi.Common.SyncCircuitBreaker.OpenUntil(installation.Id);
                totalApiFailures++;
                LogApiCall(installation, "Sync", 0, false,
                    $"[CIRCUIT OPEN] Skipping {installation.SiteName} — {installation.InverterBrand} sync has failed " +
                    $"repeatedly; paused until {openUntil:HH:mm} UTC to stop wasting the vendor's rate budget. " +
                    "Fix credentials / connectivity, then it resumes automatically.");
                // Retry a bit after the breaker's cooldown, not on the normal cadence.
                installation.NextSyncDueAt = openUntil ?? DateTime.UtcNow.AddMinutes(30);
                processedSinceLastSave++;
                if (processedSinceLastSave >= 10)
                {
                    await _context.SaveChangesAsync();
                    processedSinceLastSave = 0;
                }
                continue;
            }

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
                    // Huawei NBI reports real_health_state=1 ("abnormal"). This
                    // bucket actually covers several distinct conditions —
                    // inverter hardware fault, grid issue, network/data-logger
                    // disconnect, cloud reporting glitch — and Huawei doesn't
                    // tell us which from this field alone.  Surface as Critical
                    // so the dashboard turns red and LINE notifies; the actual
                    // cause must be read from getAlarmList / FusionSolar.
                    installation.Status = InstallationStatus.Maintenance;                    LogApiCall(installation, "GetRealtimeData", 200, true,
                        $"[FAULTY] {installation.InverterBrand} reports station abnormal. " +
                        $"Reason: {realtime.FaultReason ?? "unknown"}. " +
                        $"Today={realtime.TodayEnergyKwh:0.0}kWh");

                    // De-dup: find any open Faulty row (regardless of day) so
                    // we can promote/upgrade it as the situation evolves.
                    var faultTitle = $"Station Faulty: {installation.SiteName}";
                    var existingFault = await _context.SystemAlerts
                        .Where(a => a.InstallationId == installation.Id
                            && a.Title == faultTitle
                            && a.ResolvedAt == null)
                        .OrderByDescending(a => a.OccurredAt)
                        .FirstOrDefaultAsync();

                    // Pull recent confirming alarms from prior sync rounds
                    var since = DateTime.UtcNow.AddHours(-24);
                    var openAlarmTitles = await _context.SystemAlerts
                        .Where(a => a.InstallationId == installation.Id
                            && a.AlertType == AlertType.Error
                            && a.ResolvedAt == null
                            && a.OccurredAt >= since
                            && a.Title != faultTitle)
                        .OrderByDescending(a => a.OccurredAt)
                        .Take(5)
                        .Select(a => a.Title)
                        .ToListAsync();
                    var hasConfirmingAlarm = openAlarmTitles.Count > 0;

                    // ── State-machine for Station Faulty alerts ──────────────────
                    //
                    //   sync   confirming?   age          → row state             → LINE?
                    //   ────   ───────────   ──────       ────────────────────    ──────
                    //   1st    yes           —            Critical / Error        ✓ (cause)
                    //   1st    no            —            Medium / Error          ✗ (silent)
                    //   2nd+   no            ≥ 2 × sync   High / Offline          ✓ (offline)
                    //   any    yes (NEW)     —            promote → Critical      ✓ (cause)
                    //
                    // Why promote to Offline (not just upgrade severity):
                    // a customer who sees real_health_state=1 persisting for two
                    // sync rounds with no FusionSolar alarm is — empirically —
                    // experiencing Grid Loss or comms loss.  Grid Loss alarms
                    // don't appear in getAlarmList until power returns (the
                    // inverter can't report what it can't power), so we must
                    // notify the customer NOW using the cautious Offline
                    // wording instead of waiting indefinitely for an alarm
                    // that may only arrive minutes after the fact.
                    var syncMin = installation.SyncIntervalMinutes <= 0
                        ? 15
                        : installation.SyncIntervalMinutes;
                    var persistenceThreshold = TimeSpan.FromMinutes(syncMin * 2);

                    var commsMessage = $"[{installation.InverterBrand}] {installation.SiteName} รายงานสถานะผิดปกติ (real_health_state=1). " +
                        "อาจเป็นปัญหาที่ inverter, ฝั่งไฟฟ้า, หรือ comms ระหว่าง inverter↔cloud — " +
                        "ต้องดูจากรหัส alarm ใน FusionSolar เพื่อระบุสาเหตุที่แท้จริง";
                    var commsRca = "⚠ STATION-LEVEL ABNORMAL STATE detected via Huawei NBI real_health_state=1.\n" +
                        "Cloud API ตอบกลับได้ปกติ แต่ Huawei รายงานว่าไซต์อยู่ในสถานะผิดปกติ\n" +
                        "สถานะนี้ครอบคลุมหลายกรณี ห้ามเดาจากตัวอย่าง — ต้องอ่านชื่อ alarm จริง:\n" +
                        "  • ความผิดปกติของ inverter เอง (hardware fault)\n" +
                        "  • ปัญหาฝั่งระบบไฟฟ้า (utility/grid)\n" +
                        "  • Inverter↔cloud comms ขาด (data logger, internet ที่ไซต์)\n" +
                        "  • Cloud-side reporting glitch (Huawei รายงานผิดเอง)\n" +
                        "การตรวจสอบ:\n" +
                        "  1. เปิด FusionSolar → ดู Alarm list ของไซต์นี้เพื่อทราบสาเหตุที่แท้จริง\n" +
                        "  2. ถ้า alarm บอก comms/network → ตรวจอินเทอร์เน็ตที่ไซต์ + data logger\n" +
                        "  3. ถ้า alarm บอก grid/voltage → ตรวจไฟฟ้าและ MCB\n" +
                        "  4. ถ้า alarm บอก insulation/arc/temp → ตามขั้นตอนของ alarm นั้น ๆ";

                    if (existingFault == null)
                    {
                        // First sync seeing the faulty state — create row.
                        var alarmSummary = hasConfirmingAlarm
                            ? "\n\nรหัส alarm ที่ Huawei รายงานล่าสุด:\n• " + string.Join("\n• ", openAlarmTitles)
                            : "\n\n⚠ ยังไม่มี alarm ใน FusionSolar's getAlarmList ที่ยืนยันสถานะนี้ — " +
                              "ระบบจะรอ " + persistenceThreshold.TotalMinutes + " นาทีก่อนแจ้งเตือน เผื่อเป็น Huawei reporting glitch ชั่วคราว";

                        _context.SystemAlerts.Add(new SystemAlert
                        {
                            InstallationId = installation.Id,
                            AlertType = AlertType.Error,
                            Severity = hasConfirmingAlarm ? AlertSeverity.Critical : AlertSeverity.Medium,
                            Title = faultTitle,
                            Message = commsMessage + alarmSummary,
                            RootCauseAnalysis = commsRca,
                            OccurredAt = DateTime.UtcNow,
                            // Silent on first sync when no confirming alarm (dashboard
                            // only).  If condition persists past persistenceThreshold,
                            // the next sync's update branch (below) re-fires LINE.
                            IsNotified = !hasConfirmingAlarm,
                        });
                    }
                    else if (hasConfirmingAlarm && existingFault.Severity != AlertSeverity.Critical)
                    {
                        // A confirming alarm just appeared in getAlarmList — we
                        // now know the cause.  Upgrade to Critical/Error and
                        // re-fire LINE with the real alarm names.
                        existingFault.Severity = AlertSeverity.Critical;
                        existingFault.AlertType = AlertType.Error;
                        existingFault.Message = commsMessage +
                            "\n\nรหัส alarm ที่ Huawei รายงานล่าสุด:\n• " + string.Join("\n• ", openAlarmTitles);
                        existingFault.IsNotified = false;
                    }
                    else if (!hasConfirmingAlarm
                             && existingFault.Severity == AlertSeverity.Medium
                             && (DateTime.UtcNow - existingFault.OccurredAt) >= persistenceThreshold)
                    {
                        // Two-or-more sync rounds with real_health_state=1 and
                        // still no alarm to explain it.  Empirically this is
                        // Grid Loss / inverter-off / internet-cut at the site
                        // — Huawei can't report a specific alarm because the
                        // device can't speak.  Promote to Offline so the
                        // customer gets the cautious Thai message about
                        // possible power/internet loss, and fire LINE.
                        existingFault.AlertType = AlertType.Offline;
                        existingFault.Severity = AlertSeverity.High;
                        existingFault.Message = commsMessage +
                            $"\n\n📡 สถานะค้างมานานกว่า {persistenceThreshold.TotalMinutes:0} นาที — มีโอกาสสูงที่:\n" +
                            "  • เครื่อง off (ไฟดับ → Grid Loss alarm จะมาภายหลังเมื่อไฟกลับ)\n" +
                            "  • Data logger / internet ที่ไซต์ขาด\n" +
                            "  • Inverter ค้าง — ต้องไปดูหน้างาน";
                        existingFault.IsNotified = false;
                    }
                }
                else if (isDeviceOnline)
                {
                    if (installation.Status == InstallationStatus.Maintenance)
                        installation.Status = InstallationStatus.Active; // recovered from fault
                    LogApiCall(installation, "GetRealtimeData", 200, true,
                        $"[ONLINE] Power={realtime.CurrentPowerKw:0.00}kW, Today={realtime.TodayEnergyKwh:0.0}kWh");

                    // Auto-resolve any open station-fault alerts for this installation.
                    // Accept BOTH AlertType.Error (initial Station Faulty rows) AND
                    // AlertType.Offline (rows we promoted after persistent
                    // unconfirmed real_health_state=1 — see promotion branch above).
                    var openFaults = await _context.SystemAlerts
                        .Where(a => a.InstallationId == installation.Id
                            && (a.AlertType == AlertType.Error || a.AlertType == AlertType.Offline)
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
                                && (a.AlertType == AlertType.Error || a.AlertType == AlertType.Offline)
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
                // Key by site-local date — see SolarApp.Services.SiteTime
                // for the timezone-rollover bug this avoids.
                var today = SolarApp.Services.SiteTime.Today;

                // Defense layer — physical-plausibility ceiling.  A solar
                // system can produce at MOST capacity × 24 h kWh in a day
                // (and that's only at theoretical peak sun all day, which
                // never actually happens).  If the adapter returns more, the
                // adapter is misreading units / fields and we must not
                // persist it — doing so triggers the monotonic guard which
                // then refuses to lower the value when the adapter is fixed.
                // Real-world ceiling: 18 × capacity gives ~13 PSH headroom
                // for any optimistic numerics; anything above is bogus.
                var plausibleDailyMax = installation.SystemCapacityKw > 0
                    ? installation.SystemCapacityKw * 18m
                    : 1_000_000m; // no capacity set → don't enforce
                if (realtime.TodayEnergyKwh > plausibleDailyMax)
                {
                    _logger.LogWarning(
                        "Implausibly high TodayEnergyKwh={Value} for {SiteName} (capacity={Cap} kWp, max plausible={Max}). " +
                        "Adapter likely misreading units; skipping daily upsert.",
                        realtime.TodayEnergyKwh, installation.SiteName,
                        installation.SystemCapacityKw, plausibleDailyMax);
                    // Skip the daily upsert below; preserve any existing row
                    goto SkipDailyUpsert;
                }

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
                    // Monotonic guard: a daily-energy counter can only go UP
                    // during the day.  If the new reading is lower than what
                    // we already have, the inverter has just rolled its day
                    // over (TodayEnergyKwh reset to ~0) but our local-date
                    // hasn't ticked yet because of clock skew between us and
                    // the inverter, or the API returned momentary bad data.
                    // Either way: don't clobber the bigger value with the
                    // smaller one — it'd irreversibly lose the day's real
                    // final reading.
                    if (realtime.TodayEnergyKwh >= todayRecord.EnergyGenerationKwh)
                        todayRecord.EnergyGenerationKwh = realtime.TodayEnergyKwh;
                    if (realtime.GridExportKw.HasValue && realtime.GridExportKw.Value >= todayRecord.GridExportKwh)
                        todayRecord.GridExportKwh = realtime.GridExportKw.Value;
                    if (realtime.GridImportKw.HasValue && realtime.GridImportKw.Value >= todayRecord.GridImportKwh)
                        todayRecord.GridImportKwh = realtime.GridImportKw.Value;
                    todayRecord.SelfConsumptionKwh = Math.Max(0, todayRecord.EnergyGenerationKwh - todayRecord.GridExportKwh);
                    // SoC isn't monotonic — accept latest reading
                    todayRecord.BatterySocPercent = realtime.BatterySocPercent;
                }
                SkipDailyUpsert: ;       // <-- empty statement required after label

                // ─── 3. Historical backfill ───
                // Pull recent history when there are gaps in the last 30 days.
                // The earlier "only fire when yesterday is missing" heuristic
                // missed several real cases:
                //   * Site added days/weeks ago — yesterday's row exists from
                //     today's sync but everything before is empty.
                //   * Transient sync failure left a hole mid-week — we never
                //     filled it because yesterday is fine.
                // Now: check if ANY of the last 30 days are missing, and if
                // so request 30 days from the API.  The auto-backfill is
                // bounded (30 days, daily granularity) so a one-time API
                // round still respects rate limits; deeper historical
                // backfill goes through the manual /backfill endpoint.
                var last30Start = today.AddDays(-30);
                var existingLast30 = await _context.EnergyData
                    .Where(e => e.InstallationId == installation.Id
                        && e.Granularity == DataGranularity.Daily
                        && e.Timestamp >= last30Start
                        && e.Timestamp < today)
                    .CountAsync();
                var expectedDays = (today - last30Start).Days;
                var hasGaps = existingLast30 < expectedDays;

                if (hasGaps)
                {
                    var historical = await adapter.GetHistoricalDataAsync(credentials,
                        last30Start, today.AddDays(-1));

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
                    && huaweiBatchAlarms.TryGetValue(installation.Id, out var batchedAlarms))
                {
                    // Already fetched as part of the batch
                    alarmsForThisInstallation = batchedAlarms;
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
                        // Dedupe on DisplayTitle, not AlarmName — that's what
                        // gets stored, so an unnamed alarm must match itself.
                        var alarmKeys = alarmsForThisInstallation
                            .Select(a => new { Title = a.DisplayTitle, a.OccurredAt })
                            .ToList();
                        var titles = alarmKeys.Select(k => k.Title).Distinct().ToList();
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
                            var alarmTitle = a.DisplayTitle;
                            if (existingSet.Contains((alarmTitle, a.OccurredAt))) continue;

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
                                Title = alarmTitle,
                                Message = a.DisplayMessage,
                                OccurredAt = a.OccurredAt,
                                ResolvedAt = a.ClearedAt,
                                IsNotified = false
                            });
                            // Prevent dup-add within same batch (same alarm appearing twice in source)
                            existingSet.Add((alarmTitle, a.OccurredAt));
                            totalAlarms++;
                        }
                    }
                    catch (Exception alarmEx)
                    {
                        // Alarms are non-critical, don't fail the whole sync
                        _logger.LogWarning(alarmEx, "Failed to fetch alarms for installation {Id}", installation.Id);
                    }
                }

                // Reached the end without a thrown error → this site synced.
                // Reset its circuit breaker (a device merely offline still
                // counts as a successful sync — the cloud answered).
                Services.InverterApi.Common.SyncCircuitBreaker.RecordSuccess(installation.Id);
                await ResolveApiFailureAlertsAsync(installation);
            }
            catch (TaskCanceledException)
            {
                totalApiFailures++;
                if (Services.InverterApi.Common.SyncCircuitBreaker.RecordFailure(installation.Id))
                    RaiseApiFailureAlert(installation, "การเชื่อมต่อหมดเวลาตอบกลับ (timeout)");
                _logger.LogWarning("Sync timeout for {Id} ({Brand})", installation.Id, installation.InverterBrand);
                LogApiCall(installation, "Sync", 0, false,
                    $"[TIMEOUT] Sync timed out for {installation.SiteName}. " +
                    $"The {installation.InverterBrand} cloud server is too slow or unreachable. " +
                    "This is an API CONNECTION issue, NOT an inverter offline issue.");
            }
            catch (HttpRequestException hre)
            {
                totalApiFailures++;
                if (Services.InverterApi.Common.SyncCircuitBreaker.RecordFailure(installation.Id))
                    RaiseApiFailureAlert(installation, $"ติดต่อเซิร์ฟเวอร์ไม่ได้: {hre.Message}");
                _logger.LogWarning(hre, "Network error syncing {Id} ({Brand})", installation.Id, installation.InverterBrand);
                LogApiCall(installation, "Sync", 0, false,
                    $"[NETWORK ERROR] Cannot reach {installation.InverterBrand} cloud: {hre.Message}. " +
                    "Check SolarApp server's internet connection and DNS. " +
                    "This is an API CONNECTION issue, NOT an inverter offline issue.");
            }
            catch (Exception ex)
            {
                totalApiFailures++;
                if (Services.InverterApi.Common.SyncCircuitBreaker.RecordFailure(installation.Id))
                    RaiseApiFailureAlert(installation, $"{ex.GetType().Name}: {ex.Message}");
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

    /// <summary>
    /// Raises the alert behind the per-site "แจ้งเตือนเมื่อ API ล้มเหลว" switch.
    /// That switch was saved to the database and read by nothing — an operator
    /// could turn it on and would never hear from it. Fired only when the
    /// circuit breaker trips (5 consecutive failures, roughly 75 minutes of
    /// solid failure at the default cadence) so a single flaky poll doesn't
    /// page anyone, and deduped against an unresolved one so a site that stays
    /// broken doesn't re-alert every round.
    /// </summary>
    private void RaiseApiFailureAlert(Installation installation, string reason)
    {
        if (!installation.NotifyOnApiFailure) return;

        // Local first: a second failure in the same round would otherwise add a
        // duplicate, because nothing has been saved yet.
        if (_context.SystemAlerts.Local.Any(IsOpenApiFailure(installation.Id))) return;
        if (_context.SystemAlerts.Any(a => a.InstallationId == installation.Id
                                           && a.AlertType == AlertType.Error
                                           && a.ResolvedAt == null
                                           && a.Title.StartsWith(ApiFailureTitlePrefix))) return;

        _context.SystemAlerts.Add(new SystemAlert
        {
            InstallationId = installation.Id,
            AlertType = AlertType.Error,
            Severity = AlertSeverity.High,
            Title = $"{ApiFailureTitlePrefix}{installation.SiteName}",
            Message =
                $"ดึงข้อมูลจากคลาวด์ {installation.InverterBrand} ไม่สำเร็จติดต่อกัน " +
                $"{Services.InverterApi.Common.SyncCircuitBreaker.FailureThreshold} ครั้ง — {reason}",
            RootCauseAnalysis =
                "นี่คือปัญหาการเชื่อมต่อ API ไม่ใช่ตัวอินเวอร์เตอร์ดับ — ระบบอาจยังผลิตไฟตามปกติ " +
                "แต่ SolarApp มองไม่เห็นข้อมูล\n" +
                "การตรวจสอบ:\n" +
                "  1. เปิดหน้าตั้งค่าไซต์ → กด Test Connection ดูข้อความผิดพลาดจริง\n" +
                "  2. ถ้าเป็นปัญหา credentials → ขอ API key / รหัสผ่านใหม่จากพอร์ทัลผู้ผลิต\n" +
                "  3. ถ้าเป็นปัญหาเครือข่าย → ตรวจอินเทอร์เน็ตและ DNS ของเซิร์ฟเวอร์ SolarApp\n" +
                "ระบบจะหยุดเรียกไซต์นี้ชั่วคราวเพื่อไม่ให้เปลือง rate limit และกลับมาลองเองอัตโนมัติ",
            OccurredAt = DateTime.UtcNow,
            IsNotified = false
        });
    }

    private const string ApiFailureTitlePrefix = "เชื่อมต่อคลาวด์ไม่สำเร็จ: ";

    private static Func<SystemAlert, bool> IsOpenApiFailure(int installationId) =>
        a => a.InstallationId == installationId
             && a.AlertType == AlertType.Error
             && a.ResolvedAt == null
             && a.Title.StartsWith(ApiFailureTitlePrefix);

    /// <summary>
    /// Closes the API-failure alert once the site answers again. Without this
    /// the operator would have to clear by hand a problem that fixed itself,
    /// and the dashboard count would never come down.
    /// </summary>
    private async Task ResolveApiFailureAlertsAsync(Installation installation)
    {
        var open = await _context.SystemAlerts
            .Where(a => a.InstallationId == installation.Id
                        && a.AlertType == AlertType.Error
                        && a.ResolvedAt == null
                        && a.Title.StartsWith(ApiFailureTitlePrefix))
            .ToListAsync();

        foreach (var a in open)
            a.ResolvedAt = DateTime.UtcNow;
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
