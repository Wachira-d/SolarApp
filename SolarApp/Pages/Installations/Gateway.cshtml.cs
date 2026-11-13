using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Models.Telemetry;
using SolarApp.Services.Authorization;
using SolarApp.Services.Telemetry;

namespace SolarApp.Pages.Installations;

/// <summary>
/// A site's local data path (DESIGN.md §4): where its data comes from,
/// the gateways (Home Assistant) allowed to push, what is arriving, how HA
/// entities map to channels, and the YAML to paste into HA.
/// </summary>
[Authorize(Roles = "Admin,Partner")]
public class GatewayModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly IInstallationAccessHelper _access;
    private readonly TelemetryGatewayService _gateways;
    private readonly ITelemetryStore _store;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<GatewayModel> _logger;

    public GatewayModel(ApplicationDbContext context, IInstallationAccessHelper access, TelemetryGatewayService gateways,
        ITelemetryStore store, UserManager<ApplicationUser> userManager, ILogger<GatewayModel> logger)
    {
        _context = context;
        _access = access;
        _gateways = gateways;
        _store = store;
        _userManager = userManager;
        _logger = logger;
    }

    /// <summary>The interval the generated automation pushes at; coverage is measured against it.</summary>
    public const int ExpectedIntervalSeconds = 60;

    public Installation Installation { get; set; } = null!;
    public List<TelemetryGateway> Gateways { get; set; } = new();
    public List<ChannelRow> Channels { get; set; } = new();
    public List<TelemetryEntityMap> Maps { get; set; } = new();
    public HomeAssistantConfig HaConfig { get; set; } = null!;
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Set only on the response to "create gateway" — the one time the token exists in clear.</summary>
    public string? NewToken { get; set; }
    public string? NewTokenGatewayName { get; set; }

    [BindProperty] public MapInput Map { get; set; } = new();

    public sealed class MapInput
    {
        public int? Id { get; set; }
        public string EntityId { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public double Scale { get; set; } = 1;
        public double Offset { get; set; }
        public bool Invert { get; set; }
    }

    public sealed record ChannelRow(
        int Id, string Key, TelemetryDeviceKind Kind, string? Unit, double LastValue, DateTime? LastTs,
        string? SourceEntityId, double? Coverage24hPct)
    {
        public bool IsUnmapped => Key.StartsWith("ha.", StringComparison.Ordinal);
    }

    // ── GET ────────────────────────────────────────────────────────────

    public async Task<IActionResult> OnGetAsync(int id, string? mapEntity = null, int? editMap = null)
    {
        if (!await _access.CanAccessAsync(id, User)) return Forbid();
        if (!await LoadAsync(id)) return NotFound();

        if (editMap is int mid && Maps.FirstOrDefault(m => m.Id == mid) is { } existing)
            Map = new MapInput
            {
                Id = existing.Id, EntityId = existing.EntityId, Key = existing.Key,
                Scale = existing.Scale, Offset = existing.Offset, Invert = existing.Invert,
            };
        else if (!string.IsNullOrWhiteSpace(mapEntity))
            Map = new MapInput { EntityId = mapEntity.Trim() };
        return Page();
    }

    // ── (a) Data source ────────────────────────────────────────────────

    public async Task<IActionResult> OnPostSourceAsync(int id, TelemetrySource source)
    {
        if (!await _access.CanAccessAsync(id, User)) return Forbid();
        var inst = await _context.Installations.FirstOrDefaultAsync(i => i.Id == id);
        if (inst == null) return NotFound();
        if (!Enum.IsDefined(source)) return BadRequest();

        inst.TelemetrySource = source;
        await _context.SaveChangesAsync();
        _logger.LogInformation("Installation {Id} telemetry source set to {Source} by {User}", id, source, User.Identity?.Name);
        TempData["FlashSuccess"] = $"บันทึกแหล่งข้อมูลเป็น \"{SourceLabel(source)}\" แล้ว";
        return RedirectToPage(new { id });
    }

    // ── (b) Gateways ───────────────────────────────────────────────────

    public async Task<IActionResult> OnPostCreateGatewayAsync(int id, string? name)
    {
        if (!await _access.CanAccessAsync(id, User)) return Forbid();
        if (!await _context.Installations.AnyAsync(i => i.Id == id)) return NotFound();

        var (gw, token) = await _gateways.CreateAsync(id, name ?? "Home Assistant", _userManager.GetUserId(User));
        _logger.LogInformation("Gateway {GatewayId} ({Prefix}) created for installation {Id} by {User}",
            gw.Id, gw.TokenPrefix, id, User.Identity?.Name);

        // Rendered straight into this response — never TempData, a cookie or
        // a log — so the token exists in clear exactly once.
        NewToken = token;
        NewTokenGatewayName = gw.Name;
        ModelState.Clear();
        await LoadAsync(id);
        return Page();
    }

    public async Task<IActionResult> OnPostRevokeGatewayAsync(int id, int gatewayId)
    {
        if (!await _access.CanAccessAsync(id, User)) return Forbid();
        var ok = await _gateways.RevokeAsync(gatewayId, id);
        if (ok)
        {
            _logger.LogInformation("Gateway {GatewayId} revoked for installation {Id} by {User}", gatewayId, id, User.Identity?.Name);
            TempData["FlashSuccess"] = "ยกเลิกโทเค็นแล้ว — เกตเวย์นี้จะส่งข้อมูลไม่ได้ภายใน 1 นาที";
        }
        else TempData["FlashWarning"] = "ไม่พบเกตเวย์ หรือถูกยกเลิกไปแล้ว";
        return RedirectToPage(new { id });
    }

    // ── (d) Entity mapping ─────────────────────────────────────────────

    public async Task<IActionResult> OnPostSaveMapAsync(int id)
    {
        if (!await _access.CanAccessAsync(id, User)) return Forbid();
        if (!await _context.Installations.AnyAsync(i => i.Id == id)) return NotFound();

        var entity = (Map.EntityId ?? string.Empty).Trim();
        var key = (Map.Key ?? string.Empty).Trim().ToLowerInvariant();
        if (entity.Length == 0 || entity.Length > 255)
            ModelState.AddModelError("Map.EntityId", "กรอก entity id ของ Home Assistant เช่น sensor.pv_power");
        if (!TelemetryKey.IsValid(key) || key.StartsWith("ha.", StringComparison.Ordinal))
            ModelState.AddModelError("Map.Key", "รหัสช่องข้อมูลไม่ถูกต้อง — เลือกจากรายการ เช่น pv.power_w");
        if (!double.IsFinite(Map.Scale) || Map.Scale == 0)
            ModelState.AddModelError("Map.Scale", "ตัวคูณต้องเป็นตัวเลขที่ไม่ใช่ 0");
        if (!double.IsFinite(Map.Offset))
            ModelState.AddModelError("Map.Offset", "ค่าชดเชยต้องเป็นตัวเลข");

        // One map per entity (unique index): editing another row onto an
        // entity that already has one would fail at SaveChanges.
        var clash = await _context.TelemetryEntityMaps.FirstOrDefaultAsync(m => m.InstallationId == id
            && m.EntityId == entity && m.Id != (Map.Id ?? 0));
        if (clash != null && Map.Id != null)
            ModelState.AddModelError("Map.EntityId", "entity นี้จับคู่ไว้แล้วในอีกรายการ");

        if (!ModelState.IsValid)
        {
            await LoadAsync(id);
            return Page();
        }

        var row = Map.Id is int mid
            ? await _context.TelemetryEntityMaps.FirstOrDefaultAsync(m => m.Id == mid && m.InstallationId == id)
            : clash;   // adding an entity that already exists updates it
        if (row == null)
        {
            row = new TelemetryEntityMap { InstallationId = id, CreatedAt = DateTime.UtcNow };
            _context.TelemetryEntityMaps.Add(row);
        }
        row.EntityId = entity;
        row.Key = key;
        row.Scale = Map.Scale;
        row.Offset = Map.Offset;
        row.Invert = Map.Invert;
        row.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        TempData["FlashSuccess"] = $"บันทึกการจับคู่ {entity} → {key} แล้ว (มีผลกับข้อมูลที่เข้ามาหลังจากนี้)";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteMapAsync(int id, int mapId)
    {
        if (!await _access.CanAccessAsync(id, User)) return Forbid();
        var row = await _context.TelemetryEntityMaps.FirstOrDefaultAsync(m => m.Id == mapId && m.InstallationId == id);
        if (row != null)
        {
            _context.TelemetryEntityMaps.Remove(row);
            await _context.SaveChangesAsync();
            TempData["FlashSuccess"] = $"ลบการจับคู่ {row.EntityId} แล้ว";
        }
        return RedirectToPage(new { id });
    }

    // ── Loading ────────────────────────────────────────────────────────

    private async Task<bool> LoadAsync(int id)
    {
        var inst = await _context.Installations.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id);
        if (inst == null) return false;
        Installation = inst;

        Gateways = await _context.TelemetryGateways.AsNoTracking()
            .Where(g => g.InstallationId == id)
            .OrderBy(g => g.RevokedAt != null).ThenByDescending(g => g.CreatedAt)
            .ToListAsync();

        Maps = await _context.TelemetryEntityMaps.AsNoTracking()
            .Where(m => m.InstallationId == id)
            .OrderBy(m => m.Key).ThenBy(m => m.EntityId)
            .ToListAsync();

        var channels = await _context.TelemetryChannels.AsNoTracking()
            .Where(c => c.InstallationId == id)
            .OrderBy(c => c.Kind).ThenBy(c => c.Key)
            .ToListAsync();

        // Coverage over the last 24 h from the hourly rollups: samples
        // received ÷ samples expected at one a minute. Cheap (≤ 24 rows per
        // channel) and good enough to spot a sensor HA stopped sending.
        var coverage = new Dictionary<int, double>();
        if (channels.Count > 0)
        {
            try
            {
                var now = DateTime.UtcNow;
                var hours = await _store.ReadAggregatesAsync(channels.Select(c => c.Id).ToList(),
                    AggregateResolution.Hour, now.AddHours(-24), now);
                var expected = 24 * 3600d / ExpectedIntervalSeconds;
                foreach (var g in hours.GroupBy(h => h.ChannelId))
                    coverage[g.Key] = Math.Min(100, g.Sum(h => h.Count) / expected * 100);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Coverage lookup failed for installation {Id}", id);
            }
        }

        Channels = channels.Select(c => new ChannelRow(c.Id, c.Key, c.Kind, c.Unit, c.LastValue,
                c.LastTs == DateTime.MinValue ? null : c.LastTs, c.SourceEntityId,
                coverage.TryGetValue(c.Id, out var pct) ? pct : (double?)null))
            .ToList();

        BaseUrl = await ResolveBaseUrlAsync();
        HaConfig = HomeAssistantConfigGenerator.Generate(BaseUrl,
            Maps.Select(m => new HaEntity(m.EntityId, m.Key)), NewToken);
        return true;
    }

    /// <summary>The public URL HA must reach: the configured App.BaseUrl, else this request's own host.</summary>
    private async Task<string> ResolveBaseUrlAsync()
    {
        var configured = await _context.SystemSettings.AsNoTracking()
            .Where(s => s.Key == "App.BaseUrl").Select(s => s.Value).FirstOrDefaultAsync();
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim().TrimEnd('/');
        return $"{Request.Scheme}://{Request.Host}{Request.PathBase}".TrimEnd('/');
    }

    // ── View helpers ───────────────────────────────────────────────────

    public static string SourceLabel(TelemetrySource s) => s switch
    {
        TelemetrySource.LocalGateway => "เกตเวย์ในพื้นที่ (Home Assistant)",
        TelemetrySource.Hybrid => "ผสม (เกตเวย์ + คลาวด์)",
        _ => "คลาวด์ของผู้ผลิต",
    };

    public static string KindLabel(TelemetryDeviceKind k) => k switch
    {
        TelemetryDeviceKind.Pv => "PV",
        TelemetryDeviceKind.PvString => "สตริง PV",
        TelemetryDeviceKind.Inverter => "อินเวอร์เตอร์",
        TelemetryDeviceKind.Grid => "กริด / มิเตอร์",
        TelemetryDeviceKind.Load => "โหลด",
        TelemetryDeviceKind.Battery => "แบตเตอรี่",
        TelemetryDeviceKind.BatteryPack => "แพ็กแบตเตอรี่",
        TelemetryDeviceKind.BatteryCell => "เซลล์แบตเตอรี่",
        TelemetryDeviceKind.Environment => "สภาพแวดล้อม",
        _ => "อื่น ๆ / ยังไม่จับคู่",
    };

    public static string Ago(DateTime? utc)
    {
        if (utc == null) return "ไม่เคย";
        var d = DateTime.UtcNow - utc.Value;
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalMinutes < 1) return "เมื่อสักครู่";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} นาทีที่แล้ว";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} ชม. ที่แล้ว";
        return $"{(int)d.TotalDays} วันที่แล้ว";
    }

    /// <summary>Freshness badge colour: live within 5 min, late within an hour, otherwise stale.</summary>
    public static string FreshnessClass(DateTime? utc) =>
        utc == null ? "bg-secondary"
        : DateTime.UtcNow - utc.Value <= TimeSpan.FromMinutes(5) ? "bg-success"
        : DateTime.UtcNow - utc.Value <= TimeSpan.FromHours(1) ? "bg-warning text-dark"
        : "bg-danger";

    /// <summary>Canonical keys for the mapping datalist (DESIGN.md §3), with the common numbered ones spelled out.</summary>
    public static readonly IReadOnlyList<string> CanonicalKeys = BuildCanonicalKeys();

    private static List<string> BuildCanonicalKeys()
    {
        var k = new List<string>
        {
            "pv.power_w", "pv.energy_today_kwh", "pv.energy_total_kwh",
            "grid.power_w", "grid.import_total_kwh", "grid.export_total_kwh",
            "grid.voltage_v", "grid.l1.voltage_v", "grid.l2.voltage_v", "grid.l3.voltage_v",
            "grid.frequency_hz", "grid.status",
            "load.power_w", "load.energy_total_kwh",
            "battery.soc_pct", "battery.soh_pct", "battery.power_w",
            "battery.charge_total_kwh", "battery.discharge_total_kwh",
            "battery.voltage_v", "battery.current_a", "battery.temp_c",
            "env.lux", "env.irradiance_wm2", "env.temp_c",
        };
        for (var n = 1; n <= 8; n++)
            k.AddRange(new[] { $"pv.string{n}.voltage_v", $"pv.string{n}.current_a", $"pv.string{n}.power_w" });
        for (var n = 1; n <= 4; n++)
            k.AddRange(new[] { $"inverter{n}.power_w", $"inverter{n}.temp_c", $"inverter{n}.status_code",
                $"inverter{n}.alarm_code", $"inverter{n}.energy_total_kwh" });
        for (var n = 1; n <= 4; n++)
            k.AddRange(new[] { $"battery.pack{n}.soc_pct", $"battery.pack{n}.soh_pct", $"battery.pack{n}.power_w",
                $"battery.pack{n}.voltage_v", $"battery.pack{n}.current_a", $"battery.pack{n}.cell_v_max",
                $"battery.pack{n}.cell_v_min", $"battery.pack{n}.cell_temp_max_c", $"battery.pack{n}.cell_temp_min_c" });
        return k;
    }
}
