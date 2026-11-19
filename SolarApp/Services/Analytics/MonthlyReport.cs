using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.Telemetry;

namespace SolarApp.Services.Analytics;

/// <summary>One option of the report's month picker.</summary>
public sealed record ReportMonth(int Year, int Month, bool Partial)
{
    public string Key => $"{Year:0000}-{Month:00}";
}

/// <summary>A bar of the daily production chart, in SVG user units.</summary>
public sealed record DailyBar(int Day, double? Kwh, double X, double Y, double Width, double Height, bool Best, bool Future);

/// <summary>Server-side geometry of the month's daily production bar chart (prints without JavaScript).</summary>
public sealed class DailyChart
{
    public const double Width = 680, Height = 190, Left = 44, Right = 8, Top = 10, Bottom = 22;
    public double PlotWidth => Width - Left - Right;
    public double PlotHeight => Height - Top - Bottom;
    public double Baseline => Top + PlotHeight;
    public double MaxKwh { get; init; }
    public List<DailyBar> Bars { get; init; } = new();
    /// <summary>Y-axis gridlines: value and y position.</summary>
    public List<(double Kwh, double Y)> Ticks { get; init; } = new();

    /// <summary>One bar per calendar day; a day without data is a zero-height bar.</summary>
    public static DailyChart Build(int year, int month, IReadOnlyDictionary<int, double> kwhByDay, DateTime? today = null)
    {
        var days = DateTime.DaysInMonth(year, month);
        var max = kwhByDay.Count > 0 ? kwhByDay.Values.Max() : 0;
        var top = NiceCeiling(max);
        var chart = new DailyChart { MaxKwh = top };
        var slot = chart.PlotWidth / days;
        var width = Math.Max(1, slot * 0.72);
        var bestDay = kwhByDay.Count > 0 && max > 0 ? kwhByDay.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key : 0;
        for (var d = 1; d <= days; d++)
        {
            double? kwh = kwhByDay.TryGetValue(d, out var v) ? v : null;
            var h = kwh is > 0 && top > 0 ? kwh.Value / top * chart.PlotHeight : 0;
            var future = today is DateTime t && new DateTime(year, month, d) > t.Date;
            chart.Bars.Add(new DailyBar(d, kwh, Round(Left + (d - 1) * slot + (slot - width) / 2), Round(chart.Baseline - h),
                Round(width), Round(h), d == bestDay, future));
        }
        const int steps = 4;
        for (var i = 0; i <= steps; i++)
        {
            var val = top * i / steps;
            chart.Ticks.Add((val, Round(chart.Baseline - (top > 0 ? val / top * chart.PlotHeight : 0))));
        }
        return chart;
    }

    /// <summary>1, 2, 2.5 or 5 × 10ⁿ at or above the value, so the axis reads in round numbers.</summary>
    public static double NiceCeiling(double v)
    {
        if (!(v > 0) || !double.IsFinite(v)) return 10;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1, 2, 2.5, 5, 10 })
            if (m * mag >= v - 1e-9) return m * mag;
        return 10 * mag;
    }

    private static double Round(double v) => Math.Round(v, 2);
}

/// <summary>What one site did in one month — the printable monthly report (/Installations/Report, /My/Report).</summary>
public sealed class MonthlySiteReport
{
    public Installation Site { get; init; } = null!;
    public SiteCapabilities Caps { get; init; } = null!;
    public string? CustomerName { get; init; }
    public int Year { get; init; }
    public int Month { get; init; }
    /// <summary>The month is still running (the current site-local month).</summary>
    public bool Partial { get; init; }
    public int DaysInMonth => DateTime.DaysInMonth(Year, Month);
    /// <summary>Days counted for "healthy days": the whole month, or up to today when it is still running.</summary>
    public int DaysCounted { get; init; }
    public string MonthKey => $"{Year:0000}-{Month:00}";
    public string MonthLabel => MonthlyReportBuilder.ThaiMonth(Year, Month);
    public List<ReportMonth> Months { get; init; } = new();
    public DateTime GeneratedAt { get; init; }

    // Production and money (Daily EnergyData — any source, so cloud-only sites too)
    public EnvironmentalFactors Factors { get; init; } = EnvironmentalFactors.Default;
    public EconomicsOptions Options { get; init; } = new();
    public bool HasEnergyData { get; init; }
    public MonthlyEnergy? Energy { get; init; }
    public MonthlyEconomics? Economics { get; init; }
    public MonthlyEnergy? PrevEnergy { get; init; }
    public MonthlyEconomics? PrevEconomics { get; init; }
    public MonthlyEnergy? LastYearEnergy { get; init; }
    public MonthlyEconomics? LastYearEconomics { get; init; }
    public bool HasMonthData => Energy != null;
    public double PvKwh => Energy?.PvKwh ?? 0;
    public EconomicsResult? Result => Economics?.Result;
    public double? CumulativeSavingsThb => Economics?.CumulativeSavingsThb;
    public double? PaybackPct => Economics?.CumulativePaybackPct;
    public double? PvVsPrevPct => Change(Energy?.PvKwh, PrevEnergy?.PvKwh);
    public double? SavingsVsPrevPct => Change(Result?.SavingsThb, PrevEconomics?.Result.SavingsThb);
    public double? PvVsLastYearPct => Change(Energy?.PvKwh, LastYearEnergy?.PvKwh);
    public double? SavingsVsLastYearPct => Change(Result?.SavingsThb, LastYearEconomics?.Result.SavingsThb);

    /// <summary>kWh per day of the month (days without a row are absent).</summary>
    public Dictionary<int, double> DailyKwh { get; init; } = new();
    public DailyChart Chart { get; init; } = new();
    public int DaysWithData => DailyKwh.Count;
    public (int Day, double Kwh)? BestDay => DailyKwh.Count == 0 || DailyKwh.Values.Max() <= 0 ? null
        : DailyKwh.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)).First();
    public double? AverageDailyKwh => DailyKwh.Count > 0 ? DailyKwh.Values.Average() : null;

    // Outages (telemetry with grid status or voltage, from a stored grid snapshot)
    public bool ShowOutages => Caps.HasTelemetry && (Caps.HasGridStatus || Caps.HasGridVoltage);
    public bool OutagesCovered { get; init; }
    /// <summary>Site-local date the outage data starts from, when it starts inside the month.</summary>
    public DateTime? OutagesFrom { get; init; }
    public List<OutageEvent> Outages { get; init; } = new();

    // Battery (telemetry battery with stored analytics)
    public bool ShowBattery => Caps.HasTelemetry && Caps.HasBattery && (Battery != null || BatteryDays.Count > 0);
    public BatteryReport? Battery { get; init; }
    public DateTime? BatteryAt { get; init; }
    public List<BatteryDay> BatteryDays { get; init; } = new();

    // Service (same counts the LINE monthly report sends)
    public int OfflineDays { get; init; }
    public int HealthyDays => Math.Max(0, DaysCounted - OfflineDays);
    public int AlertCount { get; init; }
    public int CleaningsDone { get; init; }
    public int WorkOrdersCompleted { get; init; }

    public static double? Change(double? now, double? before) =>
        now is double n && before is double b && b > 0 ? (n / b - 1) * 100 : null;
}

/// <summary>
/// Builds <see cref="MonthlySiteReport"/> for one site and month. Money and CO₂
/// come from the same path as the analytics pages (Daily EnergyData grouped by
/// month → <see cref="EconomicsCalculator.Monthly"/> with
/// <see cref="EnvironmentalFactors.Apply"/> on the site's options), so the
/// printed month matches the month row on /Installations/Analytics and
/// /My/Analytics. Authorisation is the caller's job.
/// </summary>
public static class MonthlyReportBuilder
{
    public const int PickerMonths = 24;
    private static readonly CultureInfo Th = CultureInfo.GetCultureInfo("th-TH");

    public static string ThaiMonth(int year, int month) => new DateTime(year, month, 1).ToString("MMMM yyyy", Th);

    /// <summary>The last complete site-local month.</summary>
    public static (int Year, int Month) DefaultMonth(DateTime today)
    {
        var m = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
        return (m.Year, m.Month);
    }

    /// <summary>"YYYY-MM", not after the current month; anything else falls back to <see cref="DefaultMonth"/>.</summary>
    public static (int Year, int Month) ParseMonth(string? value, DateTime today)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && DateTime.TryParseExact(value.Trim(), "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            && d.Year >= 2000
            && d <= new DateTime(today.Year, today.Month, 1))
            return (d.Year, d.Month);
        return DefaultMonth(today);
    }

    public static async Task<MonthlySiteReport> BuildAsync(ApplicationDbContext db, SiteAnalyticsService analytics,
        Installation site, string? month, DateTime today, CancellationToken ct = default)
    {
        var (year, mon) = ParseMonth(month, today);
        var monthStart = new DateTime(year, mon, 1);
        var monthEndExclusive = monthStart.AddMonths(1);
        var partial = year == today.Year && mon == today.Month;
        var daysCounted = partial ? today.Day : DateTime.DaysInMonth(year, mon);
        var startUtc = SiteTime.ToUtc(monthStart);
        var endUtc = SiteTime.ToUtc(monthEndExclusive);

        var channels = await db.TelemetryChannels.AsNoTracking().Where(c => c.InstallationId == site.Id).ToListAsync(ct);
        var caps = SiteCapabilities.From(site, channels);

        // ── production and money ─────────────────────────────────
        var rows = await db.EnergyData.AsNoTracking()
            .Where(e => e.InstallationId == site.Id && e.Granularity == DataGranularity.Daily)
            .Select(e => new { e.Timestamp, e.EnergyGenerationKwh, e.GridImportKwh, e.GridExportKwh })
            .ToListAsync(ct);
        var factors = await EnvironmentalFactors.LoadAsync(db, ct);
        var opts = factors.Apply(Pages.Installations.AnalyticsModel.EconomicsOptionsFor(site));
        var energy = rows
            .GroupBy(r => (r.Timestamp.Year, r.Timestamp.Month))
            .OrderBy(g => g.Key)
            .Select(g => new MonthlyEnergy(g.Key.Year, g.Key.Month,
                (double)g.Sum(r => r.EnergyGenerationKwh), (double)g.Sum(r => r.GridImportKwh), (double)g.Sum(r => r.GridExportKwh)))
            .ToList();
        var economics = EconomicsCalculator.Monthly(energy, opts);
        MonthlyEnergy? E(DateTime m) => energy.FirstOrDefault(x => x.Year == m.Year && x.Month == m.Month);
        MonthlyEconomics? X(DateTime m) => economics.FirstOrDefault(x => x.Year == m.Year && x.Month == m.Month);

        var daily = rows.Where(r => r.Timestamp >= monthStart && r.Timestamp < monthEndExclusive)
            .GroupBy(r => r.Timestamp.Day)
            .ToDictionary(g => g.Key, g => (double)g.Sum(r => r.EnergyGenerationKwh));

        var firstPicker = new DateTime(today.Year, today.Month, 1).AddMonths(-(PickerMonths - 1));
        var months = energy
            .Where(m => new DateTime(m.Year, m.Month, 1) >= firstPicker && new DateTime(m.Year, m.Month, 1) <= new DateTime(today.Year, today.Month, 1))
            .Select(m => new ReportMonth(m.Year, m.Month, m.Year == today.Year && m.Month == today.Month))
            .ToList();
        if (!months.Any(m => m.Year == year && m.Month == mon))
            months.Add(new ReportMonth(year, mon, partial));
        months = months.OrderByDescending(m => m.Year).ThenByDescending(m => m.Month).ToList();

        // ── outages: the first grid snapshot computed after the month (covers it), else the latest ──
        var outagesCovered = false;
        DateTime? outagesFrom = null;
        var outages = new List<OutageEvent>();
        if (caps.HasTelemetry && (caps.HasGridStatus || caps.HasGridVoltage))
        {
            var (grid, at) = await SnapshotCoveringAsync<GridSnapshot>(db, analytics, site.Id, SiteAnalyticsKinds.Grid, monthEndExclusive, ct);
            if (grid != null && at is DateTime computed)
            {
                var coverFromUtc = computed.AddDays(-grid.WindowDays);
                if (coverFromUtc < endUtc)
                {
                    outagesCovered = true;
                    if (coverFromUtc > startUtc) outagesFrom = SiteTime.ToSiteTime(coverFromUtc).Date;
                    outages = grid.Outages.Where(o => o.StartUtc >= startUtc && o.StartUtc < endUtc).OrderBy(o => o.StartUtc).ToList();
                }
            }
        }

        // ── battery: the month's stored days plus the report as of the month's end ──
        BatteryReport? battery = null;
        DateTime? batteryAt = null;
        var batteryDays = new List<BatteryDay>();
        if (caps.HasTelemetry && caps.HasBattery)
        {
            (battery, batteryAt) = await SnapshotCoveringAsync<BatteryReport>(db, analytics, site.Id, SiteAnalyticsKinds.Battery, monthEndExclusive, ct);
            var lastDay = DateOnly.FromDateTime(monthEndExclusive.AddDays(-1));
            batteryDays = (await analytics.BatteryDaysAsync(site.Id, DateOnly.FromDateTime(monthStart), ct))
                .Where(d => d.Date <= lastDay).ToList();
        }

        // ── service: same counts as the LINE monthly report (MonthlyReportJob) ──
        var offlineDays = (await db.SystemAlerts.AsNoTracking()
                .Where(a => a.InstallationId == site.Id && a.AlertType == AlertType.Offline
                            && a.OccurredAt >= startUtc && a.OccurredAt < endUtc)
                .Select(a => a.OccurredAt).ToListAsync(ct))
            .Select(t => SiteTime.ToSiteTime(t).Date).Distinct().Count();
        var alertCount = await db.SystemAlerts.AsNoTracking()
            .Where(SystemAlert.CustomerVisible)
            .CountAsync(a => a.InstallationId == site.Id && a.OccurredAt >= startUtc && a.OccurredAt < endUtc, ct);
        var lastDate = monthEndExclusive.AddDays(-1);
        var cleanings = await db.CleaningRecords.AsNoTracking()
            .CountAsync(c => c.InstallationId == site.Id && c.Status == CleaningStatus.Completed
                             && c.ActualDate != null && c.ActualDate >= monthStart && c.ActualDate <= lastDate, ct);
        var workOrders = await db.WorkOrders.AsNoTracking()
            .CountAsync(w => w.InstallationId == site.Id
                             && (w.Status == WorkOrderStatus.Completed || w.Status == WorkOrderStatus.Verified
                                 || w.Status == WorkOrderStatus.Invoiced || w.Status == WorkOrderStatus.Closed)
                             && w.ActualEndTime != null && w.ActualEndTime >= startUtc && w.ActualEndTime < endUtc, ct);

        return new MonthlySiteReport
        {
            Site = site,
            Caps = caps,
            CustomerName = site.Customer?.FullName,
            Year = year,
            Month = mon,
            Partial = partial,
            DaysCounted = daysCounted,
            Months = months,
            GeneratedAt = SiteTime.Now,
            Factors = factors,
            Options = opts,
            HasEnergyData = rows.Count > 0,
            Energy = E(monthStart),
            Economics = X(monthStart),
            PrevEnergy = E(monthStart.AddMonths(-1)),
            PrevEconomics = X(monthStart.AddMonths(-1)),
            LastYearEnergy = E(monthStart.AddYears(-1)),
            LastYearEconomics = X(monthStart.AddYears(-1)),
            DailyKwh = daily,
            Chart = DailyChart.Build(year, mon, daily, partial ? today : null),
            OutagesCovered = outagesCovered,
            OutagesFrom = outagesFrom,
            Outages = outages,
            Battery = battery,
            BatteryAt = batteryAt,
            BatteryDays = batteryDays,
            OfflineDays = offlineDays,
            AlertCount = alertCount,
            CleaningsDone = cleanings,
            WorkOrdersCompleted = workOrders,
        };
    }

    /// <summary>
    /// The first stored result dated on or after <paramref name="monthEndExclusive"/> — it looked back over
    /// the month — or, for the running month or when none is that late, the latest one.
    /// </summary>
    private static async Task<(T? Value, DateTime? ComputedAt)> SnapshotCoveringAsync<T>(ApplicationDbContext db,
        SiteAnalyticsService analytics, int siteId, string kind, DateTime monthEndExclusive, CancellationToken ct) where T : class
    {
        var row = await db.SiteAnalyticsDaily.AsNoTracking()
            .Where(a => a.InstallationId == siteId && a.Kind == kind && a.Date >= monthEndExclusive)
            .OrderBy(a => a.Date).ThenByDescending(a => a.ComputedAt)
            .FirstOrDefaultAsync(ct);
        if (row == null) return await analytics.LatestAsync<T>(siteId, kind, ct);
        try { return (AnalyticsJson.Deserialize<T>(row.Json), row.ComputedAt); }
        catch { return await analytics.LatestAsync<T>(siteId, kind, ct); }
    }
}
