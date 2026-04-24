using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.InverterApi.Models;

namespace SolarApp.Services.InverterApi.Common;

/// <summary>
/// Multi-layer connection health verification.
/// Layer 1: Credential validation (format, required fields)
/// Layer 2: Authentication test (login / token)
/// Layer 3: Plant/station discovery (PlantId exists)
/// Layer 4: Data retrieval (realtime → offline vs API failure)
/// Layer 5: Historical data continuity (gaps detection)
/// Layer 6: Alarm channel verification
///
/// Each layer produces a HealthCheckResult with clear, actionable messages
/// that distinguish device-offline from API-connection-failure.
/// </summary>
public interface IConnectionHealthChecker
{
    Task<ConnectionHealthReport> RunFullCheckAsync(Installation installation);
}

public record HealthCheckStep(
    string Layer,
    string Name,
    bool Passed,
    string Message,
    string? TechnicalDetail,
    long DurationMs,
    ConnectionIssueType IssueType);

public enum ConnectionIssueType
{
    None,
    CredentialMissing,
    CredentialInvalid,
    AuthenticationFailed,
    PlantNotFound,
    ApiConnectionFailed,
    ApiTimeout,
    ApiRateLimited,
    ApiServerError,
    DeviceOffline,
    DataGap,
    AlarmChannelFailed,
    ParseError
}

public class ConnectionHealthReport
{
    public int InstallationId { get; set; }
    public string SiteName { get; set; } = "";
    public string InverterBrand { get; set; } = "";
    public DateTime RunAt { get; set; } = DateTime.UtcNow;
    public bool OverallHealthy { get; set; }
    public List<HealthCheckStep> Steps { get; set; } = new();

    /// <summary>
    /// Summary line: "API Connected, Device Online" / "API Connected, Device Offline" / "API Connection Failed: timeout"
    /// </summary>
    public string Summary { get; set; } = "";

    /// <summary>True if the API was reachable (even if device is offline).</summary>
    public bool ApiReachable { get; set; }
    /// <summary>True if the inverter/plant reports online status.</summary>
    public bool DeviceOnline { get; set; }

    public int PassedCount => Steps.Count(s => s.Passed);
    public int FailedCount => Steps.Count(s => !s.Passed);

    /// <summary>
    /// Build a rich alert message with full context.
    /// </summary>
    public string ToAlertMessage()
    {
        var failed = Steps.Where(s => !s.Passed).ToList();
        if (!failed.Any()) return $"[{InverterBrand}] {SiteName}: All checks passed.";

        var lines = new List<string> { $"[{InverterBrand}] {SiteName}: {failed.Count} issue(s) detected." };
        foreach (var f in failed)
        {
            var issueTag = f.IssueType switch
            {
                ConnectionIssueType.DeviceOffline => "DEVICE OFFLINE",
                ConnectionIssueType.ApiConnectionFailed or ConnectionIssueType.ApiTimeout => "API CONNECTION FAILED",
                ConnectionIssueType.AuthenticationFailed => "CREDENTIALS EXPIRED",
                ConnectionIssueType.ApiRateLimited => "RATE LIMITED",
                ConnectionIssueType.ApiServerError => "CLOUD SERVER ERROR",
                _ => f.IssueType.ToString().ToUpper()
            };
            lines.Add($"  [{issueTag}] {f.Layer}: {f.Message}");
            if (!string.IsNullOrEmpty(f.TechnicalDetail))
                lines.Add($"    Detail: {f.TechnicalDetail}");
        }
        return string.Join("\n", lines);
    }
}

public class ConnectionHealthChecker : IConnectionHealthChecker
{
    private readonly ApplicationDbContext _context;
    private readonly IInverterApiAdapterFactory _adapterFactory;
    private readonly ILogger<ConnectionHealthChecker> _logger;

    public ConnectionHealthChecker(
        ApplicationDbContext context,
        IInverterApiAdapterFactory adapterFactory,
        ILogger<ConnectionHealthChecker> logger)
    {
        _context = context;
        _adapterFactory = adapterFactory;
        _logger = logger;
    }

    public async Task<ConnectionHealthReport> RunFullCheckAsync(Installation installation)
    {
        var report = new ConnectionHealthReport
        {
            InstallationId = installation.Id,
            SiteName = installation.SiteName,
            InverterBrand = installation.InverterBrand.ToString()
        };

        var credentials = new ApiCredentials
        {
            ApiKey = installation.CloudApiKey,
            ApiSecret = installation.CloudApiSecret,
            Username = installation.CloudUsername,
            Password = installation.CloudPassword,
            PlantId = installation.PlantId
        };

        var adapter = _adapterFactory.GetAdapter(installation.InverterBrand);

        // ═══ Layer 1: Credential Format Validation ═══
        var credStep = ValidateCredentials(installation, credentials);
        report.Steps.Add(credStep);
        if (!credStep.Passed) { FinalizeReport(report); return report; }

        // ═══ Layer 2: Authentication + Connection Test ═══
        var authStep = await TestAuthenticationAsync(adapter, credentials);
        report.Steps.Add(authStep);
        report.ApiReachable = authStep.Passed;
        if (!authStep.Passed) { FinalizeReport(report); return report; }

        // ═══ Layer 3: Plant Discovery ═══
        var plantStep = await TestPlantInfoAsync(adapter, credentials);
        report.Steps.Add(plantStep);
        if (!plantStep.Passed) { FinalizeReport(report); return report; }

        // ═══ Layer 4: Realtime Data + Online Status ═══
        var (realtimeStep, isOnline) = await TestRealtimeAsync(adapter, credentials, installation);
        report.Steps.Add(realtimeStep);
        report.DeviceOnline = isOnline;

        // ═══ Layer 5: Historical Data Continuity ═══
        var histStep = await TestHistoricalContinuityAsync(adapter, credentials, installation);
        report.Steps.Add(histStep);

        // ═══ Layer 6: Alarm Channel ═══
        var alarmStep = await TestAlarmChannelAsync(adapter, credentials);
        report.Steps.Add(alarmStep);

        FinalizeReport(report);
        return report;
    }

    private HealthCheckStep ValidateCredentials(Installation inst, ApiCredentials cred)
    {
        var sw = Stopwatch.StartNew();
        var issues = new List<string>();

        // Brand-specific checks
        switch (inst.InverterBrand)
        {
            case InverterBrand.Huawei:
                if (string.IsNullOrEmpty(cred.Username)) issues.Add("Username (Northbound API user) is empty");
                if (string.IsNullOrEmpty(cred.Password)) issues.Add("Password (system code) is empty");
                break;
            case InverterBrand.FoxESS:
                if (string.IsNullOrEmpty(cred.ApiKey)) issues.Add("API Key (Private Token) is empty");
                break;
            case InverterBrand.Solis:
                var isWeb = cred.ApiKey?.Equals("SOLISWEB", StringComparison.OrdinalIgnoreCase) == true;
                if (isWeb)
                {
                    if (string.IsNullOrEmpty(cred.Username)) issues.Add("Username (SolisCloud email) is empty for SOLISWEB mode");
                    if (string.IsNullOrEmpty(cred.Password)) issues.Add("Password is empty for SOLISWEB mode");
                }
                else
                {
                    if (string.IsNullOrEmpty(cred.ApiKey)) issues.Add("API Key is empty");
                    if (string.IsNullOrEmpty(cred.ApiSecret)) issues.Add("API Secret is empty");
                }
                break;
            case InverterBrand.GoodWe:
                if (string.IsNullOrEmpty(cred.Username)) issues.Add("Username (SEMS email) is empty");
                if (string.IsNullOrEmpty(cred.Password)) issues.Add("Password is empty");
                break;
            case InverterBrand.Deye:
                if (string.IsNullOrEmpty(cred.ApiKey)) issues.Add("App ID is empty");
                if (string.IsNullOrEmpty(cred.ApiSecret)) issues.Add("App Secret is empty");
                break;
        }
        if (string.IsNullOrEmpty(cred.PlantId)) issues.Add("Plant ID / Station ID is empty");

        sw.Stop();
        if (issues.Any())
            return new HealthCheckStep("Layer 1", "Credential Validation", false,
                $"Missing credentials: {string.Join("; ", issues)}",
                $"Brand: {inst.InverterBrand}", sw.ElapsedMilliseconds,
                ConnectionIssueType.CredentialMissing);

        return new HealthCheckStep("Layer 1", "Credential Validation", true,
            "All required credentials present", null, sw.ElapsedMilliseconds,
            ConnectionIssueType.None);
    }

    private async Task<HealthCheckStep> TestAuthenticationAsync(
        IInverterApiAdapter adapter, ApiCredentials cred)
    {
        var sw = Stopwatch.StartNew();
        var result = await adapter.TestConnectionResultAsync(cred);
        sw.Stop();

        if (result.IsSuccess)
            return new HealthCheckStep("Layer 2", "Authentication", true,
                "Login successful, API accessible", result.Message, sw.ElapsedMilliseconds,
                ConnectionIssueType.None);

        var issueType = result.Kind switch
        {
            ApiResultKind.AuthenticationFailed => ConnectionIssueType.AuthenticationFailed,
            ApiResultKind.Timeout => ConnectionIssueType.ApiTimeout,
            ApiResultKind.NetworkError => ConnectionIssueType.ApiConnectionFailed,
            ApiResultKind.RateLimited => ConnectionIssueType.ApiRateLimited,
            ApiResultKind.CloudServerError => ConnectionIssueType.ApiServerError,
            ApiResultKind.InvalidCredentials => ConnectionIssueType.CredentialInvalid,
            _ => ConnectionIssueType.ApiConnectionFailed
        };

        return new HealthCheckStep("Layer 2", "Authentication", false,
            result.Message, result.TechnicalDetail ?? "Check API credentials in installation settings",
            sw.ElapsedMilliseconds, issueType);
    }

    private async Task<HealthCheckStep> TestPlantInfoAsync(
        IInverterApiAdapter adapter, ApiCredentials cred)
    {
        var sw = Stopwatch.StartNew();
        var result = await adapter.GetPlantInfoResultAsync(cred);
        sw.Stop();

        if (result.IsSuccess && result.Data is { } info)
            return new HealthCheckStep("Layer 3", "Plant Discovery", true,
                $"Plant found: {info.PlantName} ({info.CapacityKw}kW)",
                $"PlantId={info.PlantId}", sw.ElapsedMilliseconds,
                ConnectionIssueType.None);

        var issueType = result.Kind switch
        {
            ApiResultKind.PlantNotFound => ConnectionIssueType.PlantNotFound,
            ApiResultKind.Timeout => ConnectionIssueType.ApiTimeout,
            ApiResultKind.AuthenticationFailed => ConnectionIssueType.AuthenticationFailed,
            _ => ConnectionIssueType.ApiConnectionFailed
        };

        return new HealthCheckStep("Layer 3", "Plant Discovery", false,
            result.Message, result.TechnicalDetail, sw.ElapsedMilliseconds, issueType);
    }

    private async Task<(HealthCheckStep Step, bool IsOnline)> TestRealtimeAsync(
        IInverterApiAdapter adapter, ApiCredentials cred, Installation inst)
    {
        var sw = Stopwatch.StartNew();
        var result = await adapter.GetRealtimeResultAsync(cred);
        sw.Stop();

        if (result.Kind == ApiResultKind.Success && result.Data is { } data)
        {
            return (new HealthCheckStep("Layer 4", "Realtime Data", true,
                $"Device ONLINE. Power={data.CurrentPowerKw:0.00}kW, Today={data.TodayEnergyKwh:0.0}kWh" +
                (data.BatterySocPercent.HasValue ? $", Battery={data.BatterySocPercent:0}%" : ""),
                null, sw.ElapsedMilliseconds, ConnectionIssueType.None), true);
        }

        if (result.Kind == ApiResultKind.DeviceOffline && result.Data is { } offData)
        {
            return (new HealthCheckStep("Layer 4", "Realtime Data", true,
                $"⚠ API CONNECTED but device reports OFFLINE. Power={offData.CurrentPowerKw:0.00}kW. " +
                "This is different from an API failure — the cloud connection works, " +
                "but the inverter itself is not reporting. " +
                "Normal at night. If daytime, check the inverter's own internet/WiFi connection.",
                $"TodayKwh={offData.TodayEnergyKwh}, Timestamp={offData.Timestamp:HH:mm:ss}",
                sw.ElapsedMilliseconds,
                ConnectionIssueType.DeviceOffline), false);
        }

        var issueType = result.Kind switch
        {
            ApiResultKind.Timeout => ConnectionIssueType.ApiTimeout,
            ApiResultKind.NetworkError => ConnectionIssueType.ApiConnectionFailed,
            ApiResultKind.AuthenticationFailed => ConnectionIssueType.AuthenticationFailed,
            ApiResultKind.RateLimited => ConnectionIssueType.ApiRateLimited,
            ApiResultKind.CloudServerError => ConnectionIssueType.ApiServerError,
            _ => ConnectionIssueType.ApiConnectionFailed
        };
        return (new HealthCheckStep("Layer 4", "Realtime Data", false,
            result.Message, result.TechnicalDetail, sw.ElapsedMilliseconds, issueType), false);
    }

    private async Task<HealthCheckStep> TestHistoricalContinuityAsync(
        IInverterApiAdapter adapter, ApiCredentials cred, Installation inst)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var from = DateTime.UtcNow.Date.AddDays(-7);
            var to = DateTime.UtcNow.Date.AddDays(-1);
            var data = await adapter.GetHistoricalDataAsync(cred, from, to);
            sw.Stop();

            if (!data.Any())
                return new HealthCheckStep("Layer 5", "Historical Data", false,
                    "No historical data for the last 7 days. Check if the cloud account has data.",
                    null, sw.ElapsedMilliseconds, ConnectionIssueType.DataGap);

            var expectedDays = (int)(to - from).TotalDays + 1;
            var gaps = expectedDays - data.Count;
            if (gaps > 2)
                return new HealthCheckStep("Layer 5", "Historical Data", false,
                    $"Data gap: only {data.Count}/{expectedDays} days have data. Missing {gaps} day(s).",
                    $"From {from:yyyy-MM-dd} to {to:yyyy-MM-dd}", sw.ElapsedMilliseconds,
                    ConnectionIssueType.DataGap);

            var totalKwh = data.Sum(d => d.EnergyGenerationKwh);
            return new HealthCheckStep("Layer 5", "Historical Data", true,
                $"{data.Count} day(s) of data, total {totalKwh:0.0}kWh",
                gaps > 0 ? $"{gaps} minor gap(s)" : null, sw.ElapsedMilliseconds,
                ConnectionIssueType.None);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new HealthCheckStep("Layer 5", "Historical Data", false,
                $"Historical query failed: {ex.Message}", ex.GetType().Name,
                sw.ElapsedMilliseconds, ConnectionIssueType.ApiConnectionFailed);
        }
    }

    private async Task<HealthCheckStep> TestAlarmChannelAsync(
        IInverterApiAdapter adapter, ApiCredentials cred)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var alarms = await adapter.GetAlarmsAsync(cred,
                DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);
            sw.Stop();
            return new HealthCheckStep("Layer 6", "Alarm Channel", true,
                $"Alarm channel OK. {alarms.Count} alarm(s) in last 7 days.",
                null, sw.ElapsedMilliseconds, ConnectionIssueType.None);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new HealthCheckStep("Layer 6", "Alarm Channel", false,
                $"Alarm query failed: {ex.Message}",
                "Alarms are non-critical but should work", sw.ElapsedMilliseconds,
                ConnectionIssueType.AlarmChannelFailed);
        }
    }

    private static void FinalizeReport(ConnectionHealthReport report)
    {
        report.OverallHealthy = report.Steps.All(s => s.Passed);

        if (report.ApiReachable && report.DeviceOnline)
            report.Summary = "API Connected, Device Online — all systems operational";
        else if (report.ApiReachable && !report.DeviceOnline)
            report.Summary = "API Connected, Device OFFLINE — inverter not reporting (check inverter WiFi/LAN, normal at night)";
        else
        {
            var failedStep = report.Steps.FirstOrDefault(s => !s.Passed);
            report.Summary = failedStep?.IssueType switch
            {
                ConnectionIssueType.CredentialMissing => "CREDENTIAL ERROR — missing API key/username/password",
                ConnectionIssueType.AuthenticationFailed => "AUTH FAILED — cloud rejected credentials (expired or wrong)",
                ConnectionIssueType.ApiTimeout => "API TIMEOUT — cloud server too slow, check SolarApp server internet",
                ConnectionIssueType.ApiConnectionFailed => "API CONNECTION FAILED — cannot reach cloud server",
                ConnectionIssueType.ApiRateLimited => "RATE LIMITED — too many API calls, reduce sync frequency",
                ConnectionIssueType.PlantNotFound => "PLANT NOT FOUND — Plant ID doesn't exist on this cloud account",
                _ => $"ISSUE: {failedStep?.Message ?? "Unknown"}"
            };
        }
    }
}
