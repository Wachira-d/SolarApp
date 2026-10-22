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

    public async Task<DashboardViewModel> GetDashboardAsync(
        string? userId, string role,
        int? filterPartnerId = null,
        ServiceLevel? filterServiceLevel = null,
        bool? filterMonitoredOnly = null,
        InverterBrand? filterBrand = null)
    {
        var query = _context.Installations
            .Include(i => i.Customer)
            .Include(i => i.Partner)
            .AsQueryable();

        // Role-based filtering
        if (role == UserRoles.Customer)
        {
            var customer = await _context.Customers.FirstOrDefaultAsync(c => c.UserId == userId);
            query = customer != null
                ? query.Where(i => i.CustomerId == customer.Id)
                : query.Where(i => false);
        }
        else if (role == UserRoles.Partner)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
            query = user?.PartnerId != null
                ? query.Where(i => i.PartnerId == user.PartnerId)
                : query.Where(i => false);
        }

        // Partner filter (admin can filter by partner)
        if (filterPartnerId.HasValue && filterPartnerId > 0)
            query = query.Where(i => i.PartnerId == filterPartnerId);

        // ── Project-type filters ──
        // ServiceLevel = the contract preset (FullOM / MonitoringOnly / …).
        if (filterServiceLevel.HasValue)
            query = query.Where(i => i.ServiceLevel == filterServiceLevel.Value);

        // Monitored-only = the operator's most common ask: "show me only the
        // sites we actually poll via the cloud API".  IncludesMonitoring is
        // the runtime source of truth (a Custom site can be monitored even if
        // its ServiceLevel isn't MonitoringOnly), so we filter on the flag,
        // not the preset.
        if (filterMonitoredOnly.HasValue)
            query = query.Where(i => i.IncludesMonitoring == filterMonitoredOnly.Value);

        if (filterBrand.HasValue)
            query = query.Where(i => i.InverterBrand == filterBrand.Value);

        var installations = await query.ToListAsync();
        var installationIds = installations.Select(i => i.Id).ToList();

        // Site-local "today" (Bangkok), not UtcNow.Date — daily rows are keyed
        // by the site date, so UtcNow.Date read the wrong row during 00:00–07:00
        // Thailand time and showed 0 / stale "today" energy.
        var today = SolarApp.Services.SiteTime.Today;

        // ── Active alerts ──
        var activeAlerts = await _context.SystemAlerts
            .AsNoTracking()
            .Where(a => installationIds.Contains(a.InstallationId) && a.ResolvedAt == null)
            .OrderByDescending(a => a.Severity)
            .ThenByDescending(a => a.OccurredAt)
            .Take(20)
            .Include(a => a.Installation)
            .ToListAsync();

        // Pre-compute active alert counts per installation (was: O(installations)
        // .Count() calls each scanning activeAlerts in inner loop below).
        var alertCountByInstall = await _context.SystemAlerts
            .AsNoTracking()
            .Where(a => installationIds.Contains(a.InstallationId) && a.ResolvedAt == null)
            .GroupBy(a => a.InstallationId)
            .Select(g => new { InstallationId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.InstallationId, x => x.Count);

        // ── Today's energy ──
        var todayData = await _context.EnergyData
            .AsNoTracking()
            .Where(e => installationIds.Contains(e.InstallationId)
                && e.Granularity == DataGranularity.Daily
                && e.Timestamp.Date == today)
            .ToListAsync();

        // Index today's energy by installation for O(1) lookup in the per-install loop
        var todayDataByInstall = todayData
            .GroupBy(e => e.InstallationId)
            .ToDictionary(g => g.Key, g => g.First());

        // ── Pending maintenance ──
        var pendingMaintenance = await _context.MaintenanceRecords
            .CountAsync(m => installationIds.Contains(m.InstallationId)
                && m.Status == MaintenanceStatus.Scheduled);

        // ── Open tickets ──
        var openTickets = await _context.ServiceTickets
            .CountAsync(t => installationIds.Contains(t.InstallationId)
                && t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed);

        // ── Build installation status items ──
        var items = new List<InstallationStatusItem>();
        foreach (var inst in installations)
        {
            var instAlerts = alertCountByInstall.TryGetValue(inst.Id, out var ac) ? ac : 0;
            var instToday = todayDataByInstall.TryGetValue(inst.Id, out var td) ? td : null;

            items.Add(new InstallationStatusItem
            {
                Id = inst.Id,
                SiteCode = inst.SiteCode,
                SiteName = inst.SiteName,
                CustomerName = inst.Customer.FullName,
                PartnerName = inst.Partner?.CompanyName,
                PartnerId = inst.PartnerId,
                InverterBrand = inst.InverterBrand.ToString(),
                InverterModel = inst.InverterModel,
                InverterSizeKw = inst.InverterSizeKw,
                InverterCount = inst.InverterCount,
                CapacityKw = inst.SystemCapacityKw,
                Status = inst.Status.ToString(),
                LastOnline = inst.LastOnlineAt,
                ActiveAlerts = instAlerts,
                IsCleaningDue = inst.IsCleaningDue,
                IncludesCleaning = inst.IncludesCleaning,
                IncludesWarrantyService = inst.IncludesWarrantyService,
                RemainingCleanings = inst.RemainingCleanings,
                TotalCleaningCount = inst.TotalCleaningCount,
                NextCleaningDate = inst.NextCleaningDueDate,
                HasWarrantyDate = inst.HasWarrantyDate,
                WarrantyExpiry = inst.WarrantyExpiryDate,
                // Not clamped at zero any more: that made "expires today" and
                // "expired eight months ago" both read as 0 days left.
                WarrantyDaysLeft = inst.WarrantyDaysRemaining ?? 0,
                InstallationDate = inst.InstallationDate,
                TodayKwh = instToday?.EnergyGenerationKwh ?? 0
            });
        }

        // ── Partners dropdown ──
        var partners = new List<PartnerOption>();
        if (role == UserRoles.Admin)
        {
            partners = await _context.Partners
                .Where(p => p.IsActive)
                .Select(p => new PartnerOption
                {
                    Id = p.Id,
                    Name = p.CompanyName,
                    InstallationCount = p.Installations.Count
                })
                .OrderBy(p => p.Name)
                .ToListAsync();

            // Add "Direct (no partner)" option
            var directCount = installations.Count(i => i.PartnerId == null);
            if (directCount > 0)
                partners.Insert(0, new PartnerOption { Id = -1, Name = $"Direct Sales ({directCount})" });
        }

        var avgRate = installations.Any() ? installations.Average(i => i.ElectricityRatePerKwh) : 4.0m;
        var todayGenTotal = todayData.Sum(e => e.EnergyGenerationKwh);

        // ── 14-day production trend ──
        // "1,240 kWh today" answers nothing on its own — the operator has to
        // know whether that's a normal Tuesday or half of one. One query,
        // grouped in SQL, so the chart costs a single round-trip.
        var trendFrom = today.AddDays(-13);
        var trendRows = await _context.EnergyData
            .Where(e => installationIds.Contains(e.InstallationId)
                && e.Granularity == DataGranularity.Daily
                && e.Timestamp >= trendFrom && e.Timestamp <= today)
            .GroupBy(e => e.Timestamp.Date)
            .Select(g => new { Day = g.Key, Kwh = g.Sum(x => (decimal?)x.EnergyGenerationKwh) ?? 0 })
            .ToListAsync();

        var trendByDay = trendRows.ToDictionary(r => r.Day, r => r.Kwh);

        // Fill the gaps: a day with no rows is a real zero (nothing synced,
        // or nothing produced) and skipping it would silently compress the
        // x-axis and make an outage look like it never happened.
        var trend = new List<ChartDataPoint>();
        for (var day = trendFrom; day <= today; day = day.AddDays(1))
        {
            trend.Add(new ChartDataPoint
            {
                Label = day.ToString("dd/MM"),
                Value = Math.Round(trendByDay.TryGetValue(day, out var v) ? v : 0, 1)
            });
        }

        return new DashboardViewModel
        {
            TotalInstallations = installations.Count,
            OnlineCount = installations.Count(i => i.Status == InstallationStatus.Active),
            OfflineCount = installations.Count(i => i.Status == InstallationStatus.Offline),
            MaintenanceCount = installations.Count(i => i.Status == InstallationStatus.Maintenance),
            InactiveCount = installations.Count(i => i.Status == InstallationStatus.Inactive),

            CriticalAlertCount = activeAlerts.Count(a => a.Severity == AlertSeverity.Critical),
            HighAlertCount = activeAlerts.Count(a => a.Severity == AlertSeverity.High),
            // Cleaning-due / warranty counts respect the service-scope flags so
            // they match the alert job (CheckMaintenanceDueAsync gates on
            // IncludesCleaning; CheckWarrantyExpiryAsync gates on
            // IncludesWarrantyService).  A site past its cleaning date that we
            // don't clean is not "action required" for us, so it's excluded.
            CleaningDueCount = installations.Count(i => i.IsCleaningDue),
            // Same rule as CheckWarrantyExpiryAsync and
            // GetWarrantyExpiringAsync: recorded end date, inside 90 days,
            // not yet past, and only where we carry the warranty.
            WarrantyExpiringCount = installations.Count(i =>
                i.IncludesWarrantyService
                && i.HasWarrantyDate
                && !i.IsWarrantyExpired
                && i.WarrantyDaysRemaining <= 90),
            PendingMaintenanceCount = pendingMaintenance,
            OpenTicketCount = openTickets,

            ActiveAlerts = activeAlerts,
            OfflineInstallations = items.Where(i => i.Status == "Offline").ToList(),
            CleaningDueInstallations = items.Where(i => i.IsCleaningDue).ToList(),
            // >= 0 so a warranty ending today is still on the list — it was
            // the one day it most needed to be, and > 0 dropped it.
            WarrantyExpiringInstallations = items
                .Where(i => i.IncludesWarrantyService && i.HasWarrantyDate
                            && i.WarrantyDaysLeft >= 0 && i.WarrantyDaysLeft <= 90)
                .OrderBy(i => i.WarrantyDaysLeft)
                .ToList(),

            AllInstallations = items.OrderByDescending(i => i.ActiveAlerts)
                .ThenBy(i => i.Status == "Active" ? 1 : 0)
                .ThenBy(i => i.SiteName)
                .ToList(),

            Partners = partners,
            SelectedPartnerId = filterPartnerId,
            SelectedServiceLevel = filterServiceLevel,
            SelectedMonitoredOnly = filterMonitoredOnly,
            SelectedBrand = filterBrand,
            TotalCapacityKw = installations.Sum(i => i.SystemCapacityKw),
            TodayGenerationKwh = todayGenTotal,
            MonthlySavingsThb = todayGenTotal * avgRate * 30,
            ProductionTrend = trend
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

        // Inclusive day count: a 1-31 May range is 31 days, not 30.  The +1
        // matches PerformanceMetricsService so AverageDailyKwh agrees across
        // the two pages.
        var totalDays = Math.Max(1, (to - from).Days + 1);
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
