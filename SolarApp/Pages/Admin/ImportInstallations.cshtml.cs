using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Pages.Admin;

[Authorize(Roles = "Admin")]
public class ImportInstallationsModel : PageModel
{
    private readonly ApplicationDbContext _context;

    public ImportInstallationsModel(ApplicationDbContext context) { _context = context; }

    [BindProperty] public new IFormFile? File { get; set; }
    [BindProperty] public bool DryRun { get; set; } = true;

    public List<string> Errors { get; set; } = new();
    public List<ImportResult> Results { get; set; } = new();
    public int SuccessCount { get; set; }
    public int ErrorCount { get; set; }
    public bool Imported { get; set; }

    public record ImportResult(int Row, string SiteName, string Status, string? Detail);

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        if (File == null || File.Length == 0)
        {
            Errors.Add("Please select a CSV file.");
            return Page();
        }

        if (File.Length > 5 * 1024 * 1024)
        {
            Errors.Add("File too large — max 5 MB.");
            return Page();
        }

        var ext = Path.GetExtension(File.FileName).ToLower();
        if (ext != ".csv")
        {
            Errors.Add("File must be .csv");
            return Page();
        }

        var customerEmailMap = (await _context.Customers
            .Where(c => c.Email != null && c.Email != "")
            .Select(c => new { c.Id, c.Email }).ToListAsync())
            .GroupBy(c => c.Email!.ToLower())
            .ToDictionary(g => g.Key, g => g.First().Id);
        var partnerNameMap = await _context.Partners
            .Select(p => new { p.Id, p.CompanyName }).ToDictionaryAsync(p => p.CompanyName.ToLower(), p => p.Id);
        var existingSiteCodes = await _context.Installations
            .Select(i => i.SiteCode).ToListAsync();
        var siteCodeSet = new HashSet<string>(existingSiteCodes, StringComparer.OrdinalIgnoreCase);

        using var reader = new StreamReader(File.OpenReadStream(), Encoding.UTF8);
        var header = await reader.ReadLineAsync();
        if (header == null)
        {
            Errors.Add("CSV is empty.");
            return Page();
        }

        var columns = ParseCsvLine(header).Select(c => c.Trim().ToLower()).ToList();
        var required = new[] { "sitecode", "sitename", "customeremail", "capacitykw", "inverterbrand", "systemtype" };
        var missing = required.Where(r => !columns.Contains(r)).ToList();
        if (missing.Any())
        {
            Errors.Add($"Missing required columns: {string.Join(", ", missing)}");
            return Page();
        }

        int row = 1; // header counted as 1
        var toAdd = new List<Installation>();

        while (!reader.EndOfStream)
        {
            row++;
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var values = ParseCsvLine(line);
            string Get(string col)
            {
                var idx = columns.IndexOf(col);
                return idx >= 0 && idx < values.Count ? values[idx].Trim() : string.Empty;
            }

            var siteCode = Get("sitecode");
            var siteName = Get("sitename");
            var customerEmail = Get("customeremail").ToLower();
            var partnerName = Get("partnername").ToLower();
            var capacityStr = Get("capacitykw");
            var brandStr = Get("inverterbrand");
            var sysStr = Get("systemtype");

            if (string.IsNullOrEmpty(siteCode) || string.IsNullOrEmpty(siteName))
            {
                Results.Add(new ImportResult(row, siteName, "ERROR", "siteCode / siteName required"));
                ErrorCount++;
                continue;
            }
            if (siteCodeSet.Contains(siteCode))
            {
                Results.Add(new ImportResult(row, siteName, "SKIP", $"SiteCode '{siteCode}' already exists"));
                continue;
            }
            if (!customerEmailMap.TryGetValue(customerEmail, out var customerId))
            {
                Results.Add(new ImportResult(row, siteName, "ERROR", $"Customer email '{customerEmail}' not found"));
                ErrorCount++;
                continue;
            }

            int? partnerId = null;
            if (!string.IsNullOrEmpty(partnerName))
            {
                if (!partnerNameMap.TryGetValue(partnerName, out var pid))
                {
                    Results.Add(new ImportResult(row, siteName, "ERROR", $"Partner '{partnerName}' not found"));
                    ErrorCount++;
                    continue;
                }
                partnerId = pid;
            }

            if (!decimal.TryParse(capacityStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var capacityKw) || capacityKw <= 0)
            {
                Results.Add(new ImportResult(row, siteName, "ERROR", $"Invalid capacityKw '{capacityStr}'"));
                ErrorCount++;
                continue;
            }

            if (!Enum.TryParse<InverterBrand>(brandStr, true, out var brand))
            {
                Results.Add(new ImportResult(row, siteName, "ERROR",
                    $"Invalid inverterBrand '{brandStr}'. Valid: {string.Join(", ", Enum.GetNames<InverterBrand>())}"));
                ErrorCount++;
                continue;
            }

            if (!Enum.TryParse<SystemType>(sysStr, true, out var sysType))
            {
                Results.Add(new ImportResult(row, siteName, "ERROR",
                    $"Invalid systemType '{sysStr}'. Valid: {string.Join(", ", Enum.GetNames<SystemType>())}"));
                ErrorCount++;
                continue;
            }

            double? lat = null, lng = null;
            if (double.TryParse(Get("latitude"), NumberStyles.Any, CultureInfo.InvariantCulture, out var latVal)) lat = latVal;
            if (double.TryParse(Get("longitude"), NumberStyles.Any, CultureInfo.InvariantCulture, out var lngVal)) lng = lngVal;

            toAdd.Add(new Installation
            {
                SiteCode = siteCode,
                SiteName = siteName,
                CustomerId = customerId,
                PartnerId = partnerId,
                SystemCapacityKw = capacityKw,
                InverterBrand = brand,
                SystemType = sysType,
                Address = Get("address"),
                City = Get("city"),
                Province = Get("province"),
                PostalCode = Get("postalcode"),
                Latitude = lat,
                Longitude = lng,
                InstallationDate = DateTime.TryParse(Get("installationdate"), out var instDate) ? instDate : DateTime.UtcNow,
                Status = InstallationStatus.Active,
                CleaningFrequencyMonths = 6
            });
            siteCodeSet.Add(siteCode);
            Results.Add(new ImportResult(row, siteName, DryRun ? "VALID" : "OK", null));
            SuccessCount++;
        }

        if (!DryRun && toAdd.Any())
        {
            _context.Installations.AddRange(toAdd);
            await _context.SaveChangesAsync();
            Imported = true;
        }

        return Page();
    }

    // Minimal CSV parser — handles quoted fields with commas
    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else sb.Append(c);
            }
            else
            {
                if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
                else if (c == '"') inQuotes = true;
                else sb.Append(c);
            }
        }
        result.Add(sb.ToString());
        return result;
    }
}
