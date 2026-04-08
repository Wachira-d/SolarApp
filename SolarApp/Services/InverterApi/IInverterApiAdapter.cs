using SolarApp.Models.Enums;
using SolarApp.Services.InverterApi.Models;

namespace SolarApp.Services.InverterApi;

public interface IInverterApiAdapter
{
    InverterBrand Brand { get; }
    Task<bool> TestConnectionAsync(ApiCredentials credentials);
    Task<PlantInfo?> GetPlantInfoAsync(ApiCredentials credentials);
    Task<RealtimeData?> GetRealtimeDataAsync(ApiCredentials credentials);
    Task<List<EnergyRecord>> GetHistoricalDataAsync(ApiCredentials credentials, DateTime from, DateTime to);
    Task<List<AlarmRecord>> GetAlarmsAsync(ApiCredentials credentials, DateTime from, DateTime to);
    Task<List<DeviceInfo>> GetDevicesAsync(ApiCredentials credentials);
}

public interface IInverterApiAdapterFactory
{
    IInverterApiAdapter GetAdapter(InverterBrand brand);
}
