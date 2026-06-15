using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.ViewModels;

// ═══════════════════════════════════════════════════════════
//  DASHBOARD - Operation Monitoring Center
// ═══════════════════════════════════════════════════════════

public class DashboardViewModel
{
    // ── Status Overview (top bar) ──
    public int TotalInstallations { get; set; }
    public int OnlineCount { get; set; }
    public int OfflineCount { get; set; }
    public int MaintenanceCount { get; set; }
    public int InactiveCount { get; set; }

    // ── Action Required (critical section) ──
    public int CriticalAlertCount { get; set; }
    public int HighAlertCount { get; set; }
    public int CleaningDueCount { get; set; }
    public int WarrantyExpiringCount { get; set; }
    public int PendingMaintenanceCount { get; set; }
    public int OpenTicketCount { get; set; }

    // ── Alerts (recent, unresolved) ──
    public List<SystemAlert> ActiveAlerts { get; set; } = new();

    // ── Installations requiring attention ──
    public List<InstallationStatusItem> OfflineInstallations { get; set; } = new();
    public List<InstallationStatusItem> CleaningDueInstallations { get; set; } = new();
    public List<InstallationStatusItem> WarrantyExpiringInstallations { get; set; } = new();

    // ── All installations summary ──
    public List<InstallationStatusItem> AllInstallations { get; set; } = new();

    // ── Partner filter ──
    public List<PartnerOption> Partners { get; set; } = new();
    public int? SelectedPartnerId { get; set; }

    // ── Project-type filters (round-tripped so the dropdowns keep their
    //    selection after the GET re-renders) ──
    public ServiceLevel? SelectedServiceLevel { get; set; }
    /// <summary>null = all, true = monitored only, false = non-monitored only.</summary>
    public bool? SelectedMonitoredOnly { get; set; }
    public InverterBrand? SelectedBrand { get; set; }

    // ── Quick stats ──
    public decimal TotalCapacityKw { get; set; }
    public decimal TodayGenerationKwh { get; set; }
    public decimal MonthlySavingsThb { get; set; }
}

public class InstallationStatusItem
{
    public int Id { get; set; }
    public string SiteCode { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string? PartnerName { get; set; }
    public int? PartnerId { get; set; }
    public string InverterBrand { get; set; } = string.Empty;
    public string? InverterModel { get; set; }
    public decimal InverterSizeKw { get; set; }
    public int InverterCount { get; set; }
    public decimal CapacityKw { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime? LastOnline { get; set; }
    public int ActiveAlerts { get; set; }
    public bool IsCleaningDue { get; set; }
    public int RemainingCleanings { get; set; }
    public DateTime? NextCleaningDate { get; set; }
    public DateTime WarrantyExpiry { get; set; }
    public int WarrantyDaysLeft { get; set; }
    public DateTime InstallationDate { get; set; }
    public decimal TodayKwh { get; set; }

    // Status helpers
    public string StatusBadgeClass => Status switch
    {
        "Active" => "bg-success",
        "Offline" => "bg-danger",
        "Maintenance" => "bg-warning text-dark",
        "Inactive" => "bg-dark",
        _ => "bg-secondary"
    };

    public string OnlineAgo
    {
        get
        {
            if (!LastOnline.HasValue) return "Never";
            var diff = DateTime.UtcNow - LastOnline.Value;
            if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} min ago";
            if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
            return $"{(int)diff.TotalDays}d ago";
        }
    }
}

public class PartnerOption
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int InstallationCount { get; set; }
}

// ═══════════════════════════════════════════════════════════
//  REPORTS - kept as is
// ═══════════════════════════════════════════════════════════

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
