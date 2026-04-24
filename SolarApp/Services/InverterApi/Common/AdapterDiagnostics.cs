using System.Diagnostics;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.InverterApi.Models;

namespace SolarApp.Services.InverterApi.Common;

public class DiagnosticStep
{
    public string Name { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    public int ItemCount { get; set; }
    public string? Detail { get; set; }
}

public class DiagnosticReport
{
    public int InstallationId { get; set; }
    public string SiteName { get; set; } = string.Empty;
    public string Brand { get; set; } = string.Empty;
    public string? PlantId { get; set; }
    public DateTime RunAt { get; set; } = DateTime.UtcNow;
    public long TotalDurationMs { get; set; }
    public List<DiagnosticStep> Steps { get; set; } = new();

    public int PassedCount => Steps.Count(s => s.Success);
    public int FailedCount => Steps.Count(s => !s.Success);
    public bool OverallSuccess => FailedCount == 0;
    public string Summary => $"{PassedCount}/{Steps.Count} checks passed ({TotalDurationMs}ms)";
}

public interface IAdapterDiagnostics
{
    Task<DiagnosticReport> RunAsync(Installation installation);
}

public class AdapterDiagnostics : IAdapterDiagnostics
{
    private readonly IInverterApiAdapterFactory _factory;
    private readonly ILogger<AdapterDiagnostics> _logger;

    public AdapterDiagnostics(IInverterApiAdapterFactory factory, ILogger<AdapterDiagnostics> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<DiagnosticReport> RunAsync(Installation installation)
    {
        var report = new DiagnosticReport
        {
            InstallationId = installation.Id,
            SiteName = installation.SiteName,
            Brand = installation.InverterBrand.ToString(),
            PlantId = installation.PlantId
        };

        var swTotal = Stopwatch.StartNew();

        // Step 0: Validate credentials present
        report.Steps.Add(ValidateCredentials(installation));
        if (!report.Steps[0].Success)
        {
            report.TotalDurationMs = swTotal.ElapsedMilliseconds;
            return report;
        }

        IInverterApiAdapter adapter;
        try
        {
            adapter = _factory.GetAdapter(installation.InverterBrand);
        }
        catch (Exception ex)
        {
            report.Steps.Add(new DiagnosticStep
            {
                Name = "Get Adapter",
                Success = false,
                Message = $"Adapter not registered for brand {installation.InverterBrand}: {ex.Message}"
            });
            report.TotalDurationMs = swTotal.ElapsedMilliseconds;
            return report;
        }

        var credentials = new ApiCredentials
        {
            ApiKey = installation.CloudApiKey,
            ApiSecret = installation.CloudApiSecret,
            Username = installation.CloudUsername,
            Password = installation.CloudPassword,
            PlantId = installation.PlantId
        };

        // Step 1: Test Connection / Authentication
        await RunStepAsync(report, "1. Authentication", async () =>
        {
            var (ok, msg) = await adapter.TestConnectionAsync(credentials);
            return (ok, msg, 0, null);
        });

        // Step 2: Get Plant Info (if authenticated)
        if (report.Steps.Last().Success)
        {
            await RunStepAsync(report, "2. Plant Info", async () =>
            {
                var plant = await adapter.GetPlantInfoAsync(credentials);
                if (plant == null) return (false, "No plant data returned", 0, null);
                return (true, $"Plant: {plant.PlantName} | Capacity: {plant.CapacityKw} kW",
                    1, $"ID={plant.PlantId}, Address={plant.Address ?? "N/A"}");
            });
        }

        // Step 3: Real-time data
        if (!string.IsNullOrEmpty(installation.PlantId))
        {
            await RunStepAsync(report, "3. Realtime Data", async () =>
            {
                var rt = await adapter.GetRealtimeDataAsync(credentials);
                if (rt == null) return (false, "No realtime data returned", 0, null);

                var detail = $"Power={rt.CurrentPowerKw} kW, Today={rt.TodayEnergyKwh} kWh, " +
                             $"Total={rt.TotalEnergyKwh} kWh, Online={rt.IsOnline}";
                if (rt.GridExportKw.HasValue) detail += $", Export={rt.GridExportKw} kW";
                if (rt.BatterySocPercent.HasValue) detail += $", Battery={rt.BatterySocPercent}%";

                return (true,
                    $"Power: {rt.CurrentPowerKw:N2} kW | Today: {rt.TodayEnergyKwh:N1} kWh | {(rt.IsOnline ? "ONLINE" : "OFFLINE")}",
                    1, detail);
            });
        }

        // Step 4: Historical data (last 7 days)
        if (!string.IsNullOrEmpty(installation.PlantId))
        {
            await RunStepAsync(report, "4. Historical Data (7 days)", async () =>
            {
                var from = DateTime.UtcNow.Date.AddDays(-7);
                var to = DateTime.UtcNow.Date.AddDays(-1);
                var records = await adapter.GetHistoricalDataAsync(credentials, from, to);
                if (records.Count == 0) return (true, "No historical records (may be empty / rate limited)", 0, null);

                var totalGen = records.Sum(r => r.EnergyGenerationKwh);
                return (true,
                    $"{records.Count} daily records | Total: {totalGen:N1} kWh",
                    records.Count,
                    $"Range: {records.Min(r => r.Timestamp):dd/MM} to {records.Max(r => r.Timestamp):dd/MM}");
            });
        }

        // Step 5: Devices
        if (!string.IsNullOrEmpty(installation.PlantId))
        {
            await RunStepAsync(report, "5. Device List", async () =>
            {
                var devices = await adapter.GetDevicesAsync(credentials);
                if (devices.Count == 0) return (true, "No devices returned", 0, null);
                var online = devices.Count(d => d.IsOnline);
                return (true,
                    $"{devices.Count} devices ({online} online)",
                    devices.Count,
                    string.Join(", ", devices.Take(3).Select(d => d.DeviceName)));
            });
        }

        // Step 6: Alarms (last 24h)
        if (!string.IsNullOrEmpty(installation.PlantId))
        {
            await RunStepAsync(report, "6. Alarms (24h)", async () =>
            {
                var alarms = await adapter.GetAlarmsAsync(credentials,
                    DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);
                return (true,
                    alarms.Count == 0 ? "No active alarms" : $"{alarms.Count} alarms found",
                    alarms.Count,
                    alarms.Count > 0 ? string.Join(" | ", alarms.Take(3).Select(a => a.AlarmName)) : null);
            });
        }

        report.TotalDurationMs = swTotal.ElapsedMilliseconds;
        return report;
    }

    private DiagnosticStep ValidateCredentials(Installation inst)
    {
        var missing = new List<string>();
        var brand = inst.InverterBrand.ToString();

        if (string.IsNullOrEmpty(inst.PlantId))
            missing.Add("Plant/Station ID");

        switch (inst.InverterBrand)
        {
            case InverterBrand.Huawei:
                if (string.IsNullOrEmpty(inst.CloudUsername)) missing.Add("Username (Northbound API user)");
                if (string.IsNullOrEmpty(inst.CloudPassword)) missing.Add("Password (systemCode)");
                break;
            case InverterBrand.FoxESS:
                if (string.IsNullOrEmpty(inst.CloudApiKey)) missing.Add("API Key (Private Token)");
                break;
            case InverterBrand.Solis:
                var isWeb = string.Equals(inst.CloudApiKey, "SOLISWEB", StringComparison.OrdinalIgnoreCase);
                if (!isWeb && string.IsNullOrEmpty(inst.CloudApiKey)) missing.Add("API Key ID");
                if (!isWeb && string.IsNullOrEmpty(inst.CloudApiSecret)) missing.Add("API Secret");
                if (isWeb && string.IsNullOrEmpty(inst.CloudUsername)) missing.Add("Username (SolisCloud email)");
                if (isWeb && string.IsNullOrEmpty(inst.CloudPassword)) missing.Add("Password");
                break;
            case InverterBrand.GoodWe:
                if (string.IsNullOrEmpty(inst.CloudUsername)) missing.Add("Username (SEMS email)");
                if (string.IsNullOrEmpty(inst.CloudPassword)) missing.Add("Password");
                break;
            case InverterBrand.Deye:
                if (string.IsNullOrEmpty(inst.CloudApiKey)) missing.Add("App ID");
                if (string.IsNullOrEmpty(inst.CloudApiSecret)) missing.Add("App Secret");
                if (string.IsNullOrEmpty(inst.CloudUsername)) missing.Add("Email");
                if (string.IsNullOrEmpty(inst.CloudPassword)) missing.Add("Password");
                break;
        }

        return new DiagnosticStep
        {
            Name = "0. Credentials Check",
            Success = missing.Count == 0,
            Message = missing.Count == 0
                ? $"All required {brand} credentials provided"
                : $"Missing: {string.Join(", ", missing)}",
            DurationMs = 0
        };
    }

    private async Task RunStepAsync(DiagnosticReport report, string name,
        Func<Task<(bool success, string message, int count, string? detail)>> action)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var (success, message, count, detail) = await action();
            report.Steps.Add(new DiagnosticStep
            {
                Name = name,
                Success = success,
                Message = message,
                ItemCount = count,
                DurationMs = sw.ElapsedMilliseconds,
                Detail = detail
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Diagnostic step {Step} failed", name);
            report.Steps.Add(new DiagnosticStep
            {
                Name = name,
                Success = false,
                Message = $"Exception: {ex.GetType().Name}: {ex.Message}",
                DurationMs = sw.ElapsedMilliseconds
            });
        }
    }
}
