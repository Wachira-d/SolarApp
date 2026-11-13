using SolarApp.Models.Enums;

namespace SolarApp.Services.Analytics;

/// <summary>One progressive block: kWh up to <see cref="UpToKwh"/> (cumulative; null = the rest) at <see cref="Rate"/>.</summary>
public sealed record TariffTier(double? UpToKwh, double Rate);

/// <summary>
/// An electricity plan, editable per site and stored as JSON. All rates are
/// THB per kWh before VAT; Ft is added per kWh, VAT applies to the total.
/// </summary>
public sealed class TariffConfig
{
    public string Name { get; init; } = "";
    public TariffKind Kind { get; init; } = TariffKind.Flat;
    public double FlatRate { get; init; }
    public List<TariffTier> Tiers { get; init; } = new();
    public double PeakRate { get; init; }
    public double OffPeakRate { get; init; }
    /// <summary>Peak window [start, end) in local hours on peak days.</summary>
    public int PeakStartHour { get; init; } = 9;
    public int PeakEndHour { get; init; } = 22;
    public List<DayOfWeek> PeakDays { get; init; } = new()
        { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
    public double FtPerKwh { get; init; }
    public double ServiceChargePerMonth { get; init; }
    public double VatPct { get; init; } = 7;
    /// <summary>True for preset values the operator has not yet confirmed against the current PEA/MEA notice.</summary>
    public bool IsUnconfirmedDefault { get; init; }
    public string? Note { get; init; }

    /// <summary>Weekdays 09:00–22:00 are peak; weekends and listed holidays are all off-peak.</summary>
    public bool IsPeak(DateTime local, IReadOnlyCollection<DateOnly>? holidays = null)
    {
        if (Kind != TariffKind.TimeOfUse) return false;
        if (!PeakDays.Contains(local.DayOfWeek)) return false;
        if (holidays is not null && holidays.Contains(DateOnly.FromDateTime(local))) return false;
        return local.Hour >= PeakStartHour && local.Hour < PeakEndHour;
    }

    public string ToJson() => AnalyticsJson.Serialize(this);
    public static TariffConfig FromJson(string json) =>
        AnalyticsJson.Deserialize<TariffConfig>(json) ?? throw new FormatException("Empty tariff JSON");
}

/// <summary>
/// Starting points for the rate settings page. These are DEFAULTS the
/// operator must confirm: PEA rates and especially Ft change every few
/// months, so every preset is marked <see cref="TariffConfig.IsUnconfirmedDefault"/>.
/// </summary>
public static class TariffPresets
{
    private const string ConfirmNote = "ค่าเริ่มต้น — โปรดตรวจสอบอัตราและค่า Ft ล่าสุดของ กฟภ. ก่อนใช้งาน";

    /// <summary>PEA residential &gt; 150 kWh/month (1.1.2-style), progressive.</summary>
    public static TariffConfig PeaResidentialProgressive() => new()
    {
        Name = "กฟภ. บ้านอยู่อาศัย 1.1.2 (อัตราก้าวหน้า)",
        Kind = TariffKind.Progressive,
        Tiers = new()
        {
            new TariffTier(150, 3.2484),
            new TariffTier(400, 4.2218),
            new TariffTier(null, 4.4217),
        },
        FtPerKwh = 0.3672,
        ServiceChargePerMonth = 24.62,
        VatPct = 7,
        IsUnconfirmedDefault = true,
        Note = ConfirmNote,
    };

    /// <summary>PEA residential TOU (1.2-style): weekdays 09:00–22:00 peak; weekends and public holidays off-peak.</summary>
    public static TariffConfig PeaResidentialTou() => new()
    {
        Name = "กฟภ. บ้านอยู่อาศัย 1.2 (TOU)",
        Kind = TariffKind.TimeOfUse,
        PeakRate = 5.7982,
        OffPeakRate = 2.6369,
        PeakStartHour = 9,
        PeakEndHour = 22,
        FtPerKwh = 0.3672,
        ServiceChargePerMonth = 24.62,
        VatPct = 7,
        IsUnconfirmedDefault = true,
        Note = ConfirmNote,
    };

    public static TariffConfig Flat(double rate, string name = "อัตราเดียว") => new()
    {
        Name = name,
        Kind = TariffKind.Flat,
        FlatRate = rate,
        VatPct = 7,
    };
}

public sealed record MonthlyBill(int Year, int Month, double Kwh, double PeakKwh, double OffPeakKwh,
    double EnergyCharge, double FtCharge, double ServiceCharge, double Vat, double Total);

/// <summary>Prices a month of imports under a plan.</summary>
public static class BillCalculator
{
    /// <param name="hourlyImportKwh">Each point is the kWh imported in the hour starting at its timestamp.</param>
    public static List<MonthlyBill> Monthly(TimeSeries hourlyImportKwh, TariffConfig tariff, TimeZoneInfo? timeZone = null,
        IReadOnlyCollection<DateOnly>? holidays = null)
    {
        var tz = LocalClock.Resolve(timeZone);
        return hourlyImportKwh.Points
            .Select(p => (Local: LocalClock.ToLocal(p.TsUtc, tz), Kwh: Math.Max(0, p.Value)))
            .GroupBy(x => (x.Local.Year, x.Local.Month))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                double peak = 0, off = 0;
                foreach (var x in g)
                {
                    if (tariff.IsPeak(x.Local, holidays)) peak += x.Kwh;
                    else off += x.Kwh;
                }
                return Bill(g.Key.Year, g.Key.Month, peak, off, tariff);
            })
            .ToList();
    }

    /// <summary>One month's bill from its peak / off-peak kWh (for non-TOU plans the split is irrelevant).</summary>
    public static MonthlyBill Bill(int year, int month, double peakKwh, double offPeakKwh, TariffConfig t)
    {
        var kwh = peakKwh + offPeakKwh;
        var energy = t.Kind switch
        {
            TariffKind.Flat => kwh * t.FlatRate,
            TariffKind.TimeOfUse => peakKwh * t.PeakRate + offPeakKwh * t.OffPeakRate,
            TariffKind.Progressive => Progressive(kwh, t.Tiers),
            _ => 0,
        };
        var ft = kwh * t.FtPerKwh;
        var service = t.ServiceChargePerMonth;
        var vat = (energy + ft + service) * t.VatPct / 100;
        return new MonthlyBill(year, month, kwh, peakKwh, offPeakKwh, energy, ft, service, vat, energy + ft + service + vat);
    }

    public static double Progressive(double kwh, IReadOnlyList<TariffTier> tiers)
    {
        double cost = 0, prev = 0;
        foreach (var tier in tiers)
        {
            var upper = tier.UpToKwh ?? double.PositiveInfinity;
            if (kwh <= prev) break;
            var inTier = Math.Min(kwh, upper) - prev;
            if (inTier > 0) cost += inTier * tier.Rate;
            prev = upper;
        }
        // Energy above the last bounded tier is billed at the last rate.
        if (tiers.Count > 0 && kwh > prev && double.IsFinite(prev)) cost += (kwh - prev) * tiers[^1].Rate;
        return cost;
    }
}

public sealed record MonthComparison(int Year, int Month, double ImportKwh, double PeakSharePct, double ExportKwh, double ExportCredit,
    double CurrentBill, double AlternativeBill, double Delta, double? DeltaPct);

public sealed class TouComparison
{
    public string CurrentName { get; init; } = "";
    public string AlternativeName { get; init; } = "";
    public List<MonthComparison> Months { get; init; } = new();
    public int MonthsCompared { get; init; }
    public double RecentCurrentTotal { get; init; }
    public double RecentAlternativeTotal { get; init; }
    /// <summary>Positive = the alternative is cheaper.</summary>
    public double RecentSavingThb { get; init; }
    public double? RecentSavingPct { get; init; }
    public double? RecentPeakSharePct { get; init; }
    public bool RecommendSwitch { get; init; }
    public string RecommendationTh { get; init; } = "";
    public bool UsesUnconfirmedRates { get; init; }
}

public sealed class TouOptions
{
    public int RecentMonths { get; init; } = 3;
    public double MinSavingPct { get; init; } = 3;
    /// <summary>Meter change cost; 0 = ignore. With a cost, the monthly saving must pay it back within <see cref="MaxPaybackMonths"/>.</summary>
    public double MeterChangeCostThb { get; init; }
    public double MaxPaybackMonths { get; init; } = 24;
}

/// <summary>"Would TOU be cheaper?" priced on the site's own hourly imports (DESIGN.md §7.4).</summary>
public static class TouAnalyzer
{
    public static TouComparison Compare(TimeSeries hourlyImportKwh, TimeSeries? hourlyExportKwh, TariffConfig current,
        TariffConfig alternative, double? exportRateThb = null, IReadOnlyCollection<DateOnly>? holidays = null,
        TimeZoneInfo? timeZone = null, TouOptions? options = null)
    {
        var o = options ?? new TouOptions();
        var tz = LocalClock.Resolve(timeZone);
        var cur = BillCalculator.Monthly(hourlyImportKwh, current, tz, holidays);
        var alt = BillCalculator.Monthly(hourlyImportKwh, alternative, tz, holidays).ToDictionary(b => (b.Year, b.Month));
        // Peak share is judged with the TOU plan's window, whichever side that is.
        var touPlan = alternative.Kind == TariffKind.TimeOfUse ? alternative : current.Kind == TariffKind.TimeOfUse ? current : PeakWindowOnly(alternative);
        var split = BillCalculator.Monthly(hourlyImportKwh, touPlan, tz, holidays).ToDictionary(b => (b.Year, b.Month));
        var exports = (hourlyExportKwh ?? TimeSeries.Empty).Points
            .GroupBy(p => { var l = LocalClock.ToLocal(p.TsUtc, tz); return (l.Year, l.Month); })
            .ToDictionary(g => g.Key, g => g.Sum(p => Math.Max(0, p.Value)));

        var months = cur.Select(c =>
        {
            var a = alt[(c.Year, c.Month)];
            var s = split[(c.Year, c.Month)];
            var exp = exports.TryGetValue((c.Year, c.Month), out var e) ? e : 0;
            var credit = exp * (exportRateThb ?? 0);
            var delta = a.Total - c.Total;
            return new MonthComparison(c.Year, c.Month, c.Kwh, c.Kwh > 0 ? s.PeakKwh / c.Kwh * 100 : 0, exp, credit,
                c.Total - credit, a.Total - credit, delta, c.Total > 0 ? delta / c.Total * 100 : null);
        }).ToList();

        var recent = months.TakeLast(o.RecentMonths).ToList();
        var rc = recent.Sum(m => m.CurrentBill);
        var ra = recent.Sum(m => m.AlternativeBill);
        var saving = rc - ra;
        double? savingPct = rc > 0 ? saving / rc * 100 : null;
        var importKwh = recent.Sum(m => m.ImportKwh);
        double? peakShare = importKwh > 0 ? recent.Sum(m => m.ImportKwh * m.PeakSharePct / 100) / importKwh * 100 : null;
        var monthlySaving = recent.Count > 0 ? saving / recent.Count : 0;
        var paysBack = o.MeterChangeCostThb <= 0 || monthlySaving * o.MaxPaybackMonths >= o.MeterChangeCostThb;
        var enoughData = recent.Count >= o.RecentMonths;
        var recommend = enoughData && savingPct is { } sp && sp > o.MinSavingPct && paysBack;

        string text;
        if (months.Count == 0)
            text = "ยังไม่มีข้อมูลการซื้อไฟรายชั่วโมง";
        else if (!enoughData)
            text = $"ข้อมูลยังไม่ครบ {o.RecentMonths} เดือน — ยังสรุปไม่ได้";
        else if (recommend)
            text = $"แนะนำเปลี่ยนเป็น {alternative.Name}: {recent.Count} เดือนล่าสุดจะประหยัดได้ประมาณ {saving:#,0} บาท ({savingPct:0.0} %) " +
                   $"เพราะไฟที่ซื้อจากการไฟฟ้าอยู่ในช่วง Peak เพียง {peakShare:0} % — ส่วนใหญ่ซื้อช่วง Off-Peak (กลางคืน/วันหยุด) " +
                   "โดยแบตเตอรี่ช่วยย้ายการซื้อไฟออกจากช่วง Peak";
        else if (savingPct is > 0 && !paysBack)
            text = $"{alternative.Name} ถูกกว่า {savingPct:0.0} % แต่ไม่คุ้มค่าเปลี่ยนมิเตอร์ ({o.MeterChangeCostThb:#,0} บาท) ภายใน {o.MaxPaybackMonths:0} เดือน";
        else
            text = $"ยังไม่คุ้มเปลี่ยนเป็น {alternative.Name}: {recent.Count} เดือนล่าสุด " +
                   (saving >= 0 ? $"ประหยัดได้เพียง {savingPct:0.0} % (เกณฑ์ > {o.MinSavingPct:0} %)" : $"จะแพงขึ้น {-saving:#,0} บาท") +
                   (peakShare is { } ps ? $" — ไฟที่ซื้ออยู่ช่วง Peak {ps:0} %" : "");

        return new TouComparison
        {
            CurrentName = current.Name,
            AlternativeName = alternative.Name,
            Months = months,
            MonthsCompared = recent.Count,
            RecentCurrentTotal = rc,
            RecentAlternativeTotal = ra,
            RecentSavingThb = saving,
            RecentSavingPct = savingPct,
            RecentPeakSharePct = peakShare,
            RecommendSwitch = recommend,
            RecommendationTh = text,
            UsesUnconfirmedRates = current.IsUnconfirmedDefault || alternative.IsUnconfirmedDefault,
        };
    }

    private static TariffConfig PeakWindowOnly(TariffConfig like) => new()
    {
        Kind = TariffKind.TimeOfUse,
        PeakStartHour = like.PeakStartHour,
        PeakEndHour = like.PeakEndHour,
        PeakDays = like.PeakDays,
    };
}
