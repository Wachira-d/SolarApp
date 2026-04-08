using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.InverterApi;
using SolarApp.Services.InverterApi.Models;

namespace SolarApp.BackgroundJobs;

/// <summary>
/// Background job that periodically syncs energy data from inverter cloud APIs.
/// Runs every N minutes (configured in appsettings.json).
/// </summary>
public class SolarDataSyncJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SolarDataSyncJob> _logger;
    private readonly IConfiguration _configuration;

    public SolarDataSyncJob(
        IServiceScopeFactory scopeFactory,
        ILogger<SolarDataSyncJob> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = _configuration.GetValue("InverterApis:SyncIntervalMinutes", 15);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncAllInstallationsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in solar data sync job");
            }

            await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
        }
    }

    private async Task SyncAllInstallationsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var adapterFactory = scope.ServiceProvider.GetRequiredService<IInverterApiAdapterFactory>();

        var installations = await context.Installations
            .Where(i => i.Status != InstallationStatus.Inactive && i.PlantId != null)
            .ToListAsync(ct);

        _logger.LogInformation("Starting data sync for {Count} installations", installations.Count);

        foreach (var installation in installations)
        {
            try
            {
                var adapter = adapterFactory.GetAdapter(installation.InverterBrand);
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
                    {
                        installation.Status = InstallationStatus.Active;
                    }

                    // Store energy data point
                    var energyData = new EnergyData
                    {
                        InstallationId = installation.Id,
                        Timestamp = DateTime.UtcNow,
                        Granularity = DataGranularity.Realtime,
                        PowerGenerationKw = realtimeData.CurrentPowerKw,
                        EnergyGenerationKwh = realtimeData.TodayEnergyKwh,
                        GridExportKwh = realtimeData.GridExportKw ?? 0,
                        GridImportKwh = realtimeData.GridImportKw ?? 0,
                        BatterySocPercent = realtimeData.BatterySocPercent
                    };

                    context.EnergyData.Add(energyData);

                    // Log successful API call
                    context.InverterApiLogs.Add(new InverterApiLog
                    {
                        InstallationId = installation.Id,
                        ApiProvider = installation.InverterBrand.ToString(),
                        Endpoint = "GetRealtimeData",
                        ResponseStatusCode = 200,
                        IsSuccess = true,
                        RequestTime = DateTime.UtcNow
                    });
                }

                await context.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync data for installation {Id} ({Brand})",
                    installation.Id, installation.InverterBrand);

                context.InverterApiLogs.Add(new InverterApiLog
                {
                    InstallationId = installation.Id,
                    ApiProvider = installation.InverterBrand.ToString(),
                    Endpoint = "GetRealtimeData",
                    ResponseStatusCode = 500,
                    IsSuccess = false,
                    ErrorMessage = ex.Message,
                    RequestTime = DateTime.UtcNow
                });

                await context.SaveChangesAsync(ct);
            }
        }

        _logger.LogInformation("Data sync completed for {Count} installations", installations.Count);
    }
}
