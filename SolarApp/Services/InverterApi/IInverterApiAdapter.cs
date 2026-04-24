using SolarApp.Models.Enums;
using SolarApp.Services.InverterApi.Models;

namespace SolarApp.Services.InverterApi;

/// <summary>
/// Unified interface for every inverter cloud API.
/// NEW *ResultAsync methods return ApiResult&lt;T&gt; so callers can distinguish:
///   - Success: data received, device online
///   - DeviceOffline: API ok, device reports offline
///   - Timeout/NetworkError/AuthenticationFailed/RateLimited/etc.: API issue
/// Legacy methods remain for backward compatibility (default-interface
/// implementations that thinly wrap the ApiResult methods).
/// </summary>
public interface IInverterApiAdapter
{
    InverterBrand Brand { get; }

    // ═══ New typed API (adapters MUST implement) ═══
    Task<ApiResult<bool>> TestConnectionResultAsync(ApiCredentials credentials, CancellationToken ct = default);
    Task<ApiResult<PlantInfo>> GetPlantInfoResultAsync(ApiCredentials credentials, CancellationToken ct = default);
    Task<ApiResult<RealtimeData>> GetRealtimeResultAsync(ApiCredentials credentials, CancellationToken ct = default);
    Task<ApiResult<List<EnergyRecord>>> GetHistoricalResultAsync(ApiCredentials credentials, DateTime from, DateTime to, CancellationToken ct = default);
    Task<ApiResult<List<AlarmRecord>>> GetAlarmsResultAsync(ApiCredentials credentials, DateTime from, DateTime to, CancellationToken ct = default);
    Task<ApiResult<List<DeviceInfo>>> GetDevicesResultAsync(ApiCredentials credentials, CancellationToken ct = default);

    // ═══ Legacy wrappers (auto-implemented) ═══
    async Task<(bool Success, string Message)> TestConnectionAsync(ApiCredentials credentials)
    {
        var r = await TestConnectionResultAsync(credentials);
        return (r.IsSuccess, r.Message);
    }

    async Task<PlantInfo?> GetPlantInfoAsync(ApiCredentials credentials)
        => (await GetPlantInfoResultAsync(credentials)).Data;

    async Task<RealtimeData?> GetRealtimeDataAsync(ApiCredentials credentials)
        => (await GetRealtimeResultAsync(credentials)).Data;

    async Task<List<EnergyRecord>> GetHistoricalDataAsync(ApiCredentials credentials, DateTime from, DateTime to)
        => (await GetHistoricalResultAsync(credentials, from, to)).Data ?? new();

    async Task<List<AlarmRecord>> GetAlarmsAsync(ApiCredentials credentials, DateTime from, DateTime to)
        => (await GetAlarmsResultAsync(credentials, from, to)).Data ?? new();

    async Task<List<DeviceInfo>> GetDevicesAsync(ApiCredentials credentials)
        => (await GetDevicesResultAsync(credentials)).Data ?? new();
}

public interface IInverterApiAdapterFactory
{
    IInverterApiAdapter GetAdapter(InverterBrand brand);
}
