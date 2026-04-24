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

/// <summary>
/// Hangfire recurring job - syncs full data set from all inverter cloud APIs.
/// [DisableConcurrentExecution] prevents overlapping runs if previous sync
/// takes longer than the sync interval. Timeout = 30 min.
/// </summary>
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

        var totalRealtime = 0;
        var totalDaily = 0;
        var totalAlarms = 0;

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
                    LogApiCall(installation, "GetRealtimeData", 500, false, "No data returned");
                    await _context.SaveChangesAsync();
                    continue;
                }

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

                LogApiCall(installation, "GetRealtimeData", 200, true);

                installation.LastOnlineAt = realtime.IsOnline ? DateTime.UtcNow : installation.LastOnlineAt;
                if (realtime.IsOnline && installation.Status == InstallationStatus.Offline)
                    installation.Status = InstallationStatus.Active;

                // Push real-time update to dashboard viewers
                try
                {
                    var payload = new
                    {
                        installation.Id,
                        installation.SiteName,
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
                    _logger.LogDebug(hubEx, "Hub broadcast failed (non-critical)");
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sync failed for installation {Id} ({Brand})",
                    installation.Id, installation.InverterBrand);

                LogApiCall(installation, "GetRealtimeData", 500, false, ex.Message);
                await _context.SaveChangesAsync();
            }
        }

        _logger.LogInformation(
            "Sync complete: {Installations} installations, {Realtime} realtime, {Daily} daily, {Alarms} alarms",
            installations.Count, totalRealtime, totalDaily, totalAlarms);
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
