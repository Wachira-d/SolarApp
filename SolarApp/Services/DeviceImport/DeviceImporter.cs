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
        try
        {
            var plant = await adapter.GetPlantInfoAsync(credentials);
            if (plant != null)
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
                if (changed)
                {
                    installation.UpdatedAt = DateTime.UtcNow;
                    installationNote = "Installation metadata updated from cloud (capacity/address/commission date).";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DeviceImporter: GetPlantInfoAsync failed for installation {Id}", installationId);
        }

        // 2. Fetch device list and upsert Equipment records
        List<DeviceInfo> devices;
        try
        {
            devices = await adapter.GetDevicesAsync(credentials);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DeviceImporter: GetDevicesAsync failed for installation {Id}", installationId);
            await _context.SaveChangesAsync(ct);
            return new DeviceImportResult(false, $"Device list fetch failed: {ex.Message}",
                0, 0, 0, installationNote);
        }

        if (!devices.Any())
        {
            await _context.SaveChangesAsync(ct);
            return new DeviceImportResult(true,
                "Plant info updated but no devices returned. API might not expose device list for this plant.",
                0, 0, 0, installationNote);
        }

        int created = 0, updated = 0;
        foreach (var dev in devices)
        {
            // Match on SerialNumber (strongest) or DeviceId
            var serial = dev.SerialNumber?.Trim();
            var devKey = string.IsNullOrWhiteSpace(serial) ? dev.DeviceId : serial;

            var equipment = installation.Equipment
                .FirstOrDefault(e => (e.SerialNumber ?? "").Equals(devKey, StringComparison.OrdinalIgnoreCase));

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

        await _context.SaveChangesAsync(ct);
        return new DeviceImportResult(true,
            $"Imported {created} new + updated {updated} existing equipment record(s)",
            devices.Count, created, updated, installationNote);
    }

    private static EquipmentType ClassifyDeviceType(string deviceType)
    {
        var t = (deviceType ?? "").ToLowerInvariant();
        if (t.Contains("inverter") || t.Contains("pcs")) return EquipmentType.Inverter;
        if (t.Contains("batter") || t.Contains("bms") || t.Contains("ess")) return EquipmentType.Battery;
        if (t.Contains("meter") || t.Contains("smart")) return EquipmentType.Meter;
        if (t.Contains("panel") || t.Contains("module")) return EquipmentType.SolarPanel;
        return EquipmentType.Inverter; // Most API-exposed devices are inverters
    }
}
