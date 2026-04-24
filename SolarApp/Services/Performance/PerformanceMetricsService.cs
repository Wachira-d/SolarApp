using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.Weather;

namespace SolarApp.Services.Performance;

public interface IPerformanceMetricsService
{
    Task<InstallationPerformance> GetPerformanceAsync(int installationId, DateTime from, DateTime to);
    Task<FleetPerformance> GetFleetPerformanceAsync(System.Security.Claims.ClaimsPrincipal user, DateTime from, DateTime to);
}

public record DailyProduction(DateTime Date, decimal ActualKwh, decimal ExpectedKwh, decimal PerformanceRatio);

public class InstallationPerformance
{
    public Installation Installation { get; set; } = null!;
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public decimal TotalActualKwh { get; set; }
    public decimal TotalExpectedKwh { get; set; }
    public decimal PerformanceRatio { get; set; }       // Actual / Expected
    public decimal CapacityFactor { get; set; }         // Actual / (Capacity × 24h × days)
    public decimal SpecificYieldKwhPerKw { get; set; }  // kWh per kW installed
    public decimal UptimePercent { get; set; }          // (Days - offline days) / days × 100
    public int OfflineDays { get; set; }
    public int UnresolvedAlerts { get; set; }
    public List<DailyProduction> DailySeries { get; set; } = new();
    public decimal EstimatedRevenue { get; set; }
    public decimal EstimatedSavings { get; set; }
    public string HealthGrade { get; set; } = "-";       // A/B/C/D/F
    public string HealthColor { get; set; } = "secondary";
    public List<string> Recommendations { get; set; } = new();
}

public class FleetPerformance
{
    public int InstallationCount { get; set; }
    public decimal TotalCapacityKw { get; set; }
    public decimal TotalActualKwh { get; set; }
    public decimal TotalExpectedKwh { get; set; }
    public decimal FleetPerformanceRatio { get; set; }
    public decimal FleetUptimePercent { get; set; }
    public int ActiveCount { get; set; }
    public int OfflineCount { get; set; }
    public int UnderperformingCount { get; set; }       // PR < 0.75
    public List<InstallationPerformance> TopPerformers { get; set; } = new();
    public List<InstallationPerformance> BottomPerformers { get; set; } = new();
}

public class PerformanceMetricsService : IPerformanceMetricsService
{
    // Thailand peak-sun-hours (PSH) average ≈ 5.0 kWh/m²/day
    // Typical PV system derate factor ≈ 0.80 (losses: wiring, inverter, temp, soiling)
    private const decimal DefaultPeakSunHours = 5.0m;
    private const decimal DefaultDerateFactor = 0.80m;

    private readonly ApplicationDbContext _context;
    private readonly Microsoft.AspNetCore.Identity.UserManager<ApplicationUser> _userManager;
    private readonly IWeatherService _weather;

    public PerformanceMetricsService(
        ApplicationDbContext context,
        Microsoft.AspNetCore.Identity.UserManager<ApplicationUser> userManager,
        IWeatherService weather)
    {
        _context = context;
        _userManager = userManager;
        _weather = weather;
    }

    public async Task<InstallationPerformance> GetPerformanceAsync(int installationId, DateTime from, DateTime to)
    {
        var installation = await _context.Installations
            .Include(i => i.Customer)
            .Include(i => i.Partner)
            .FirstAsync(i => i.Id == installationId);

        var fromDate = from.Date;
        var toDate = to.Date;
        var days = Math.Max(1, (int)(toDate - fromDate).TotalDays + 1);

        var dailyRecords = await _context.EnergyData
            .Where(e => e.InstallationId == installationId
                        && e.Granularity == DataGranularity.Daily
                        && e.Timestamp >= fromDate
                        && e.Timestamp <= toDate)
            .OrderBy(e => e.Timestamp)
            .ToListAsync();

        // Current tariff for revenue/savings estimate
        var tariff = await _context.ElectricityTariffs
            .Where(t => t.InstallationId == installationId
                        && t.EffectiveFrom <= toDate
                        && (t.EffectiveTo == null || t.EffectiveTo >= fromDate))
            .OrderByDescending(t => t.EffectiveFrom)
            .FirstOrDefaultAsync();

        var importRate = tariff?.ImportRatePerKwh ?? 4.5m;   // ~THB 4.50 default
        var exportRate = tariff?.ExportRatePerKwh ?? 2.2m;

        var capacity = installation.SystemCapacityKw;

        // Try weather-based PSH per day if lat/lng present; fall back to constant
        Dictionary<DateTime, decimal>? pshByDate = null;
        if (installation.Latitude.HasValue && installation.Longitude.HasValue
            && Math.Abs(installation.Latitude.Value) > 0.0001
            && Math.Abs(installation.Longitude.Value) > 0.0001)
        {
            try
            {
                var forecast = await _weather.GetForecastAsync(
                    installation.Latitude.Value, installation.Longitude.Value, fromDate, toDate);
                if (forecast != null)
                    pshByDate = forecast.Days.ToDictionary(d => d.Date.Date, d => d.PeakSunHours);
            }
            catch { /* weather failures non-fatal */ }
        }

        var seriesByDate = dailyRecords.ToDictionary(d => d.Timestamp.Date);
        var series = new List<DailyProduction>();
        decimal actualTotal = 0;
        decimal expectedTotal = 0;
        int offlineDays = 0;

        for (var d = fromDate; d <= toDate; d = d.AddDays(1))
        {
            decimal actual = 0;
            if (seriesByDate.TryGetValue(d, out var rec)) actual = rec.EnergyGenerationKwh;
            if (actual <= 0) offlineDays++;
            actualTotal += actual;

            var psh = pshByDate != null && pshByDate.TryGetValue(d, out var p) ? p : DefaultPeakSunHours;
            var expectedPerDay = capacity * psh * DefaultDerateFactor;
            expectedTotal += expectedPerDay;

            var pr = expectedPerDay > 0 ? Math.Round(actual / expectedPerDay, 3) : 0;
            series.Add(new DailyProduction(d, actual, expectedPerDay, pr));
        }
        var performanceRatio = expectedTotal > 0 ? actualTotal / expectedTotal : 0;
        var capacityFactor = capacity > 0 ? actualTotal / (capacity * 24 * days) : 0;
        var specificYield = capacity > 0 ? actualTotal / capacity : 0;
        var uptimePercent = days > 0 ? (decimal)(days - offlineDays) / days * 100 : 0;

        var unresolvedAlerts = await _context.SystemAlerts
            .CountAsync(a => a.InstallationId == installationId && a.ResolvedAt == null);

        // Revenue = export value + savings from self-consumption
        var exportKwh = dailyRecords.Sum(d => d.GridExportKwh);
        var selfConsumed = dailyRecords.Sum(d => d.SelfConsumptionKwh);
        var revenue = exportKwh * exportRate;
        var savings = selfConsumed * importRate;

        var result = new InstallationPerformance
        {
            Installation = installation,
            From = fromDate,
            To = toDate,
            TotalActualKwh = Math.Round(actualTotal, 2),
            TotalExpectedKwh = Math.Round(expectedTotal, 2),
            PerformanceRatio = Math.Round(performanceRatio, 3),
            CapacityFactor = Math.Round(capacityFactor, 3),
            SpecificYieldKwhPerKw = Math.Round(specificYield, 2),
            UptimePercent = Math.Round(uptimePercent, 1),
            OfflineDays = offlineDays,
            UnresolvedAlerts = unresolvedAlerts,
            DailySeries = series,
            EstimatedRevenue = Math.Round(revenue, 2),
            EstimatedSavings = Math.Round(savings, 2)
        };

        AssignHealthGrade(result);
        return result;
    }

    public async Task<FleetPerformance> GetFleetPerformanceAsync(
        System.Security.Claims.ClaimsPrincipal principal, DateTime from, DateTime to)
    {
        var user = await _userManager.GetUserAsync(principal);
        var roles = user != null ? await _userManager.GetRolesAsync(user) : new List<string>();

        var query = _context.Installations.AsQueryable();

        if (!roles.Contains(UserRoles.Admin))
        {
            if (roles.Contains(UserRoles.Partner))
                query = query.Where(i => user!.PartnerId != null && i.PartnerId == user.PartnerId);
            else if (roles.Contains(UserRoles.Customer))
            {
                var cust = await _context.Customers
                    .Where(c => c.UserId == user!.Id).Select(c => c.Id).FirstOrDefaultAsync();
                query = query.Where(i => i.CustomerId == cust);
            }
            else
                query = query.Where(i => false);
        }

        var installations = await query
            .Where(i => i.Status != InstallationStatus.Inactive)
            .ToListAsync();

        var perInstallation = new List<InstallationPerformance>();
        foreach (var inst in installations)
            perInstallation.Add(await GetPerformanceAsync(inst.Id, from, to));

        var fleet = new FleetPerformance
        {
            InstallationCount = installations.Count,
            TotalCapacityKw = installations.Sum(i => i.SystemCapacityKw),
            TotalActualKwh = Math.Round(perInstallation.Sum(p => p.TotalActualKwh), 2),
            TotalExpectedKwh = Math.Round(perInstallation.Sum(p => p.TotalExpectedKwh), 2),
            ActiveCount = installations.Count(i => i.Status == InstallationStatus.Active),
            OfflineCount = installations.Count(i => i.Status == InstallationStatus.Offline),
            UnderperformingCount = perInstallation.Count(p => p.PerformanceRatio < 0.75m),
            TopPerformers = perInstallation
                .OrderByDescending(p => p.PerformanceRatio).Take(5).ToList(),
            BottomPerformers = perInstallation
                .Where(p => p.TotalActualKwh > 0)
                .OrderBy(p => p.PerformanceRatio).Take(5).ToList()
        };

        fleet.FleetPerformanceRatio = fleet.TotalExpectedKwh > 0
            ? Math.Round(fleet.TotalActualKwh / fleet.TotalExpectedKwh, 3) : 0;

        if (perInstallation.Any())
            fleet.FleetUptimePercent = Math.Round(perInstallation.Average(p => p.UptimePercent), 1);

        return fleet;
    }

    private static void AssignHealthGrade(InstallationPerformance p)
    {
        var score = 0m;
        score += p.PerformanceRatio * 60;    // PR weight 60%
        score += (p.UptimePercent / 100) * 30; // Uptime weight 30%
        score += p.UnresolvedAlerts == 0 ? 10 : Math.Max(0, 10 - p.UnresolvedAlerts * 2);

        (p.HealthGrade, p.HealthColor) = score switch
        {
            >= 85 => ("A", "success"),
            >= 70 => ("B", "info"),
            >= 55 => ("C", "warning"),
            >= 40 => ("D", "warning"),
            _ => ("F", "danger")
        };

        // Auto recommendations
        if (p.PerformanceRatio < 0.7m)
            p.Recommendations.Add("Performance Ratio below 70% — check for shading, soiling, or string faults.");
        if (p.UptimePercent < 95)
            p.Recommendations.Add($"Uptime {p.UptimePercent}% — investigate connectivity / inverter health.");
        if (p.UnresolvedAlerts > 0)
            p.Recommendations.Add($"{p.UnresolvedAlerts} unresolved alert(s) — review on Alerts page.");
        if (p.OfflineDays > 3)
            p.Recommendations.Add($"{p.OfflineDays} day(s) with zero production in period — potential inverter outage.");
        if (p.PerformanceRatio > 0.9m && p.UptimePercent > 98)
            p.Recommendations.Add("Excellent performance — system operating near ideal.");
    }
}
