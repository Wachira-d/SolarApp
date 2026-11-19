using System.Globalization;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SolarApp.BackgroundJobs;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Services;
using SolarApp.Services.Telemetry;

namespace SolarApp.Pages.Admin;

/// <summary>
/// Where telemetry is stored and how big it is getting (DESIGN.md §5), plus
/// the one-off copy of the telemetry left in SQL Server after
/// Telemetry:Store moved to PostgreSQL / TimescaleDB.
/// </summary>
[Authorize(Roles = "Admin")]
public class TelemetryStorageModel : PageModel
{
    /// <summary>The page must not hang on an unreachable SQL Server while checking for data left behind.</summary>
    private const int LegacyConnectTimeoutSeconds = 5;

    private readonly ApplicationDbContext _context;
    private readonly ITelemetryStore _store;
    private readonly IConfiguration _configuration;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<TelemetryStorageModel> _logger;

    public TelemetryStorageModel(ApplicationDbContext context, ITelemetryStore store, IConfiguration configuration,
        UserManager<ApplicationUser> userManager, ILogger<TelemetryStorageModel> logger)
    {
        _context = context;
        _store = store;
        _configuration = configuration;
        _userManager = userManager;
        _logger = logger;
    }

    /// <summary>
    /// Optional keyed service standing in for the SQL Server tables (tests).
    /// Not registered in production: the tables are read through the
    /// TelemetryConnection / DefaultConnection connection string.
    /// </summary>
    public const string LegacySourceKey = "TelemetryLegacySqlServer";

    public TelemetryStorageStatus Status { get; private set; } = new();
    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    private TelemetryStorageInspector Inspector() => new(_context, _store,
        () => HttpContext.RequestServices.GetKeyedService<ITelemetryStoreStats>(LegacySourceKey)
              ?? _store as SqlServerTelemetryStore
              ?? SqlServerTelemetryStore.FromConfiguration(_configuration, LegacyConnectTimeoutSeconds), _logger);

    public async Task OnGetAsync(CancellationToken ct)
    {
        Status = await Inspector().InspectAsync(DateTime.UtcNow, ct);
    }

    public async Task<IActionResult> OnPostCopyAsync(CancellationToken ct)
    {
        var status = await Inspector().InspectAsync(DateTime.UtcNow, ct);
        if (!status.CopyAvailable)
        {
            ErrorMessage = status.IsPostgres
                ? "ไม่พบข้อมูล telemetry เดิมใน SQL Server ที่ต้องคัดลอก"
                : "คัดลอกได้เฉพาะเมื่อระบบใช้ PostgreSQL / TimescaleDB เก็บ telemetry";
            return RedirectToPage();
        }

        var request = await TelemetryStoreCopier.RequestAsync(_context, User.Identity?.Name, _userManager.GetUserId(User), DateTime.UtcNow, ct);
        if (!request.Started)
        {
            StatusMessage = "กำลังคัดลอกอยู่แล้ว ไม่ได้เริ่มซ้ำ — ดูความคืบหน้าด้านล่าง";
            return RedirectToPage();
        }

        try
        {
            var runId = request.RunId!;
            BackgroundJob.Enqueue<TelemetryCopyFromSqlServerJob>(j => j.ExecuteAsync(runId, CancellationToken.None));
            _logger.LogInformation("Telemetry copy from SQL Server requested by {User} (run {RunId})", User.Identity?.Name, runId);
            StatusMessage = "เริ่มคัดลอกแล้ว งานทำอยู่เบื้องหลัง ปิดหน้านี้ได้ — เปิดกลับมาดูความคืบหน้าได้ตลอด";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telemetry copy: could not enqueue the job");
            await TelemetryStoreCopier.RecordFailureAsync(_context, $"เข้าคิวงานเบื้องหลังไม่ได้: {ex.Message}", DateTime.UtcNow, ct);
            ErrorMessage = "เริ่มงานเบื้องหลังไม่ได้ (Hangfire) — ลองใหม่อีกครั้ง หรือดูบันทึกของระบบ";
        }
        return RedirectToPage();
    }

    // ── display helpers ──

    public static string Bytes(double? bytes)
    {
        if (bytes is not { } b) return "–";
        string[] units = { "ไบต์", "KB", "MB", "GB", "TB" };
        var i = 0;
        while (b >= 1024 && i < units.Length - 1) { b /= 1024; i++; }
        return i == 0 ? $"{b:N0} {units[i]}" : $"{b.ToString(b < 10 ? "N2" : b < 100 ? "N1" : "N0", CultureInfo.CurrentCulture)} {units[i]}";
    }

    public static string Count(long? n) => n is { } v ? v.ToString("N0", CultureInfo.CurrentCulture) : "–";

    public static string Time(DateTime? utc) =>
        utc is { } t ? SiteTime.ToSiteTime(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToString("d MMM yyyy HH:mm", CultureInfo.CurrentCulture) : "–";

    public static string StoreName(TelemetryStoreKind kind) => kind switch
    {
        TelemetryStoreKind.SqlServer => "SQL Server",
        TelemetryStoreKind.Postgres => "PostgreSQL (ไม่มี TimescaleDB)",
        TelemetryStoreKind.Timescale => "PostgreSQL + TimescaleDB",
        _ => "หน่วยความจำ (ทดสอบ)",
    };

    public static string TableLabel(TelemetryTableKind kind) => kind switch
    {
        TelemetryTableKind.Raw => "ค่าดิบ",
        TelemetryTableKind.AllAggregates => "ค่าสรุปทุกระดับ",
        TelemetryTableKind.FiveMinutes => "สรุปราย 5 นาที",
        TelemetryTableKind.Hour => "สรุปรายชั่วโมง",
        TelemetryTableKind.Day => "สรุปรายวัน",
        _ => kind.ToString(),
    };

    public static (string Text, string Css) CopyStatus(TelemetryCopyProgress p, bool active) => p.Status switch
    {
        TelemetryCopyStatus.None => ("ยังไม่เคยคัดลอก", "secondary"),
        TelemetryCopyStatus.Queued when active => ("รอคิวงานเบื้องหลัง", "info"),
        TelemetryCopyStatus.Running when active => ("กำลังคัดลอก", "primary"),
        TelemetryCopyStatus.Queued or TelemetryCopyStatus.Running => ("ไม่มีความคืบหน้านานเกิน 15 นาที — กดปุ่มเพื่อทำต่อ", "warning"),
        TelemetryCopyStatus.Interrupted => ("หยุดกลางทาง — กดปุ่มเพื่อทำต่อจากจุดเดิม", "warning"),
        TelemetryCopyStatus.Failed => ("ผิดพลาด — กดปุ่มเพื่อทำต่อจากจุดเดิม", "danger"),
        TelemetryCopyStatus.Done => ("คัดลอกเสร็จแล้ว", "success"),
        _ => (p.Status.ToString(), "secondary"),
    };
}
