using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services;
using SolarApp.Services.Analytics;
using SolarApp.Services.Telemetry;
using Staff = SolarApp.Pages.Installations.AnalyticsModel;

namespace SolarApp.Pages.My;

/// <summary>
/// Owner-facing analytics (DESIGN.md §8, last paragraph): savings, payback,
/// self-sufficiency, CO₂, year-on-year, outages, battery summary and the best
/// hours to run appliances. A separate page rather than a section of
/// /My/Site: that page polls live power every 30 s and is the quick "is it
/// working" view, while this one reads the stored analytics once.
///
/// Sections follow the same SiteCapabilities rules as the staff page.
/// Savings / CO₂ / year-on-year come from Daily EnergyData, so cloud-only
/// sites get them too; everything else needs telemetry and a stored report.
/// Read only: no tariff editing, no recompute.
///
/// Authorisation is the same as /My/Site: the signed-in user's linked
/// Customer must own the installation, otherwise 404.
/// </summary>
[Authorize]
public class AnalyticsModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SiteAnalyticsService _analytics;

    public AnalyticsModel(ApplicationDbContext context, UserManager<ApplicationUser> userManager, SiteAnalyticsService analytics)
    {
        _context = context;
        _userManager = userManager;
        _analytics = analytics;
    }

    /// <summary>One row of the year-on-year table; last-year values are null when that month has no data.</summary>
    public sealed record YoyRow(int Year, int Month, bool Partial,
        double PvKwh, double? PvLastYearKwh, double? PvYoYPct,
        double SavingsThb, double? SavingsLastYearThb, double? SavingsYoYPct);

    public Installation Site { get; private set; } = null!;
    public SiteCapabilities Caps { get; private set; } = null!;

    // Savings (EnergyData — any source)
    public bool HasEnergyData { get; private set; }
    public List<MonthlyEconomics> Months { get; private set; } = new();
    public List<YoyRow> YearOnYear { get; private set; } = new();
    public EconomicsResult? Lifetime { get; private set; }
    public EconomicsResult? Last30 { get; private set; }
    public double? CumulativeSavingsThb => Months.Count > 0 ? Months[^1].CumulativeSavingsThb : null;
    public double? PaybackPct => Months.Count > 0 ? Months[^1].CumulativePaybackPct : null;

    // Stored analytics (telemetry sites)
    public GridSnapshot? Grid { get; private set; }
    public BatteryReport? Battery { get; private set; }
    public HistorySeries History { get; private set; } = new();
    public int HistoryDays { get; private set; }
    public List<CurtailmentHour> BestHours { get; private set; } = new();
    public DateTime? UpdatedAt { get; private set; }

    /// <summary>Grid status or voltage is measured, so outages can be detected.</summary>
    public bool ShowOutages => Caps.HasTelemetry && (Caps.HasGridStatus || Caps.HasGridVoltage);
    /// <summary>The site has a battery we receive data from (a cloud-only battery site has nothing to show).</summary>
    public bool ShowBattery => Caps.HasTelemetry && Caps.HasBattery;

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Forbid();

        var customer = await _context.Customers
            .Where(c => c.UserId == user.Id)
            .Select(c => new { c.Id })
            .FirstOrDefaultAsync();
        if (customer == null) return NotFound();

        var site = await _context.Installations.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == id && i.CustomerId == customer.Id);
        if (site == null) return NotFound();
        Site = site;

        var channels = await _context.TelemetryChannels.AsNoTracking().Where(c => c.InstallationId == id).ToListAsync();
        Caps = SiteCapabilities.From(site, channels);

        await LoadSavingsAsync(site);

        if (Caps.HasTelemetry)
        {
            var stamps = new List<DateTime?>();
            if (ShowOutages)
            {
                (Grid, var at) = await _analytics.LatestAsync<GridSnapshot>(id, SiteAnalyticsKinds.Grid);
                stamps.Add(at);
            }
            if (ShowBattery)
            {
                (Battery, var at) = await _analytics.LatestAsync<BatteryReport>(id, SiteAnalyticsKinds.Battery);
                stamps.Add(at);
                // The whole stored history: BatteryHistory switches to weekly bars past 365 days.
                var days = await _analytics.BatteryDaysAsync(id);
                HistoryDays = days.Count;
                History = BatteryHistory.Buckets(days.Select(d =>
                    new DailyHistoryPoint(d.Date, d.ChargedKwh, d.DischargedKwh, d.SocMin, d.SocMax, d.HadOutage)).ToList());
            }
            var (curtailment, cat) = await _analytics.LatestAsync<CurtailmentReport>(id, SiteAnalyticsKinds.Curtailment);
            if (curtailment is { Available: true })
            {
                BestHours = curtailment.TopHours;
                stamps.Add(cat);
            }
            UpdatedAt = stamps.Max();
        }

        return Page();
    }

    private async Task LoadSavingsAsync(Installation site)
    {
        var rows = await _context.EnergyData.AsNoTracking()
            .Where(e => e.InstallationId == site.Id && e.Granularity == DataGranularity.Daily)
            .Select(e => new { e.Timestamp, e.EnergyGenerationKwh, e.GridImportKwh, e.GridExportKwh })
            .ToListAsync();
        HasEnergyData = rows.Count > 0;
        if (!HasEnergyData) return;

        var opts = Staff.EconomicsOptionsFor(site);
        var months = rows
            .GroupBy(r => (r.Timestamp.Year, r.Timestamp.Month))
            .OrderBy(g => g.Key)
            .Select(g => new MonthlyEnergy(g.Key.Year, g.Key.Month,
                (double)g.Sum(r => r.EnergyGenerationKwh), (double)g.Sum(r => r.GridImportKwh), (double)g.Sum(r => r.GridExportKwh)))
            .ToList();
        Months = EconomicsCalculator.Monthly(months, opts);
        YearOnYear = BuildYearOnYear(Months, months, SiteTime.Today);

        Lifetime = EconomicsCalculator.Calculate(new EnergyTotals(
            (double)rows.Sum(r => r.EnergyGenerationKwh), (double)rows.Sum(r => r.GridImportKwh), (double)rows.Sum(r => r.GridExportKwh)),
            opts, CumulativeSavingsThb);
        var since = SiteTime.Today.AddDays(-29);
        var recent = rows.Where(r => r.Timestamp.Date >= since).ToList();
        if (recent.Count > 0)
            Last30 = EconomicsCalculator.Calculate(new EnergyTotals(
                (double)recent.Sum(r => r.EnergyGenerationKwh), (double)recent.Sum(r => r.GridImportKwh),
                (double)recent.Sum(r => r.GridExportKwh)), opts);
    }

    /// <summary>Newest first, at most 24 months, each with the same month a year earlier.</summary>
    public static List<YoyRow> BuildYearOnYear(IReadOnlyList<MonthlyEconomics> economics, IReadOnlyList<MonthlyEnergy> energy, DateTime today)
    {
        var pv = energy.ToDictionary(m => (m.Year, m.Month), m => m.PvKwh);
        var savings = economics.ToDictionary(m => (m.Year, m.Month), m => m.Result.SavingsThb);
        return Enumerable.Reverse(economics).Take(24).Select(m =>
        {
            var prev = (m.Year - 1, m.Month);
            return new YoyRow(m.Year, m.Month, m.Year == today.Year && m.Month == today.Month,
                pv.GetValueOrDefault((m.Year, m.Month)), pv.TryGetValue(prev, out var lp) ? lp : null, m.PvYoYPct,
                m.Result.SavingsThb, savings.TryGetValue(prev, out var ls) ? ls : null, m.SavingsYoYPct);
        }).ToList();
    }

    // ── view helpers (owner wording) ─────────────────────────────────────

    public static string MonthLabel(int year, int month, string format = "MMM yy") =>
        new DateTime(year, month, 1).ToString(format, System.Globalization.CultureInfo.GetCultureInfo("th-TH"));

    public static string GradeSentence(string? grade) => grade switch
    {
        "A" => "แบตเตอรี่อยู่ในสภาพดีมาก ใช้งานได้ตามปกติ",
        "B" => "แบตเตอรี่ใช้งานได้ปกติ ระบบกำลังเฝ้าดูให้อย่างต่อเนื่อง",
        "C" => "พบสิ่งที่ควรตรวจสอบ ทีมช่างจะติดต่อกลับเพื่อดูแลให้",
        _ => "ยังมีข้อมูลไม่พอสำหรับประเมิน",
    };
}
