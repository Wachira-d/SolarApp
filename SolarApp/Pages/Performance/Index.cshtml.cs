using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SolarApp.Services.Performance;

namespace SolarApp.Pages.Performance;

[Authorize]
public class IndexModel : PageModel
{
    private readonly IPerformanceMetricsService _metrics;

    public IndexModel(IPerformanceMetricsService metrics)
    {
        _metrics = metrics;
    }

    public FleetPerformance Fleet { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public DateTime From { get; set; } = SolarApp.Services.SiteTime.Today.AddDays(-29);

    [BindProperty(SupportsGet = true)]
    public DateTime To { get; set; } = SolarApp.Services.SiteTime.Today;

    /// <summary>"csv" streams the comparison table instead of rendering it.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Export { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (To < From) (From, To) = (To, From);
        if ((To - From).TotalDays > 365) From = To.AddDays(-365);

        Fleet = await _metrics.GetFleetPerformanceAsync(User, From, To);

        if (string.Equals(Export, "csv", StringComparison.OrdinalIgnoreCase))
            return BuildCsv();

        return Page();
    }

    /// <summary>
    /// The comparison numbers end up in a spreadsheet sooner or later — someone
    /// always wants them next to a cost column or in a slide. Retyping twenty
    /// rows by hand is how transcription errors get into a customer report.
    /// </summary>
    private FileContentResult BuildCsv()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("ไซต์งาน,รหัสไซต์,ลูกค้า,กำลังติดตั้ง kWp,ผลิตจริง kWh,ที่ควรผลิตได้ kWh,"
                      + "ผลผลิตจำเพาะ kWh/kWp,PR %,Uptime %,วันที่ออฟไลน์,แจ้งเตือนค้าง,เกรด");

        foreach (var p in Fleet.AllSites)
        {
            sb.AppendLine(string.Join(',',
                Csv(p.Installation.SiteName),
                Csv(p.Installation.SiteCode),
                Csv(p.Installation.Customer?.FullName),
                p.Installation.SystemCapacityKw.ToString("0.###", Inv),
                p.TotalActualKwh.ToString("0.##", Inv),
                p.TotalExpectedKwh.ToString("0.##", Inv),
                p.SpecificYieldKwhPerKw.ToString("0.##", Inv),
                (p.PerformanceRatio * 100).ToString("0.#", Inv),
                p.UptimePercent.ToString("0.#", Inv),
                p.OfflineDays.ToString(Inv),
                p.UnresolvedAlerts.ToString(Inv),
                Csv(p.HealthGrade)));
        }

        // UTF-8 BOM: without it Excel on Windows reads the Thai column headers
        // as mojibake, which is most of what this file is for.
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString()))
            .ToArray();

        return File(bytes, "text/csv",
            $"performance_{From:yyyyMMdd}-{To:yyyyMMdd}.csv");
    }

    private static readonly System.Globalization.CultureInfo Inv =
        System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>Quote a field so a comma in a site name can't shift every column after it.</summary>
    private static string Csv(string? value)
    {
        var v = value ?? string.Empty;
        return v.Contains(',') || v.Contains('"') || v.Contains('\n')
            ? "\"" + v.Replace("\"", "\"\"") + "\""
            : v;
    }
}
