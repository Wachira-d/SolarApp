using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Services.Analytics;

namespace SolarApp.Pages.Admin;

/// <summary>
/// System-wide factors behind every CO₂ and tree figure (staff analytics,
/// customer portal, monthly LINE report). One value for all sites: they sit
/// on the same national grid.
/// </summary>
[Authorize(Roles = "Admin")]
public class ReportSettingsModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<ReportSettingsModel> _logger;

    public ReportSettingsModel(ApplicationDbContext context, UserManager<ApplicationUser> userManager, ILogger<ReportSettingsModel> logger)
    {
        _context = context;
        _userManager = userManager;
        _logger = logger;
    }

    /// <summary>Strings so a Thai/English decimal comma or point both parse, and a bad value can be echoed back.</summary>
    [BindProperty] public string Co2KgPerKwh { get; set; } = "";
    [BindProperty] public string Co2KgPerTreeYear { get; set; } = "";
    [BindProperty] public string? Source { get; set; }

    public EnvironmentalFactors Current { get; private set; } = EnvironmentalFactors.Default;
    public string? Error { get; private set; }
    [TempData] public string? StatusMessage { get; set; }

    public async Task OnGetAsync()
    {
        Current = await EnvironmentalFactors.LoadAsync(_context);
        Co2KgPerKwh = Current.Co2KgPerKwh.ToString("0.####", CultureInfo.InvariantCulture);
        Co2KgPerTreeYear = Current.Co2KgPerTreeYear.ToString("0.##", CultureInfo.InvariantCulture);
        Source = Current.Source;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Current = await EnvironmentalFactors.LoadAsync(_context);
        if (!TryParse(Co2KgPerKwh, out var perKwh) || perKwh < EnvironmentalFactors.MinPerKwh || perKwh > EnvironmentalFactors.MaxPerKwh)
        {
            Error = $"ค่า CO₂ ต่อ kWh ต้องเป็นตัวเลข {EnvironmentalFactors.MinPerKwh}–{EnvironmentalFactors.MaxPerKwh} กก.";
            return Page();
        }
        if (!TryParse(Co2KgPerTreeYear, out var perTree) || perTree < EnvironmentalFactors.MinPerTree || perTree > EnvironmentalFactors.MaxPerTree)
        {
            Error = $"ค่า CO₂ ต่อต้นไม้ต้องเป็นตัวเลข {EnvironmentalFactors.MinPerTree}–{EnvironmentalFactors.MaxPerTree} กก./ปี";
            return Page();
        }
        var source = string.IsNullOrWhiteSpace(Source) ? null : Source.Trim();
        if (source is { Length: > 200 }) source = source[..200];

        await EnvironmentalFactors.SaveAsync(_context, perKwh, perTree, source, _userManager.GetUserId(User));
        _logger.LogInformation("Report CO2 factors set by {User}: {PerKwh} kg/kWh, {PerTree} kg/tree/yr, source {Source}",
            User.Identity?.Name, perKwh, perTree, source);
        StatusMessage = "บันทึกแล้ว — ทุกหน้าและรายงานรายเดือนจะใช้ค่านี้ทันที";
        return RedirectToPage();
    }

    public static bool TryParse(string? raw, out double value) =>
        double.TryParse((raw ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>Preview figures for 1,000 kWh, so the operator sees what a change does before saving.</summary>
    public static (double Co2Kg, double Trees) Example(EnvironmentalFactors f) => (f.Co2Kg(1000), f.Trees(f.Co2Kg(1000)));
}
