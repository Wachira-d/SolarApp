using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.InverterApi;
using SolarApp.Services.InverterApi.Models;

namespace SolarApp.Services.DeviceImport;

/// <summary>
/// Imports device metadata (model, serial, firmware, install date) from the
/// cloud API into local Installation + Equipment records. Run once when the
/// credentials are first saved, or via "Refresh device info" button, or
/// piggy-backed on the scheduled sync (no more than once per 24h to avoid
/// wasting API quota).
/// </summary>
public interface IDeviceImporter
{
    Task<DeviceImportResult> ImportAsync(int installationId, CancellationToken ct = default);
}

public record DeviceImportResult(
    bool Success,
    string Message,
    int DevicesFetched,
    int EquipmentCreated,
    int EquipmentUpdated,
    string? InstallationUpdate);

public class DeviceImporter : IDeviceImporter
{
    private readonly ApplicationDbContext _context;
    private readonly IInverterApiAdapterFactory _factory;
    private readonly Services.Security.ICredentialProtector _credProtector;
    private readonly ILogger<DeviceImporter> _logger;

    public DeviceImporter(
        ApplicationDbContext context,
        IInverterApiAdapterFactory factory,
        Services.Security.ICredentialProtector credProtector,
        ILogger<DeviceImporter> logger)
    {
        _context = context;
        _factory = factory;
        _credProtector = credProtector;
        _logger = logger;
    }

    public async Task<DeviceImportResult> ImportAsync(int installationId, CancellationToken ct = default)
    {
        var installation = await _context.Installations
            .Include(i => i.Equipment)
            .FirstOrDefaultAsync(i => i.Id == installationId, ct);
        if (installation == null)
            return new DeviceImportResult(false, "Installation not found", 0, 0, 0, null);

        if (string.IsNullOrEmpty(installation.PlantId))
            return new DeviceImportResult(false, "Plant/Station ID is not configured", 0, 0, 0, null);

        var adapter = _factory.GetAdapter(installation.InverterBrand);
        var credentials = new ApiCredentials
        {
            ApiKey = _credProtector.Unprotect(installation.CloudApiKey),
            ApiSecret = _credProtector.Unprotect(installation.CloudApiSecret),
            Username = _credProtector.Unprotect(installation.CloudUsername),
            Password = _credProtector.Unprotect(installation.CloudPassword),
            PlantId = installation.PlantId
        };

        // 1. Try to get plant-level info (capacity, install date, location)
        string? installationNote = null;
        var plantResult = await adapter.GetPlantInfoResultAsync(credentials, ct);
        if (plantResult.IsSuccess && plantResult.Data is { } plant)
        {
            bool changed = false;
            if (plant.CapacityKw > 0 && installation.SystemCapacityKw != plant.CapacityKw)
            {
                installation.SystemCapacityKw = plant.CapacityKw;
                changed = true;
            }
            if (!string.IsNullOrWhiteSpace(plant.Address)
                && string.IsNullOrWhiteSpace(installation.Address))
            {
                installation.Address = plant.Address;
                changed = true;
            }
            if (plant.InstallDate.HasValue && !installation.CommissionDate.HasValue)
            {
                installation.CommissionDate = plant.InstallDate;
                changed = true;
            }
            if (plant.Latitude.HasValue && !installation.Latitude.HasValue)
            {
                installation.Latitude = plant.Latitude.Value;
                changed = true;
            }
            if (plant.Longitude.HasValue && !installation.Longitude.HasValue)
            {
                installation.Longitude = plant.Longitude.Value;
                changed = true;
            }
            if (changed)
            {
                installation.UpdatedAt = DateTime.UtcNow;
                installationNote = "Installation metadata updated from cloud (capacity/address/commission date/GPS).";
            }
        }
        else if (plantResult.IsApiFailure)
        {
            _logger.LogWarning("DeviceImporter: GetPlantInfo for installation {Id} failed: {Kind} — {Msg}",
                installationId, plantResult.Kind, plantResult.Message);
        }

        // 2. Fetch device list and upsert Equipment records
        var devResult = await adapter.GetDevicesResultAsync(credentials, ct);
        var devices = devResult.Data ?? new List<DeviceInfo>();

        if (!devices.Any())
        {
            await _context.SaveChangesAsync(ct);
            var reason = devResult.IsApiFailure
                ? $"[{devResult.Kind}] {devResult.Message}"
                : "API returned no devices for this plant. Possible causes: " +
                  "wrong Plant ID, no device-level access for your Northbound account, or plant has no inverters.";
            _logger.LogWarning("DeviceImporter: zero devices for installation {Id} ({Brand}). Reason: {Reason}",
                installationId, installation.InverterBrand, reason);
            return new DeviceImportResult(false, reason, 0, 0, 0, installationNote);
        }

        int created = 0, updated = 0, skipped = 0;
        foreach (var dev in devices)
        {
            var serial = dev.SerialNumber?.Trim();
            var devKey = string.IsNullOrWhiteSpace(serial) ? dev.DeviceId?.Trim() : serial;
            if (string.IsNullOrWhiteSpace(devKey))
            {
                skipped++;
                continue;
            }

            // Match on SerialNumber (strongest) — check BOTH in-memory collection
            // AND direct DB query to catch equipment added in previous Refresh clicks
            var equipment = installation.Equipment
                .FirstOrDefault(e => !string.IsNullOrEmpty(e.SerialNumber)
                    && e.SerialNumber.Equals(devKey, StringComparison.OrdinalIgnoreCase));

            if (equipment == null)
            {
                // Double-check DB directly (handles race condition / stale Include)
                equipment = await _context.Equipment
                    .FirstOrDefaultAsync(e => e.InstallationId == installation.Id
                        && e.SerialNumber != null
                        && e.SerialNumber == devKey, ct);
            }

            var eqType = ClassifyDeviceType(dev.DeviceType);

            if (equipment == null)
            {
                equipment = new Equipment
                {
                    InstallationId = installation.Id,
                    EquipmentType = eqType,
                    Brand = installation.InverterBrand.ToString(),
                    Model = dev.Model ?? dev.DeviceName,
                    SerialNumber = serial ?? dev.DeviceId,
                    Quantity = 1,
                    RatingKw = dev.RatedPowerKw,
                    WarrantyYears = 10,
                    InstalledDate = dev.InstallDate ?? installation.InstallationDate,
                    WarrantyExpiryDate = (dev.InstallDate ?? installation.InstallationDate).AddYears(10),
                    IsActive = dev.IsOnline,
                    Notes = string.IsNullOrEmpty(dev.FirmwareVersion)
                        ? $"Auto-imported from {installation.InverterBrand} cloud"
                        : $"Auto-imported. Firmware: {dev.FirmwareVersion}"
                };
                _context.Equipment.Add(equipment);
                created++;

                // First inverter found → update installation's primary InverterModel
                if (eqType == EquipmentType.Inverter && string.IsNullOrEmpty(installation.InverterModel))
                {
                    installation.InverterModel = dev.Model ?? dev.DeviceName;
                    installation.UpdatedAt = DateTime.UtcNow;
                }
            }
            else
            {
                bool eqChanged = false;
                if (!string.IsNullOrWhiteSpace(dev.Model) && equipment.Model != dev.Model)
                { equipment.Model = dev.Model; eqChanged = true; }
                if (dev.RatedPowerKw.HasValue && equipment.RatingKw != dev.RatedPowerKw)
                { equipment.RatingKw = dev.RatedPowerKw; eqChanged = true; }
                if (!string.IsNullOrWhiteSpace(dev.FirmwareVersion)
                    && (equipment.Notes ?? "").IndexOf(dev.FirmwareVersion, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    equipment.Notes = string.IsNullOrWhiteSpace(equipment.Notes)
                        ? $"Firmware: {dev.FirmwareVersion}"
                        : $"{equipment.Notes} | Firmware: {dev.FirmwareVersion}";
                    eqChanged = true;
                }
                if (equipment.IsActive != dev.IsOnline)
                { equipment.IsActive = dev.IsOnline; eqChanged = true; }
                if (eqChanged) updated++;
            }
        }

        // ─── 3. Update Installation summary fields from imported devices ───
        // Recalculate inverter model/size/count from the Equipment table
        // (not just newly imported — use ALL equipment for this installation)
        var allEquipment = await _context.Equipment
            .Where(e => e.InstallationId == installation.Id)
            .ToListAsync(ct);

        var inverters = allEquipment
            .Where(e => e.EquipmentType == EquipmentType.Inverter)
            .ToList();

        if (inverters.Any())
        {
            // Primary inverter model = the one with highest rating (or first)
            var primary = inverters.OrderByDescending(e => e.RatingKw ?? 0).First();
            installation.InverterModel = primary.Model;
            installation.InverterCount = inverters.Count;
            installation.InverterSizeKw = primary.RatingKw ?? 0;

            // Total inverter capacity (for display "X kW × N = Y kW")
            // is computed from TotalInverterCapacityKw property using Count × Size

            installationNote = (installationNote ?? "") +
                $" | Inverters: {inverters.Count}× {primary.Model}" +
                (primary.RatingKw.HasValue ? $" ({primary.RatingKw.Value} kW each)" : "");
        }

        // Panel info from solar panel equipment
        var panels = allEquipment
            .Where(e => e.EquipmentType == EquipmentType.SolarPanel)
            .ToList();
        if (panels.Any())
        {
            var totalPanels = panels.Sum(p => p.Quantity);
            var primaryPanel = panels.OrderByDescending(p => p.RatingWatts ?? 0).First();
            if (totalPanels > 0) installation.NumberOfPanels = totalPanels;
            if (primaryPanel.RatingWatts.HasValue && primaryPanel.RatingWatts > 0)
                installation.PanelWattage = primaryPanel.RatingWatts.Value;
            if (!string.IsNullOrWhiteSpace(primaryPanel.Brand))
                installation.PanelBrand = primaryPanel.Brand;
            if (!string.IsNullOrWhiteSpace(primaryPanel.Model))
                installation.PanelModel = primaryPanel.Model;
        }

        // Battery info
        var batteries = allEquipment
            .Where(e => e.EquipmentType == EquipmentType.Battery)
            .ToList();
        if (batteries.Any())
        {
            var primary = batteries.First();
            installation.BatteryBrand = primary.Brand;
            installation.BatteryModel = primary.Model;
            installation.BatteryCount = batteries.Count;
            var totalKwh = batteries
                .Where(b => b.RatingKw.HasValue)
                .Sum(b => b.RatingKw!.Value);
            if (totalKwh > 0) installation.BatteryCapacityKwh = totalKwh;
        }

        // System capacity from plant info (already set above) or calculated
        if (installation.SystemCapacityKw <= 0 && inverters.Any())
        {
            var totalInvKw = inverters.Sum(i => (i.RatingKw ?? 0) * i.Quantity);
            if (totalInvKw > 0) installation.SystemCapacityKw = totalInvKw;
        }

        installation.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);
        return new DeviceImportResult(true,
            $"Imported {created} new + updated {updated} existing + skipped {skipped} (no serial). " +
            $"Total from API: {devices.Count} device(s).",
            devices.Count, created, updated, installationNote);
    }

    private static EquipmentType ClassifyDeviceType(string deviceType)
    {
        var t = (deviceType ?? "").ToLowerInvariant();

        // Exact matches first (from Huawei MapHuaweiDevType + other brands)
        if (t.Contains("meter") || t.Contains("sensor") || t.Contains("power meter"))
            return EquipmentType.Meter;
        if (t.Contains("batter") || t.Contains("bms") || t.Contains("ess") || t.Contains("battery pack"))
            return EquipmentType.Battery;
        if (t.Contains("logger") || t.Contains("datalog") || t.Contains("emi"))
            return EquipmentType.Other;       // Smart Logger / EMI / Datalogger = not inverter
        if (t.Contains("optimizer") || t.Contains("merc") || t.Contains("mlpe"))
            return EquipmentType.Optimizer;
        if (t.Contains("charger") || t.Contains("ev"))
            return EquipmentType.Charger;
        if (t.Contains("panel") || t.Contains("module"))
            return EquipmentType.SolarPanel;
        if (t.Contains("inverter") || t.Contains("pcs") || t.Contains("micro"))
            return EquipmentType.Inverter;

        return EquipmentType.Other;           // Unknown → Other (not Inverter)
    }
}
