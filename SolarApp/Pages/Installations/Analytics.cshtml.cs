using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Models.Telemetry;
using SolarApp.Services;
using SolarApp.Services.Analytics;
using SolarApp.Services.Authorization;
using SolarApp.Services.Telemetry;

namespace SolarApp.Pages.Installations;

/// <summary>
/// Site analytics (DESIGN.md §8). Tabs appear only for devices the site has
/// (SiteCapabilities); a hidden tab is listed with the one-line reason. The
/// page reads results stored by SiteAnalyticsJob and can recompute one site
/// on demand. Overview and savings come from EnergyData, so cloud-only sites
/// get them too.
/// </summary>
[Authorize(Roles = "Admin,Partner")]
public class AnalyticsModel : PageModel
{
    private readonly ApplicationDbContext _db;
    private readonly IInstallationAccessHelper _access;
    private readonly SiteAnalyticsService _analytics;
    private readonly ILogger<AnalyticsModel> _logger;

    public AnalyticsModel(ApplicationDbContext db, IInstallationAccessHelper access, SiteAnalyticsService analytics,
        ILogger<AnalyticsModel> logger)
    {
        _db = db;
        _access = access;
        _analytics = analytics;
        _logger = logger;
    }

    public sealed record Tab(string Key, string Title, string Icon, bool Available, string? Reason);

    public Installation Installation { get; private set; } = null!;
    public SiteCapabilities Caps { get; private set; } = null!;
    public List<Tab> Tabs { get; } = new();
    public string ActiveTab { get; private set; } = "overview";
    public string Range { get; private set; } = "365";

    // Overview (EnergyData — any source)
    public List<MonthlyEconomics> Months { get; private set; } = new();
    public Dictionary<(int Year, int Month), MonthlyEnergy> MonthEnergy { get; private set; } = new();
    public EconomicsResult? Lifetime { get; private set; }
    public EconomicsResult? Last30 { get; private set; }
    public bool HasEnergyData { get; private set; }
    public EnvironmentalFactors Factors { get; private set; } = EnvironmentalFactors.Default;
    public EnergyFlow? Flow30 { get; private set; }

    /// <summary>Last-30-day energy flows for the Sankey, kWh. Battery terms are 0 without a battery.</summary>
    public sealed record EnergyFlow(double PvToHome, double PvToBattery, double PvToGrid, double GridToHome, double BatteryToHome)
    {
        public double Total => PvToHome + PvToBattery + PvToGrid + GridToHome + BatteryToHome;
    }

    // Stored analytics
    public BatteryReport? Battery { get; private set; }
    public DateTime? BatteryAt { get; private set; }
    public HistorySeries History { get; private set; } = new();
    public int HistoryDays { get; private set; }
    public PvSnapshot? Pv { get; private set; }
    public CurtailmentReport? Curtailment { get; private set; }
    public StringComparisonReport? Strings { get; private set; }
    public GridSnapshot? Grid { get; private set; }
    public ThermalReport? Thermal { get; private set; }
    public DataQualityReport? Quality { get; private set; }
    public TouSnapshot? Tou { get; private set; }
    public DateTime? LastComputedAt { get; private set; }
    public List<DeviceEvent> Events { get; private set; } = new();
    public List<TariffPlan> TariffPlans { get; private set; } = new();

    // Per-cell battery view (battery tab; only for sites with battery.pack{n}.cell{m}.voltage_v)
    public const int CellHistoryDays = 7;
    public List<CellPackView> CellPacks { get; private set; } = new();
    /// <summary>The pack whose history is shown (?pack=n), defaulting to the first.</summary>
    public int? CellPack { get; private set; }
    public CellHistoryReport? CellHistory { get; private set; }
    public bool HasCellVoltages => CellPacks.Count > 0;
    private List<TelemetryChannel> _channels = new();

    [BindProperty] public TariffInput Tariff { get; set; } = new();
    [BindProperty] public WarrantyInput Warranty { get; set; } = new();

    public sealed class TariffInput
    {
        public int? Id { get; set; }
        public string Name { get; set; } = "";
        public TariffKind Kind { get; set; } = TariffKind.Progressive;
        public bool IsCurrent { get; set; }
        public double FlatRate { get; set; }
        /// <summary>"150:3.2484, 400:4.2218, :4.4217" — kWh up to (cumulative) : rate; empty limit = the rest.</summary>
        public string Tiers { get; set; } = "";
        public double PeakRate { get; set; }
        public double OffPeakRate { get; set; }
        public int PeakStartHour { get; set; } = 9;
        public int PeakEndHour { get; set; } = 22;
        public double FtPerKwh { get; set; }
        public double ServiceChargePerMonth { get; set; }
        public double VatPct { get; set; } = 7;
    }

    public sealed class WarrantyInput
    {
        public int? Years { get; set; }
        public decimal? ThroughputMwh { get; set; }
        public decimal? MinCapacityPct { get; set; }
    }

    public static readonly string[] Ranges = { "30", "90", "365", "all" };

    // ── GET ────────────────────────────────────────────────────────────

    public async Task<IActionResult> OnGetAsync(int id, string? tab = null, string? range = null, int? editTariff = null,
        int? pack = null)
    {
        if (!await _access.CanAccessAsync(id, User)) return Forbid();
        if (!await LoadAsync(id, tab, range)) return NotFound();
        if (ActiveTab == "battery") await LoadCellsAsync(pack, DateTime.UtcNow, HttpContext?.RequestAborted ?? default);

        if (editTariff is int tid && TariffPlans.FirstOrDefault(p => p.Id == tid) is { } plan)
            Tariff = ToInput(plan);
        return Page();
    }

    private async Task<bool> LoadAsync(int id, string? tab, string? range)
    {
        var inst = await _db.Installations.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id);
        if (inst == null) return false;
        Installation = inst;
        var channels = await _db.TelemetryChannels.AsNoTracking().Where(c => c.InstallationId == id).ToListAsync();
        _channels = channels;
        Caps = SiteCapabilities.From(inst, channels);
        Range = Ranges.Contains(range) ? range! : "365";

        await LoadOverviewAsync(inst);

        if (Caps.HasTelemetry)
        {
            (Battery, BatteryAt) = await _analytics.LatestAsync<BatteryReport>(id, SiteAnalyticsKinds.Battery);
            (Pv, _) = await _analytics.LatestAsync<PvSnapshot>(id, SiteAnalyticsKinds.Pv);
            (Curtailment, _) = await _analytics.LatestAsync<CurtailmentReport>(id, SiteAnalyticsKinds.Curtailment);
            (Strings, _) = await _analytics.LatestAsync<StringComparisonReport>(id, SiteAnalyticsKinds.Strings);
            (Grid, _) = await _analytics.LatestAsync<GridSnapshot>(id, SiteAnalyticsKinds.Grid);
            (Thermal, _) = await _analytics.LatestAsync<ThermalReport>(id, SiteAnalyticsKinds.Thermal);
            (Quality, _) = await _analytics.LatestAsync<DataQualityReport>(id, SiteAnalyticsKinds.Quality);
            (Tou, _) = await _analytics.LatestAsync<TouSnapshot>(id, SiteAnalyticsKinds.Tou);
            LastComputedAt = await _db.SiteAnalyticsDaily.AsNoTracking().Where(a => a.InstallationId == id)
                .MaxAsync(a => (DateTime?)a.ComputedAt);

            if (Caps.HasBattery)
            {
                DateOnly? from = Range == "all" ? null
                    : DateOnly.FromDateTime(SiteTime.Today).AddDays(-int.Parse(Range, CultureInfo.InvariantCulture) + 1);
                var days = await _analytics.BatteryDaysAsync(id, from);
                HistoryDays = days.Count;
                History = BatteryHistory.Buckets(days.Select(d =>
                    new DailyHistoryPoint(d.Date, d.ChargedKwh, d.DischargedKwh, d.SocMin, d.SocMax, d.HadOutage)).ToList());
            }
        }

        if (Caps.HasStatusCodes || await _db.DeviceEvents.AnyAsync(e => e.InstallationId == id))
            Events = await _db.DeviceEvents.AsNoTracking().Where(e => e.InstallationId == id)
                .OrderByDescending(e => e.TsUtc).Take(200).ToListAsync();

        TariffPlans = await _db.TariffPlans.AsNoTracking().Where(p => p.InstallationId == id)
            .OrderByDescending(p => p.IsCurrent).ThenBy(p => p.Id).ToListAsync();

        Warranty = new WarrantyInput
        {
            Years = inst.BatteryWarrantyYears,
            ThroughputMwh = inst.BatteryWarrantyThroughputMwh,
            MinCapacityPct = inst.BatteryWarrantyMinCapacityPct,
        };

        BuildTabs();
        ActiveTab = Tabs.Any(t => t.Key == tab && t.Available) ? tab! : "overview";
        return true;
    }

    private void BuildTabs()
    {
        var c = Caps;
        string? NoTelemetry() => c.HasTelemetry ? null : "ต้องเชื่อมต่อ Home Assistant / Local API ก่อน";
        Tabs.Add(new("overview", "ภาพรวม", "bi-grid-1x2", true, null));
        Tabs.Add(new("battery", "แบตเตอรี่", "bi-battery-half", c.HasBattery && c.HasTelemetry,
            !c.HasBattery ? "ไม่มีแบตเตอรี่ในระบบนี้" : NoTelemetry()));
        Tabs.Add(new("pv", "PV & สตริง", "bi-sun", c.HasPv && c.HasTelemetry,
            !c.HasPv ? "ไม่มีข้อมูลกำลังผลิต PV" : NoTelemetry()));
        Tabs.Add(new("tariff", "ค่าไฟ & TOU", "bi-receipt", c.HasGridMeter,
            c.HasGridMeter ? null : "ต้องมีมิเตอร์กริด (grid.power_w หรือ grid.import_total_kwh)"));
        var hasPowerTab = c.HasGridVoltage || c.HasGridStatus || c.HasInverterTemps || c.HasBatteryTemps;
        Tabs.Add(new("power", "ไฟฟ้า & ความร้อน", "bi-thermometer-half", hasPowerTab,
            hasPowerTab ? null : "ไม่มีแรงดันกริด สถานะกริด หรืออุณหภูมิอุปกรณ์"));
        Tabs.Add(new("quality", "คุณภาพข้อมูล", "bi-clipboard-data", c.HasTelemetry, NoTelemetry()));
        var hasEvents = c.HasStatusCodes || Events.Count > 0;
        Tabs.Add(new("events", "เหตุการณ์", "bi-list-ul", hasEvents,
            hasEvents ? null : "อุปกรณ์ไม่ได้ส่งรหัสสถานะ/alarm"));
    }

    private async Task LoadOverviewAsync(Installation inst)
    {
        var rows = await _db.EnergyData.AsNoTracking()
            .Where(e => e.InstallationId == inst.Id && e.Granularity == DataGranularity.Daily)
            .Select(e => new { e.Timestamp, e.EnergyGenerationKwh, e.GridImportKwh, e.GridExportKwh, e.BatteryChargeKwh, e.BatteryDischargeKwh })
            .ToListAsync();
        HasEnergyData = rows.Count > 0;
        if (!HasEnergyData) return;

        Factors = await EnvironmentalFactors.LoadAsync(_db);
        var opts = Factors.Apply(EconomicsOptionsFor(inst));
        var months = rows
            .GroupBy(r => (r.Timestamp.Year, r.Timestamp.Month))
            .OrderBy(g => g.Key)
            .Select(g => new MonthlyEnergy(g.Key.Year, g.Key.Month,
                (double)g.Sum(r => r.EnergyGenerationKwh), (double)g.Sum(r => r.GridImportKwh), (double)g.Sum(r => r.GridExportKwh)))
            .ToList();
        Months = EconomicsCalculator.Monthly(months, opts);
        MonthEnergy = months.ToDictionary(m => (m.Year, m.Month));

        var cumulative = Months.Count > 0 ? Months[^1].CumulativeSavingsThb : (double?)null;
        Lifetime = EconomicsCalculator.Calculate(new EnergyTotals(
            (double)rows.Sum(r => r.EnergyGenerationKwh), (double)rows.Sum(r => r.GridImportKwh), (double)rows.Sum(r => r.GridExportKwh)),
            opts, cumulative);
        var since = SiteTime.Today.AddDays(-29);
        var recent = rows.Where(r => r.Timestamp.Date >= since).ToList();
        if (recent.Count > 0)
            Last30 = EconomicsCalculator.Calculate(new EnergyTotals(
                (double)recent.Sum(r => r.EnergyGenerationKwh), (double)recent.Sum(r => r.GridImportKwh),
                (double)recent.Sum(r => r.GridExportKwh)), opts);
        if (recent.Count > 0)
        {
            double pv = (double)recent.Sum(r => r.EnergyGenerationKwh), imp = (double)recent.Sum(r => r.GridImportKwh),
                exp = (double)recent.Sum(r => r.GridExportKwh);
            double chg = inst.HasBattery ? (double)recent.Sum(r => r.BatteryChargeKwh ?? 0) : 0;
            double dis = inst.HasBattery ? (double)recent.Sum(r => r.BatteryDischargeKwh ?? 0) : 0;
            var toGrid = Math.Min(exp, pv);
            var toBattery = Math.Min(chg, Math.Max(0, pv - toGrid));
            Flow30 = new EnergyFlow(Math.Max(0, pv - toGrid - toBattery), toBattery, toGrid, imp, dis);
        }
    }

    /// <summary>
    /// Latest cell voltages of every pack from the channel catalog, and for the
    /// selected pack ΔV per 5-minute bucket over the last 7 days plus the cells
    /// most often lowest / highest. Nothing is loaded without per-cell channels.
    /// </summary>
    private async Task LoadCellsAsync(int? pack, DateTime nowUtc, CancellationToken ct)
    {
        var cells = _channels.Where(c => c.Kind == TelemetryDeviceKind.BatteryCell && c.Quantity == TelemetryQuantity.Voltage
                                         && c.DeviceIndex > 0 && c.SubIndex > 0).ToList();
        if (cells.Count == 0) return;

        // Current SOC / power for grading ΔV: the pack's own, else the system's; only when fresh.
        double? Latest(TelemetryDeviceKind kind, int index, TelemetryQuantity q) =>
            _channels.Where(c => c.Kind == kind && c.DeviceIndex == index && c.Quantity == q && !c.IsCumulative
                                 && !BatteryCellAnalyzer.IsStale(c.LastTs, nowUtc))
                .OrderByDescending(c => c.LastTs).Select(c => (double?)c.LastValue).FirstOrDefault();

        foreach (var g in cells.GroupBy(c => c.DeviceIndex).OrderBy(g => g.Key))
        {
            var soc = Latest(TelemetryDeviceKind.BatteryPack, g.Key, TelemetryQuantity.Soc)
                      ?? Latest(TelemetryDeviceKind.Battery, 0, TelemetryQuantity.Soc);
            var power = Latest(TelemetryDeviceKind.BatteryPack, g.Key, TelemetryQuantity.Power);
            var view = BatteryCellAnalyzer.Pack(g.Key, g.Select(c => new CellReading(c.SubIndex, c.LastValue, c.LastTs)),
                nowUtc, soc, power);
            if (view != null) CellPacks.Add(view);
        }
        if (CellPacks.Count == 0) return;

        CellPack = pack is int p && CellPacks.Any(x => x.Pack == p) ? p : CellPacks[0].Pack;
        var packChannels = cells.Where(c => c.DeviceIndex == CellPack).ToList();
        var series = await _analytics.ReadSeriesAsync(packChannels, nowUtc.AddDays(-CellHistoryDays), nowUtc, ct);
        var byCell = packChannels
            .GroupBy(c => c.SubIndex)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Point>)g
                .SelectMany(c => series.TryGetValue(c.Key, out var s) ? s.Points : Array.Empty<Point>()).ToList());
        CellHistory = BatteryCellAnalyzer.History(CellPack.Value, byCell);
    }

    public static string CellClass(KpiGrade g) => g switch
    {
        KpiGrade.Watch => "watch",
        KpiGrade.Poor => "poor",
        _ => "",
    };

    public static EconomicsOptions EconomicsOptionsFor(Installation inst) => new()
    {
        ImportRateThb = inst.ElectricityRatePerKwh > 0 ? (double)inst.ElectricityRatePerKwh : 4.5,
        FeedInRateThb = inst.FeedInTariffRate is > 0 ? (double)inst.FeedInTariffRate.Value : 0,
        SystemPriceThb = inst.ContractValue is > 0 ? (double)inst.ContractValue.Value : null,
    };

    // ── POST: recompute ────────────────────────────────────────────────

    public async Task<IActionResult> OnPostRecomputeAsync(int id, string? tab)
    {
        if (!await _access.CanAccessAsync(id, User)) return Forbid();
        try
        {
            var r = await _analytics.ComputeSiteAsync(id, DateTime.UtcNow, HttpContext.RequestAborted);
            _logger.LogInformation("Analytics recomputed by {User}: {Result}", User.Identity?.Name, r);
            if (r.Errors.Count > 0)
                TempData["FlashWarning"] = "คำนวณเสร็จบางส่วน ส่วนที่ผิดพลาด: " + string.Join(" · ", r.Errors);
            else if (r.Computed.Count == 0)
                TempData["FlashWarning"] = "ยังคำนวณไม่ได้: " + string.Join(" · ", r.Skipped);
            else
                TempData["FlashSuccess"] = $"คำนวณใหม่แล้ว {r.Computed.Count} ส่วน";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Analytics recompute failed for site {Id}", id);
            TempData["FlashWarning"] = "คำนวณไม่สำเร็จ: " + ex.Message;
        }
        return RedirectToPage(new { id, tab });
    }

    // ── POST: battery warranty ─────────────────────────────────────────

    public async Task<IActionResult> OnPostWarrantyAsync(int id)
    {
        if (!await _access.CanAccessAsync(id, User)) return Forbid();
        var inst = await _db.Installations.FirstOrDefaultAsync(i => i.Id == id);
        if (inst == null) return NotFound();
        if (Warranty.Years is < 0 or > 30 || Warranty.ThroughputMwh is < 0 or > 10000 || Warranty.MinCapacityPct is < 0 or > 100)
        {
            TempData["FlashWarning"] = "ค่าประกันไม่ถูกต้อง (ปี 0–30, MWh 0–10000, ความจุขั้นต่ำ 0–100 %)";
            return RedirectToPage(new { id, tab = "battery" });
        }
        inst.BatteryWarrantyYears = Warranty.Years;
        inst.BatteryWarrantyThroughputMwh = Warranty.ThroughputMwh;
        inst.BatteryWarrantyMinCapacityPct = Warranty.MinCapacityPct;
        await _db.SaveChangesAsync();
        TempData["FlashSuccess"] = "บันทึกเงื่อนไขประกันแบตเตอรี่แล้ว — กด \"คำนวณใหม่\" เพื่ออัปเดตผล";
        return RedirectToPage(new { id, tab = "battery" });
    }

    // ── POST: tariff plans ─────────────────────────────────────────────

    public async Task<IActionResult> OnPostSeedTariffsAsync(int id)
    {
        if (!await _access.CanAccessAsync(id, User)) return Forbid();
        if (!await _db.Installations.AnyAsync(i => i.Id == id)) return NotFound();
        if (await _db.TariffPlans.AnyAsync(p => p.InstallationId == id))
        {
            TempData["FlashWarning"] = "ไซต์นี้มีแผนค่าไฟอยู่แล้ว";
            return RedirectToPage(new { id, tab = "tariff" });
        }
        var progressive = TariffPresets.PeaResidentialProgressive();
        var tou = TariffPresets.PeaResidentialTou();
        _db.TariffPlans.Add(new TariffPlan { InstallationId = id, Name = progressive.Name, Kind = progressive.Kind, IsCurrent = true, ConfigJson = progressive.ToJson() });
        _db.TariffPlans.Add(new TariffPlan { InstallationId = id, Name = tou.Name, Kind = tou.Kind, IsCurrent = false, ConfigJson = tou.ToJson() });
        await _db.SaveChangesAsync();
        TempData["FlashSuccess"] = "เพิ่มแผนค่าไฟเริ่มต้นของ กฟภ. แล้ว — โปรดตรวจสอบอัตราและ Ft ให้ตรงประกาศล่าสุด";
        return RedirectToPage(new { id, tab = "tariff" });
    }

    public async Task<IActionResult> OnPostSaveTariffAsync(int id)
    {
        if (!await _access.CanAccessAsync(id, User)) return Forbid();
        if (!await _db.Installations.AnyAsync(i => i.Id == id)) return NotFound();

        var (config, error) = BuildConfig(Tariff);
        if (config == null)
        {
            TempData["FlashWarning"] = error;
            return RedirectToPage(new { id, tab = "tariff", editTariff = Tariff.Id });
        }

        TariffPlan? plan = null;
        if (Tariff.Id is int pid)
        {
            plan = await _db.TariffPlans.FirstOrDefaultAsync(p => p.Id == pid && p.InstallationId == id);
            if (plan == null) return NotFound();
        }
        else
        {
            plan = new TariffPlan { InstallationId = id };
            _db.TariffPlans.Add(plan);
        }
        plan.Name = config.Name;
        plan.Kind = config.Kind;
        plan.ConfigJson = config.ToJson();
        plan.UpdatedAt = DateTime.UtcNow;
        plan.IsCurrent = Tariff.IsCurrent;

        if (Tariff.IsCurrent)
            await foreach (var other in _db.TariffPlans.Where(p => p.InstallationId == id && p.Id != plan.Id && p.IsCurrent).AsAsyncEnumerable())
                other.IsCurrent = false;

        await _db.SaveChangesAsync();
        TempData["FlashSuccess"] = $"บันทึกแผน \"{config.Name}\" แล้ว — กด \"คำนวณใหม่\" เพื่อเทียบค่าไฟอีกครั้ง";
        return RedirectToPage(new { id, tab = "tariff" });
    }

    public async Task<IActionResult> OnPostDeleteTariffAsync(int id, int planId)
    {
        if (!await _access.CanAccessAsync(id, User)) return Forbid();
        var plan = await _db.TariffPlans.FirstOrDefaultAsync(p => p.Id == planId && p.InstallationId == id);
        if (plan == null) return NotFound();
        _db.TariffPlans.Remove(plan);
        await _db.SaveChangesAsync();
        TempData["FlashSuccess"] = $"ลบแผน \"{plan.Name}\" แล้ว";
        return RedirectToPage(new { id, tab = "tariff" });
    }

    /// <summary>Validate the form and build a TariffConfig; returns (null, Thai message) on bad input.</summary>
    public static (TariffConfig? Config, string? Error) BuildConfig(TariffInput t)
    {
        var name = (t.Name ?? "").Trim();
        if (name.Length == 0) return (null, "กรุณาตั้งชื่อแผนค่าไฟ");
        if (name.Length > 100) name = name[..100];
        if (t.VatPct is < 0 or > 30 || t.FtPerKwh is < -5 or > 5 || t.ServiceChargePerMonth is < 0 or > 10000)
            return (null, "VAT, Ft หรือค่าบริการไม่ถูกต้อง");

        switch (t.Kind)
        {
            case TariffKind.Flat:
                if (t.FlatRate is <= 0 or > 50) return (null, "อัตราค่าไฟต้องอยู่ระหว่าง 0–50 บาท/kWh");
                return (new TariffConfig
                {
                    Name = name, Kind = TariffKind.Flat, FlatRate = t.FlatRate,
                    FtPerKwh = t.FtPerKwh, ServiceChargePerMonth = t.ServiceChargePerMonth, VatPct = t.VatPct,
                }, null);

            case TariffKind.Progressive:
                var tiers = ParseTiers(t.Tiers);
                if (tiers == null) return (null, "รูปแบบขั้นบันไดไม่ถูกต้อง ตัวอย่าง: 150:3.2484, 400:4.2218, :4.4217");
                return (new TariffConfig
                {
                    Name = name, Kind = TariffKind.Progressive, Tiers = tiers,
                    FtPerKwh = t.FtPerKwh, ServiceChargePerMonth = t.ServiceChargePerMonth, VatPct = t.VatPct,
                }, null);

            case TariffKind.TimeOfUse:
                if (t.PeakRate is <= 0 or > 50 || t.OffPeakRate is <= 0 or > 50)
                    return (null, "อัตรา Peak / Off-peak ต้องอยู่ระหว่าง 0–50 บาท/kWh");
                if (t.PeakStartHour is < 0 or > 23 || t.PeakEndHour is < 1 or > 24 || t.PeakEndHour <= t.PeakStartHour)
                    return (null, "ช่วงเวลา Peak ไม่ถูกต้อง (เริ่ม < สิ้นสุด, 0–24 น.)");
                return (new TariffConfig
                {
                    Name = name, Kind = TariffKind.TimeOfUse, PeakRate = t.PeakRate, OffPeakRate = t.OffPeakRate,
                    PeakStartHour = t.PeakStartHour, PeakEndHour = t.PeakEndHour,
                    FtPerKwh = t.FtPerKwh, ServiceChargePerMonth = t.ServiceChargePerMonth, VatPct = t.VatPct,
                }, null);

            default:
                return (null, "ประเภทแผนไม่ถูกต้อง");
        }
    }

    /// <summary>"150:3.2484, 400:4.2218, :4.4217" → tiers; limits must rise and only the last may be open.</summary>
    public static List<TariffTier>? ParseTiers(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var tiers = new List<TariffTier>();
        double last = 0;
        var parts = text.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var kv = parts[i].Split(':', StringSplitOptions.TrimEntries);
            if (kv.Length != 2) return null;
            if (!double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) || rate <= 0 || rate > 50)
                return null;
            double? upTo = null;
            if (kv[0].Length > 0)
            {
                if (!double.TryParse(kv[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var u) || u <= last) return null;
                upTo = u;
                last = u;
            }
            else if (i != parts.Length - 1) return null;
            tiers.Add(new TariffTier(upTo, rate));
        }
        if (tiers.Count == 0) return null;
        if (tiers[^1].UpToKwh != null) tiers.Add(new TariffTier(null, tiers[^1].Rate));
        return tiers;
    }

    public static string FormatTiers(IEnumerable<TariffTier> tiers) =>
        string.Join(", ", tiers.Select(t =>
            (t.UpToKwh?.ToString(CultureInfo.InvariantCulture) ?? "") + ":" + t.Rate.ToString(CultureInfo.InvariantCulture)));

    public static TariffConfig? ParseConfig(TariffPlan p)
    {
        try { return TariffConfig.FromJson(p.ConfigJson); }
        catch { return null; }
    }

    private static TariffInput ToInput(TariffPlan p)
    {
        var c = ParseConfig(p) ?? new TariffConfig { Name = p.Name, Kind = p.Kind };
        return new TariffInput
        {
            Id = p.Id, Name = c.Name, Kind = c.Kind, IsCurrent = p.IsCurrent, FlatRate = c.FlatRate,
            Tiers = FormatTiers(c.Tiers), PeakRate = c.PeakRate, OffPeakRate = c.OffPeakRate,
            PeakStartHour = c.PeakStartHour, PeakEndHour = c.PeakEndHour, FtPerKwh = c.FtPerKwh,
            ServiceChargePerMonth = c.ServiceChargePerMonth, VatPct = c.VatPct,
        };
    }

    // ── view helpers ───────────────────────────────────────────────────

    public static string GradeClass(KpiGrade g) => g switch
    {
        KpiGrade.Good => "success",
        KpiGrade.Watch => "warning",
        KpiGrade.Poor => "danger",
        _ => "secondary",
    };

    public static string EfficiencyClass(EfficiencyGrade? g) => g switch
    {
        EfficiencyGrade.Good => "success",
        EfficiencyGrade.Watch => "warning",
        EfficiencyGrade.Poor => "danger",
        EfficiencyGrade.DataError => "dark",
        _ => "secondary",
    };

    public static string EfficiencyText(EfficiencyGrade? g) => g switch
    {
        EfficiencyGrade.Good => "ดี (≥ 92 %)",
        EfficiencyGrade.Watch => "เฝ้าระวัง (88–92 %)",
        EfficiencyGrade.Poor => "ต่ำ (< 88 %)",
        EfficiencyGrade.DataError => "ข้อมูลผิดปกติ (> 100 %)",
        _ => "ยังไม่มีรอบเต็ม→เต็ม",
    };

    public static string OverallClass(string? grade) => grade switch
    {
        "A" => "success",
        "B" => "warning",
        "C" => "danger",
        _ => "secondary",
    };

    public static string StringIssueText(StringIssue i) => i switch
    {
        StringIssue.Soiling => "แผงสกปรก",
        StringIssue.PanelFault => "แผงเสีย",
        StringIssue.LooseConnection => "ขั้วต่อหลวม",
        _ => "ผลิตต่ำกว่าสตริงอื่น",
    };

    public static string SeverityClass(AlertSeverity s) => s switch
    {
        AlertSeverity.Critical => "danger",
        AlertSeverity.High => "danger",
        AlertSeverity.Medium => "warning",
        _ => "secondary",
    };

    public static string Local(DateTime utc, string format = "d MMM yy HH:mm") =>
        SiteTime.ToSiteTime(utc).ToString(format, CultureInfo.GetCultureInfo("th-TH"));

    public static string Day(DateOnly d, string format = "d MMM yy") =>
        d.ToString(format, CultureInfo.GetCultureInfo("th-TH"));

    public static string N(double? v, string format = "N1") =>
        v.HasValue && double.IsFinite(v.Value) ? v.Value.ToString(format, CultureInfo.InvariantCulture) : "–";

    public static string Hours(double minutes) =>
        minutes < 60 ? $"{minutes:0} นาที" : $"{Math.Floor(minutes / 60):0} ชม. {minutes % 60:0} นาที";
}
