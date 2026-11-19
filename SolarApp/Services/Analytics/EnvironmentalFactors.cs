using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;

namespace SolarApp.Services.Analytics;

/// <summary>
/// The two numbers behind every "CO₂ saved" and "trees" figure, set once for
/// the whole system on /Admin/ReportSettings so every page and report agrees:
/// kg CO₂ avoided per kWh of solar, and kg CO₂ one tree absorbs in a year.
/// Not per site: every Thai site sits on the same grid.
/// </summary>
public sealed record EnvironmentalFactors(double Co2KgPerKwh, double Co2KgPerTreeYear, string? Source)
{
    public const string Co2PerKwhKey = "Report.Co2KgPerKwh";
    public const string Co2PerTreeKey = "Report.Co2KgPerTreeYear";
    public const string SourceKey = "Report.Co2Source";

    /// <summary>Defaults until an operator sets them — the values the pages used before.</summary>
    public const double DefaultCo2KgPerKwh = 0.5;
    public const double DefaultCo2KgPerTreeYear = 9;

    public const double MinPerKwh = 0.05, MaxPerKwh = 2.0;
    public const double MinPerTree = 1, MaxPerTree = 100;

    public static EnvironmentalFactors Default { get; } = new(DefaultCo2KgPerKwh, DefaultCo2KgPerTreeYear, null);

    public double Co2Kg(double kwh) => Math.Max(0, kwh) * Co2KgPerKwh;

    /// <summary>Trees that would need a year to absorb this much CO₂.</summary>
    public double Trees(double co2Kg) => Co2KgPerTreeYear > 0 ? co2Kg / Co2KgPerTreeYear : 0;

    /// <summary>One line for under a CO₂ figure: what it was computed from.</summary>
    public string BasisText =>
        $"คิดที่ {Co2KgPerKwh.ToString("0.####", CultureInfo.InvariantCulture)} กก. CO₂/kWh · ต้นไม้ 1 ต้นดูดซับ " +
        $"{Co2KgPerTreeYear.ToString("0.#", CultureInfo.InvariantCulture)} กก./ปี" +
        (string.IsNullOrWhiteSpace(Source) ? "" : $" ({Source})");

    public static async Task<EnvironmentalFactors> LoadAsync(ApplicationDbContext db, CancellationToken ct = default)
    {
        var keys = new[] { Co2PerKwhKey, Co2PerTreeKey, SourceKey };
        var rows = await db.SystemSettings.AsNoTracking()
            .Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        return new EnvironmentalFactors(
            Parse(rows.GetValueOrDefault(Co2PerKwhKey), DefaultCo2KgPerKwh, MinPerKwh, MaxPerKwh),
            Parse(rows.GetValueOrDefault(Co2PerTreeKey), DefaultCo2KgPerTreeYear, MinPerTree, MaxPerTree),
            string.IsNullOrWhiteSpace(rows.GetValueOrDefault(SourceKey)) ? null : rows[SourceKey].Trim());
    }

    /// <summary>A stored value outside the allowed range is ignored, so one typo can't make every report absurd.</summary>
    internal static double Parse(string? raw, double fallback, double min, double max) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max ? v : fallback;

    public static async Task SaveAsync(ApplicationDbContext db, double perKwh, double perTree, string? source, string? userId,
        CancellationToken ct = default)
    {
        async Task Upsert(string key, string value, string description)
        {
            var row = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
            if (row == null)
                db.SystemSettings.Add(new SystemSetting { Key = key, Value = value, Description = description, UpdatedByUserId = userId });
            else
            {
                row.Value = value;
                row.UpdatedAt = DateTime.UtcNow;
                row.UpdatedByUserId = userId;
            }
        }
        await Upsert(Co2PerKwhKey, perKwh.ToString("0.######", CultureInfo.InvariantCulture), "กก. CO₂ ที่ลดได้ต่อไฟโซลาร์ 1 kWh");
        await Upsert(Co2PerTreeKey, perTree.ToString("0.###", CultureInfo.InvariantCulture), "กก. CO₂ ที่ต้นไม้ 1 ต้นดูดซับต่อปี");
        await Upsert(SourceKey, (source ?? "").Trim(), "แหล่งอ้างอิงของค่า CO₂");
        await db.SaveChangesAsync(ct);
    }

    public EconomicsOptions Apply(EconomicsOptions o) => new()
    {
        ImportRateThb = o.ImportRateThb,
        FeedInRateThb = o.FeedInRateThb,
        SystemPriceThb = o.SystemPriceThb,
        Co2KgPerKwh = Co2KgPerKwh,
        Co2KgPerTree = Co2KgPerTreeYear,
    };
}
