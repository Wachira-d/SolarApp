using Hangfire;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Hubs;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.InverterApi;
using SolarApp.Services.InverterApi.Models;

namespace SolarApp.BackgroundJobs;

public class SolarDataSyncJob
{
    private readonly ApplicationDbContext _context;
    private readonly IInverterApiAdapterFactory _adapterFactory;
    private readonly ILogger<SolarDataSyncJob> _logger;
    private readonly IHubContext<DashboardHub> _hub;

    public SolarDataSyncJob(
        ApplicationDbContext context,
        IInverterApiAdapterFactory adapterFactory,
        ILogger<SolarDataSyncJob> logger,
        IHubContext<DashboardHub> hub)
    {
        _context = context;
        _adapterFactory = adapterFactory;
        _logger = logger;
        _hub = hub;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 1800)]
    public async Task ExecuteAsync()
    {
        var installations = await _context.Installations
            .Where(i => i.Status != InstallationStatus.Inactive && i.PlantId != null)
            .ToListAsync();

        _logger.LogInformation("Solar data sync: starting for {Count} installations", installations.Count);

        int totalRealtime = 0, totalDaily = 0, totalAlarms = 0;
        int totalApiFailures = 0, totalDeviceOffline = 0;

        foreach (var installation in installations)
        {
            try
            {
                var adapter = _adapterFactory.GetAdapter(installation.InverterBrand);
                var credentials = new ApiCredentials
                {
                    ApiKey = installation.CloudApiKey,
                    ApiSecret = installation.CloudApiSecret,
                    Username = installation.CloudUsername,
                    Password = installation.CloudPassword,
                    PlantId = installation.PlantId
                };

                // ─── 1. Realtime snapshot ───
                var realtime = await adapter.GetRealtimeDataAsync(credentials);

                if (realtime == null)
                {
                    // API failure — could NOT reach cloud or parse response
                    totalApiFailures++;
                    LogApiCall(installation, "GetRealtimeData", 500, false,
                        $"[API FAILURE] Could not retrieve data from {installation.InverterBrand} cloud. " +
                        "This does NOT mean the inverter is offline — it means SolarApp cannot reach the cloud API. " +
                        "Possible causes: network issue, API credentials expired, cloud server down, rate limiting.");
                    await _context.SaveChangesAsync();
                    continue;
                }

                // ─── Got data — now distinguish: online vs device-offline ───
                var isDeviceOnline = realtime.IsOnline;

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

                if (isDeviceOnline)
                {
                    LogApiCall(installation, "GetRealtimeData", 200, true,
                        $"[ONLINE] Power={realtime.CurrentPowerKw:0.00}kW, Today={realtime.TodayEnergyKwh:0.0}kWh");
                    installation.LastOnlineAt = DateTime.UtcNow;
                    if (installation.Status == InstallationStatus.Offline)
                        installation.Status = InstallationStatus.Active;
                }
                else
                {
                    totalDeviceOffline++;
                    LogApiCall(installation, "GetRealtimeData", 200, true,
                        $"[DEVICE OFFLINE] API connected OK, but inverter reports offline/sleeping. " +
                        $"Power={realtime.CurrentPowerKw:0.00}kW. " +
                        "This is normal at night. If daytime — check inverter's own internet connection, not SolarApp.");
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
                catch { /* hub failure is non-critical */ }

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

                // ─── 4. Alarms (last 24 hours) ───
                try
                {
                    var alarms = await adapter.GetAlarmsAsync(credentials,
                        DateTime.UtcNow.AddHours(-24), DateTime.UtcNow);

                    foreach (var a in alarms)
                    {
                        var exists = await _context.SystemAlerts.AnyAsync(x =>
                            x.InstallationId == installation.Id
                            && x.Title == a.AlarmName
                            && x.OccurredAt == a.OccurredAt);

                        if (!exists)
                        {
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
                            totalAlarms++;
                        }
                    }
                }
                catch (Exception alarmEx)
                {
                    // Alarms are non-critical, don't fail the whole sync
                    _logger.LogWarning(alarmEx, "Failed to fetch alarms for installation {Id}", installation.Id);
                }

                await _context.SaveChangesAsync();
            }
            catch (TaskCanceledException)
            {
                totalApiFailures++;
                _logger.LogWarning("Sync timeout for {Id} ({Brand})", installation.Id, installation.InverterBrand);
                LogApiCall(installation, "Sync", 0, false,
                    $"[TIMEOUT] Sync timed out for {installation.SiteName}. " +
                    $"The {installation.InverterBrand} cloud server is too slow or unreachable. " +
                    "This is an API CONNECTION issue, NOT an inverter offline issue.");
                await _context.SaveChangesAsync();
            }
            catch (HttpRequestException hre)
            {
                totalApiFailures++;
                _logger.LogWarning(hre, "Network error syncing {Id} ({Brand})", installation.Id, installation.InverterBrand);
                LogApiCall(installation, "Sync", 0, false,
                    $"[NETWORK ERROR] Cannot reach {installation.InverterBrand} cloud: {hre.Message}. " +
                    "Check SolarApp server's internet connection and DNS. " +
                    "This is an API CONNECTION issue, NOT an inverter offline issue.");
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                totalApiFailures++;
                _logger.LogError(ex, "Sync failed for {Id} ({Brand})", installation.Id, installation.InverterBrand);
                LogApiCall(installation, "Sync", 500, false,
                    $"[UNEXPECTED ERROR] {ex.GetType().Name}: {ex.Message}");
                await _context.SaveChangesAsync();
            }
        }

        _logger.LogInformation(
            "Sync complete: {Total} installations | {Realtime} realtime OK | {Daily} daily records | {Alarms} alarms | " +
            "{ApiFailures} API failures | {DeviceOffline} device-offline",
            installations.Count, totalRealtime, totalDaily, totalAlarms, totalApiFailures, totalDeviceOffline);
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
