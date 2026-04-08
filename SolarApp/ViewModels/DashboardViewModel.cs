using SolarApp.Models;

namespace SolarApp.ViewModels;

public class DashboardViewModel
{
    // Summary cards
    public int TotalInstallations { get; set; }
    public int ActiveInstallations { get; set; }
    public int OfflineInstallations { get; set; }
    public decimal TotalCapacityKw { get; set; }

    // Today's production
    public decimal TodayGenerationKwh { get; set; }
    public decimal TodayExportKwh { get; set; }
    public decimal TodaySelfConsumptionKwh { get; set; }
    public decimal TodaySavingsThb { get; set; }

    // Monthly production
    public decimal MonthlyGenerationKwh { get; set; }
    public decimal MonthlySavingsThb { get; set; }

    // Alerts
    public int ActiveAlertCount { get; set; }
    public List<SystemAlert> RecentAlerts { get; set; } = new();

    // Maintenance
    public int PendingMaintenanceCount { get; set; }
    public int CleaningDueCount { get; set; }
    public int WarrantyExpiringCount { get; set; }

    // Charts data (JSON serialized for Chart.js)
    public List<ChartDataPoint> DailyProductionChart { get; set; } = new();
    public List<ChartDataPoint> MonthlyProductionChart { get; set; } = new();

    // Installations list
    public List<InstallationSummary> Installations { get; set; } = new();
}

public class InstallationSummary
{
    public int Id { get; set; }
    public string SiteName { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string InverterBrand { get; set; } = string.Empty;
    public decimal CapacityKw { get; set; }
    public string Status { get; set; } = string.Empty;
    public decimal TodayKwh { get; set; }
    public decimal MonthKwh { get; set; }
    public DateTime? LastOnline { get; set; }
    public int ActiveAlerts { get; set; }
}

public class ChartDataPoint
{
    public string Label { get; set; } = string.Empty;
    public decimal Value { get; set; }
    public decimal? Value2 { get; set; }
}

public class ProductionReportViewModel
{
    public Installation Installation { get; set; } = null!;
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }

    public decimal TotalGenerationKwh { get; set; }
    public decimal TotalExportKwh { get; set; }
    public decimal TotalImportKwh { get; set; }
    public decimal TotalSelfConsumptionKwh { get; set; }
    public decimal TotalSavingsThb { get; set; }
    public decimal AverageDailyKwh { get; set; }

    public List<ChartDataPoint> DailyData { get; set; } = new();
    public List<ChartDataPoint> MonthlyData { get; set; } = new();
}
