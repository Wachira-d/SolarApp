using SolarApp.Models.Enums;

namespace SolarApp.Services.Telemetry;

/// <summary>
/// Who writes a site's EnergyData — the cloud poller or the gateway — given
/// Installation.TelemetrySource. One place, so SolarDataSyncJob and the
/// rollup bridge can never both think they own the same row.
/// </summary>
public static class TelemetrySourcePolicy
{
    /// <summary>A gateway that pushed within this window is the live source of a Hybrid site.</summary>
    public static readonly TimeSpan GatewayFreshWindow = TimeSpan.FromMinutes(10);

    /// <summary>Local-gateway sites are never polled: there may be no cloud account at all.</summary>
    public static bool PollsCloud(TelemetrySource source) => source != TelemetrySource.LocalGateway;

    /// <summary>
    /// Whether the cloud poll may write realtime / daily EnergyData. Hybrid
    /// sites keep being polled for vendor alarms, but while the gateway is
    /// pushing, its data is the better record (1-minute, every channel) and
    /// a second writer would make the realtime chart zig-zag between sources.
    /// When the gateway goes quiet the cloud takes over again — the fallback
    /// Hybrid exists for.
    /// </summary>
    public static bool CloudWritesEnergyData(TelemetrySource source, DateTime? gatewayLastSeenUtc, DateTime nowUtc) =>
        source switch
        {
            TelemetrySource.LocalGateway => false,
            TelemetrySource.Hybrid => !(gatewayLastSeenUtc is { } seen && nowUtc - seen <= GatewayFreshWindow),
            _ => true,
        };

    /// <summary>The rollup bridge writes Daily EnergyData only where the gateway is a source of record.</summary>
    public static bool GatewayWritesDaily(TelemetrySource source) => source != TelemetrySource.Cloud;
}
