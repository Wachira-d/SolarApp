using SolarApp.Services.InverterApi.Models;

namespace SolarApp.Services.InverterApi;

/// <summary>
/// One selectable value for the Plant/Station ID field, fetched live from
/// the vendor cloud so the operator picks instead of hunting for an id in
/// a portal URL and pasting it by hand.
/// </summary>
/// <param name="Id">The exact value to store in Installation.PlantId.</param>
/// <param name="Name">Human label (site or device name).</param>
/// <param name="Kind">"plant" or "device" — plants aggregate, devices are single units.</param>
/// <param name="Detail">Extra context: capacity, model, online state…</param>
/// <param name="Recommended">True for the option most sites should choose.</param>
public record PlantOption(string Id, string Name, string Kind, string? Detail = null, bool Recommended = false);

/// <summary>
/// Optional capability: "list what this account can see".
///
/// Every brand names and formats its site identifier differently — a
/// numeric sid, an NE= station code, a GUID, or an inverter serial — and
/// getting it wrong fails in ways that look like an auth problem. Rather
/// than documenting seven scavenger hunts, adapters that can enumerate
/// their account expose the real choices and the operator picks one.
///
/// Implemented per adapter as the vendor's API allows; callers must
/// check for the interface and fall back to manual entry.
/// </summary>
public interface IPlantDiscovery
{
    Task<List<PlantOption>> DiscoverAsync(ApiCredentials credentials, CancellationToken ct = default);
}
