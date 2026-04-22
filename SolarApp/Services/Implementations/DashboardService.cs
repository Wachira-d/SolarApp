using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.Interfaces;
using SolarApp.ViewModels;

namespace SolarApp.Services.Implementations;

public class DashboardService : IDashboardService
{
    private readonly ApplicationDbContext _context;

    public DashboardService(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<DashboardViewModel> GetDashboardAsync(string? userId, string role)
    {
        var query = _context.Installations.AsQueryable();

        // Role-based filtering using new Customer/Partner entities
        if (role == UserRoles.Customer)
        {
            var customer = await _context.Customers.FirstOrDefaultAsync(c => c.UserId == userId);
            if (customer != null)
                query = query.Where(i => i.CustomerId == customer.Id);
            else
                query = query.Where(i => false);
        }
        else if (role == UserRoles.Partner)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user?.PartnerId != null)
                query = query.Where(i => i.PartnerId == user.PartnerId);
            else
                query = query.Where(i => false);
        }

        var installations = await query
            .Include(i => i.Customer)
            .ToListAsync();

        var installationIds = installations.Select(i => i.Id).ToList();
        var today = DateTime.UtcNow.Date;
        var monthStart = new DateTime(today.Year, today.Month, 1);

        var todayData = await _context.EnergyData
            .Where(e => installationIds.Contains(e.InstallationId)
                && e.Granularity == DataGranularity.Daily
                && e.Timestamp.Date == today)
            .ToListAsync();

        var monthlyData = await _context.EnergyData
            .Where(e => installationIds.Contains(e.InstallationId)
                && e.Granularity == DataGranularity.Daily
                && e.Timestamp >= monthStart)
            .ToListAsync();

        var activeAlerts = await _context.SystemAlerts
            .Where(a => installationIds.Contains(a.InstallationId) && a.ResolvedAt == null)
            .OrderByDescending(a => a.OccurredAt)
            .Take(10)
            .Include(a => a.Installation)
            .ToListAsync();

        var avgRate = installations.Any() ? installations.Average(i => i.ElectricityRatePerKwh) : 4.0m;

        var last30Days = today.AddDays(-30);
        var dailyChartRaw = await _context.EnergyData
            .Where(e => installationIds.Contains(e.InstallationId)
                && e.Granularity == DataGranularity.Daily
                && e.Timestamp >= last30Days)
            .GroupBy(e => e.Timestamp.Date)
            .Select(g => new
            {
                Date = g.Key,
                Gen = g.Sum(e => e.EnergyGenerationKwh),
                Self = g.Sum(e => e.SelfConsumptionKwh)
            })
            .OrderBy(g => g.Date)
            .ToListAsync();

        var dailyChart = dailyChartRaw.Select(g => new ChartDataPoint
        {
            Label = g.Date.ToString("dd/MM"),
            Value = g.Gen,
            Value2 = g.Self
        }).ToList();

        var last12Months = today.AddMonths(-12);
        var monthlyChartRaw = await _context.EnergyData
            .Where(e => installationIds.Contains(e.InstallationId)
                && e.Granularity == DataGranularity.Daily
                && e.Timestamp >= last12Months)
            .GroupBy(e => new { e.Timestamp.Year, e.Timestamp.Month })
            .Select(g => new
            {
                g.Key.Year,
                g.Key.Month,
                Gen = g.Sum(e => e.EnergyGenerationKwh),
                Export = g.Sum(e => e.GridExportKwh)
            })
            .OrderBy(g => g.Year).ThenBy(g => g.Month)
            .ToListAsync();

        var monthlyChart = monthlyChartRaw.Select(g => new ChartDataPoint
        {
            Label = $"{g.Month:00}/{g.Year}",
            Value = g.Gen,
            Value2 = g.Export
        }).ToList();

        var summaries = new List<InstallationSummary>();
        foreach (var inst in installations)
        {
            var instToday = todayData.Where(e => e.InstallationId == inst.Id);
            var instMonth = monthlyData.Where(e => e.InstallationId == inst.Id);

            summaries.Add(new InstallationSummary
            {
                Id = inst.Id,
                SiteName = inst.SiteName,
                CustomerName = inst.Customer.FullName,
                InverterBrand = inst.InverterBrand.ToString(),
                CapacityKw = inst.SystemCapacityKw,
                Status = inst.Status.ToString(),
                TodayKwh = instToday.Sum(e => e.EnergyGenerationKwh),
                MonthKwh = instMonth.Sum(e => e.EnergyGenerationKwh),
                LastOnline = inst.LastOnlineAt,
                ActiveAlerts = activeAlerts.Count(a => a.InstallationId == inst.Id)
            });
        }

        var todayGenKwh = todayData.Sum(e => e.EnergyGenerationKwh);
        var monthGenKwh = monthlyData.Sum(e => e.EnergyGenerationKwh);

        return new DashboardViewModel
        {
            TotalInstallations = installations.Count,
            ActiveInstallations = installations.Count(i => i.Status == InstallationStatus.Active),
            OfflineInstallations = installations.Count(i => i.Status == InstallationStatus.Offline),
            TotalCapacityKw = installations.Sum(i => i.SystemCapacityKw),
            TodayGenerationKwh = todayGenKwh,
            TodayExportKwh = todayData.Sum(e => e.GridExportKwh),
            TodaySelfConsumptionKwh = todayData.Sum(e => e.SelfConsumptionKwh),
            TodaySavingsThb = todayGenKwh * avgRate,
            MonthlyGenerationKwh = monthGenKwh,
            MonthlySavingsThb = monthGenKwh * avgRate,
            ActiveAlertCount = activeAlerts.Count,
            RecentAlerts = activeAlerts,
            PendingMaintenanceCount = await _context.MaintenanceRecords
                .CountAsync(m => installationIds.Contains(m.InstallationId)
                    && m.Status == MaintenanceStatus.Scheduled),
            CleaningDueCount = installations.Count(i => i.IsCleaningDue),
            WarrantyExpiringCount = installations.Count(i =>
                i.WarrantyExpiryDate <= DateTime.UtcNow.AddDays(90) && !i.IsWarrantyExpired),
            DailyProductionChart = dailyChart,
            MonthlyProductionChart = monthlyChart,
            Installations = summaries
        };
    }

    public async Task<ProductionReportViewModel> GetProductionReportAsync(
        int installationId, DateTime from, DateTime to)
    {
        var installation = await _context.Installations
            .Include(i => i.Customer)
            .FirstOrDefaultAsync(i => i.Id == installationId)
            ?? throw new ArgumentException("Installation not found");

        var data = await _context.EnergyData
            .Where(e => e.InstallationId == installationId
                && e.Granularity == DataGranularity.Daily
                && e.Timestamp >= from && e.Timestamp <= to)
            .OrderBy(e => e.Timestamp)
            .ToListAsync();

        var totalDays = Math.Max(1, (to - from).Days);
        var totalGen = data.Sum(e => e.EnergyGenerationKwh);

        return new ProductionReportViewModel
        {
            Installation = installation,
            FromDate = from,
            ToDate = to,
            TotalGenerationKwh = totalGen,
            TotalExportKwh = data.Sum(e => e.GridExportKwh),
            TotalImportKwh = data.Sum(e => e.GridImportKwh),
            TotalSelfConsumptionKwh = data.Sum(e => e.SelfConsumptionKwh),
            TotalSavingsThb = totalGen * installation.ElectricityRatePerKwh,
            AverageDailyKwh = totalGen / totalDays,
            DailyData = data.Select(e => new ChartDataPoint
            {
                Label = e.Timestamp.ToString("dd/MM/yyyy"),
                Value = e.EnergyGenerationKwh,
                Value2 = e.SelfConsumptionKwh
            }).ToList(),
            MonthlyData = data.GroupBy(e => new { e.Timestamp.Year, e.Timestamp.Month })
                .Select(g => new ChartDataPoint
                {
                    Label = $"{g.Key.Month:00}/{g.Key.Year}",
                    Value = g.Sum(e => e.EnergyGenerationKwh),
                    Value2 = g.Sum(e => e.GridExportKwh)
                }).ToList()
        };
    }
}
