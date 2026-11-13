namespace SolarApp.Services.Analytics;

public sealed record EnergyTotals(double PvKwh, double ImportKwh, double ExportKwh, double? LoadKwh = null);

public sealed record MonthlyEnergy(int Year, int Month, double PvKwh, double ImportKwh, double ExportKwh, double? LoadKwh = null);

public sealed class EconomicsOptions
{
    public double ImportRateThb { get; init; } = 4.5;
    public double FeedInRateThb { get; init; }
    /// <summary>Installed price (Installation.ContractValue); null = no payback figures.</summary>
    public double? SystemPriceThb { get; init; }
    /// <summary>Thai grid emission factor, kg CO₂ per kWh (setting).</summary>
    public double Co2KgPerKwh { get; init; } = 0.5;
    /// <summary>kg CO₂ one tree absorbs per year.</summary>
    public double Co2KgPerTree { get; init; } = 9;
}

public sealed record EconomicsResult(double SelfConsumedKwh, double ConsumptionKwh, double SavingsThb, double ImportAvoidedThb,
    double ExportIncomeThb, double? SelfSufficiencyPct, double? SelfConsumptionPct, double Co2Kg, double Trees, double? PaybackProgressPct);

public sealed record MonthlyEconomics(int Year, int Month, EconomicsResult Result, double CumulativeSavingsThb, double? CumulativePaybackPct,
    double? PvYoYPct, double? SavingsYoYPct, double? SelfSufficiencyYoYPts);

/// <summary>Owner-facing money and environment figures (DESIGN.md §7.4).</summary>
public static class EconomicsCalculator
{
    /// <param name="cumulativeSavingsThb">Savings so far when computing payback for a period that is not since commissioning.</param>
    public static EconomicsResult Calculate(EnergyTotals e, EconomicsOptions? options = null, double? cumulativeSavingsThb = null)
    {
        var o = options ?? new EconomicsOptions();
        var self = Math.Max(0, e.PvKwh - e.ExportKwh);
        // Without a load meter, consumption is what we used of our own PV plus what we bought.
        var consumption = e.LoadKwh ?? self + e.ImportKwh;
        var avoided = self * o.ImportRateThb;
        var export = e.ExportKwh * o.FeedInRateThb;
        var savings = avoided + export;
        double? suff = consumption > 0 ? Math.Clamp((1 - e.ImportKwh / consumption) * 100, 0, 100) : null;
        double? selfUse = e.PvKwh > 0 ? self / e.PvKwh * 100 : null;
        var co2 = e.PvKwh * o.Co2KgPerKwh;
        var paybackBase = cumulativeSavingsThb ?? savings;
        double? payback = o.SystemPriceThb is > 0 ? paybackBase / o.SystemPriceThb.Value * 100 : null;
        return new EconomicsResult(self, consumption, savings, avoided, export, suff, selfUse, co2,
            o.Co2KgPerTree > 0 ? co2 / o.Co2KgPerTree : 0, payback);
    }

    /// <summary>Per month with running payback; year-on-year once the same month a year earlier exists.</summary>
    public static List<MonthlyEconomics> Monthly(IEnumerable<MonthlyEnergy> months, EconomicsOptions? options = null)
    {
        var o = options ?? new EconomicsOptions();
        var ordered = months.OrderBy(m => m.Year).ThenBy(m => m.Month).ToList();
        var byKey = new Dictionary<(int, int), EconomicsResult>();
        var list = new List<MonthlyEconomics>();
        double cumulative = 0;
        foreach (var m in ordered)
        {
            var r = Calculate(new EnergyTotals(m.PvKwh, m.ImportKwh, m.ExportKwh, m.LoadKwh), o);
            cumulative += r.SavingsThb;
            byKey[(m.Year, m.Month)] = r;
            double? cumPayback = o.SystemPriceThb is > 0 ? cumulative / o.SystemPriceThb.Value * 100 : null;
            double? pvYoY = null, savYoY = null, suffYoY = null;
            if (byKey.TryGetValue((m.Year - 1, m.Month), out var prev))
            {
                var prevPv = ordered.First(x => x.Year == m.Year - 1 && x.Month == m.Month).PvKwh;
                pvYoY = prevPv > 0 ? (m.PvKwh / prevPv - 1) * 100 : null;
                savYoY = prev.SavingsThb > 0 ? (r.SavingsThb / prev.SavingsThb - 1) * 100 : null;
                suffYoY = r.SelfSufficiencyPct - prev.SelfSufficiencyPct;
            }
            list.Add(new MonthlyEconomics(m.Year, m.Month, r with { PaybackProgressPct = cumPayback }, cumulative, cumPayback, pvYoY, savYoY, suffYoY));
        }
        return list;
    }
}
