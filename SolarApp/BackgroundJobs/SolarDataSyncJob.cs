using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.InverterApi;
using SolarApp.Services.InverterApi.Models;

namespace SolarApp.BackgroundJobs;

/// <summary>
/// Hangfire recurring job: syncs energy data from all inverter cloud APIs.
/// Scheduled: every 5 minutes.
/// </summary>
public class SolarDataSyncJob
{
    private readonly ApplicationDbContext _context;
    private readonly IInverterApiAdapterFactory _adapterFactory;
    private readonly ILogger<SolarDataSyncJob> _logger;

    public SolarDataSyncJob(
        ApplicationDbContext context,
        IInverterApiAdapterFactory adapterFactory,
        ILogger<SolarDataSyncJob> logger)
    {
        _context = context;
        _adapterFactory = adapterFactory;
        _logger = logger;
    }

    public async Task ExecuteAsync()
    {
        var installations = await _context.Installations
            .Where(i => i.Status != InstallationStatus.Inactive && i.PlantId != null)
            .ToListAsync();

        _logger.LogInformation("Starting data sync for {Count} installations", installations.Count);

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

                var realtimeData = await adapter.GetRealtimeDataAsync(credentials);
                if (realtimeData != null)
                {
                    installation.LastOnlineAt = realtimeData.IsOnline ? DateTime.UtcNow : installation.LastOnlineAt;

                    if (realtimeData.IsOnline && installation.Status == InstallationStatus.Offline)
                        installation.Status = InstallationStatus.Active;

                    _context.EnergyData.Add(new EnergyData
                    {
                        InstallationId = installation.Id,
                        Timestamp = DateTime.UtcNow,
                        Granularity = DataGranularity.Realtime,
                        PowerGenerationKw = realtimeData.CurrentPowerKw,
                        EnergyGenerationKwh = realtimeData.TodayEnergyKwh,
                        GridExportKwh = realtimeData.GridExportKw ?? 0,
                        GridImportKwh = realtimeData.GridImportKw ?? 0,
                        BatterySocPercent = realtimeData.BatterySocPercent
                    });

                    _context.InverterApiLogs.Add(new InverterApiLog
                    {
                        InstallationId = installation.Id,
                        ApiProvider = installation.InverterBrand.ToString(),
                        Endpoint = "GetRealtimeData",
                        ResponseStatusCode = 200,
                        IsSuccess = true,
                        RequestTime = DateTime.UtcNow
                    });
                }

                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync installation {Id} ({Brand})",
                    installation.Id, installation.InverterBrand);

                _context.InverterApiLogs.Add(new InverterApiLog
                {
                    InstallationId = installation.Id,
                    ApiProvider = installation.InverterBrand.ToString(),
                    Endpoint = "GetRealtimeData",
                    ResponseStatusCode = 500,
                    IsSuccess = false,
                    ErrorMessage = ex.Message,
                    RequestTime = DateTime.UtcNow
                });

                await _context.SaveChangesAsync();
            }
        }

        _logger.LogInformation("Data sync completed for {Count} installations", installations.Count);
    }
}
