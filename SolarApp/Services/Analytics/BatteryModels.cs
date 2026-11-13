namespace SolarApp.Services.Analytics;

// ───────────────────────────── Inputs ─────────────────────────────

/// <summary>Per-pack channels (battery.pack{n}.*). Every series is optional.</summary>
public sealed class BatteryPackInput
{
    public int Index { get; init; }
    /// <summary>e.g. 10.24 kWh for one FoxESS EP11 module.</summary>
    public double NominalCapacityKwh { get; init; }
    public TimeSeries? Soc { get; init; }
    /// <summary>W, + charge / − discharge.</summary>
    public TimeSeries? PowerW { get; init; }
    /// <summary>Highest / lowest cell voltage in the pack, V (mV values are detected and converted).</summary>
    public TimeSeries? CellVMax { get; init; }
    public TimeSeries? CellVMin { get; init; }
    public TimeSeries? CellTempMaxC { get; init; }
    public TimeSeries? CellTempMinC { get; init; }
    public TimeSeries? Soh { get; init; }
    /// <summary>Pack voltage and current — only needed for the internal-resistance estimate.</summary>
    public TimeSeries? VoltageV { get; init; }
    public TimeSeries? CurrentA { get; init; }
}

/// <summary>e.g. FoxESS EP11: 10 years or 33.4 MWh, whichever first, ≥ 70 % capacity.</summary>
public sealed record BatteryWarranty(double Years, double ThroughputMwh, double MinCapacityPct);

public sealed class BatteryInput
{
    /// <summary>System SOC, % (battery.soc_pct).</summary>
    public TimeSeries Soc { get; init; } = TimeSeries.Empty;
    /// <summary>System battery power, W, + charge / − discharge (battery.power_w).</summary>
    public TimeSeries PowerW { get; init; } = TimeSeries.Empty;
    public IReadOnlyList<BatteryPackInput> Packs { get; init; } = Array.Empty<BatteryPackInput>();
    public TimeSeries? SystemSoh { get; init; }
    /// <summary>1 = grid present, 0 = outage (grid.status).</summary>
    public TimeSeries? GridPresent { get; init; }
    /// <summary>System battery temperature, used when there are no per-pack cell temperatures.</summary>
    public TimeSeries? TemperatureC { get; init; }
    /// <summary>Lifetime discharge counter (battery.discharge_total_kwh); preferred for warranty throughput.</summary>
    public TimeSeries? DischargeTotalKwh { get; init; }
    /// <summary>Total nominal capacity, kWh; 0 → sum of the packs' nominal capacities.</summary>
    public double NominalCapacityKwh { get; init; }
    public BatteryWarranty? Warranty { get; init; }
    public DateTime? InstallDateUtc { get; init; }
    /// <summary>Site time zone for day boundaries; null → SiteTime.TimeZone.</summary>
    public TimeZoneInfo? TimeZone { get; init; }

    public double EffectiveCapacityKwh =>
        NominalCapacityKwh > 0 ? NominalCapacityKwh : Packs.Sum(p => Math.Max(0, p.NominalCapacityKwh));
}

/// <summary>Thresholds of the product owner's definitions; defaults are the agreed values.</summary>
public sealed class BatteryOptions
{
    private readonly TimeSpan _mergeGap = TimeSpan.FromMinutes(7);

    /// <summary>"Now" for 30/90-day windows. Null → the last sample (never the wall clock).</summary>
    public DateTime? AsOfUtc { get; init; }

    public double SessionThresholdW { get; init; } = 300;
    /// <summary>A session must last strictly longer than this.</summary>
    public TimeSpan SessionMinDuration { get; init; } = TimeSpan.FromMinutes(3);
    /// <summary>Pauses shorter than this inside a session are merged; clamped to 5–10 min.</summary>
    public TimeSpan SessionMergeGap
    {
        get => _mergeGap;
        init => _mergeGap = value < TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5)
            : value > TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : value;
    }

    public double FullSocPct { get; init; } = 98;
    public double DeepMaxSocPct { get; init; } = 30;
    public double MidMaxSocPct { get; init; } = 60;

    public TimeSpan CapacityStartWindow { get; init; } = TimeSpan.FromMinutes(30);
    public double CapacityMinDeltaSoc { get; init; } = 50;
    public double CapacityMaxChargeKwh { get; init; } = 0.2;
    public double CapacityTempMinC { get; init; } = 20;
    public double CapacityTempMaxC { get; init; } = 40;
    public int SummaryWindowDays { get; init; } = 30;
    public int CompareLagDays { get; init; } = 90;
    public double SohGapFlagPts { get; init; } = 5;

    /// <summary>
    /// A full-to-full window must move at least this share of capacity in
    /// charge before its efficiency is meaningful (a 100→97→98 blip divides
    /// two rounding errors).
    /// </summary>
    public double RteMinChargedPctOfCapacity { get; init; } = 10;

    public TimeSpan MaxGap { get; init; } = TimeSeries.DefaultMaxGap;

    public double OvernightStartHour { get; init; } = 16;
    /// <summary>Next-morning minimum is searched until this local hour.</summary>
    public double OvernightEndHour { get; init; } = 10;
    public double DaytimeFullFromHour { get; init; } = 7;
    public double DaytimeFullToHour { get; init; } = 18;
    public double FullHoldPct { get; init; } = 99.5;
    public double LowSocPct { get; init; } = 20;
    public double HeatFullHours { get; init; } = 5;
    public double HeatFullTempC { get; init; } = 35;

    /// <summary>Health KPIs look at this many days before AsOf.</summary>
    public int KpiWindowDays { get; init; } = 7;
    public double RestPowerW { get; init; } = 100;
    public double InternalResistanceMinStepA { get; init; } = 10;
    public double TempWarningC { get; init; } = 45;
    public double TempCriticalC { get; init; } = 50;
}

// ───────────────────────────── Outputs ─────────────────────────────

public enum SessionDirection { Charge = 1, Discharge = -1 }

public sealed record BatterySession(SessionDirection Direction, DateTime StartUtc, DateTime EndUtc, double Kwh, double DurationMinutes);

public sealed class SessionSummary
{
    public int ChargeCount { get; init; }
    public int DischargeCount { get; init; }
    public double ChargeKwh { get; init; }
    public double DischargeKwh { get; init; }
    public List<BatterySession> Items { get; init; } = new();
}

public enum CycleDepth { Deep, Mid, Shallow }

/// <summary>Full → full window; the minimum SOC inside is the depth.</summary>
public sealed record FullCycle(DateTime StartUtc, DateTime EndUtc, double StartSoc, double MinSoc, DateTime MinUtc,
    double DodPct, CycleDepth Depth);

public sealed class CycleSummary
{
    public int Deep { get; init; }
    public int Mid { get; init; }
    public int Shallow { get; init; }
    public List<FullCycle> Items { get; init; } = new();
}

/// <param name="QuantizationErrorPct">±% from SOC being an integer (±1 point at each end ÷ ΔSOC).</param>
public sealed record CapacityMeasurement(DateTime StartUtc, DateTime EndUtc, double SocStart, double SocEnd,
    double DischargedKwh, double ChargedInsideKwh, double CapacityKwh, double PctOfNominal,
    double QuantizationErrorPct);

public sealed class CapacitySummary
{
    public List<CapacityMeasurement> Measurements { get; init; } = new();
    public Dictionary<string, int> Rejected { get; init; } = new();
    public double NominalKwh { get; init; }
    public int WindowCount { get; init; }
    public double? MedianKwh { get; init; }
    public double? MedianPct { get; init; }
    /// <summary>± spread: half the inter-quartile range (half the range with fewer than 4 samples).</summary>
    public double? SpreadKwh { get; init; }
    public double? PriorMedianKwh { get; init; }
    public double? ChangeKwh { get; init; }
    public double? ChangePct { get; init; }
    /// <summary>"▲", "▼" or "≈".</summary>
    public string? Trend { get; init; }
    public double? BmsSohPct { get; init; }
    /// <summary>Measured % − BMS SOH, in points.</summary>
    public double? GapToSohPts { get; init; }
    public bool SohGapFlag { get; init; }
    /// <summary>Integer SOC ⇒ one measurement is good to about ±3–6 %, a median of many to ±1–2 %.</summary>
    public string PerCycleErrorPct { get; init; } = "±3–6";
    public string MedianErrorPct { get; init; } = "±1–2";
    public List<string> Notes { get; init; } = new();
}

public enum EfficiencyGrade { Good, Watch, Poor, DataError }

public sealed record RoundTrip(DateTime StartUtc, DateTime EndUtc, double ChargedKwh, double DischargedKwh,
    double? EfficiencyPct, EfficiencyGrade Grade, string? Note);

public sealed class EfficiencySummary
{
    public List<RoundTrip> Items { get; init; } = new();
    /// <summary>Median of valid (non-DataError) round trips in the summary window, else of all valid ones.</summary>
    public double? MedianPct { get; init; }
    public EfficiencyGrade? Grade { get; init; }
}

public sealed class BatteryDay
{
    public DateOnly Date { get; init; }
    public double ChargedKwh { get; init; }
    public double DischargedKwh { get; init; }
    public double? SocMax { get; init; }
    public double? SocMin { get; init; }
    public double? DodPct { get; init; }
    public double EfcToday { get; init; }
    public double? OvernightFromSoc { get; init; }
    public double? OvernightToSoc { get; init; }
    public double? OvernightKwh { get; init; }
    /// <summary>"100 % → 24 %, 22.8 kWh".</summary>
    public string? OvernightText { get; init; }
    /// <summary>Hours at ≥ 99.5 % between 07:00 and 18:00 — the battery is full while the sun still shines (curtailment sign).</summary>
    public double HoursFullDaytime { get; init; }
    public double HoursAtFull { get; init; }
    public double HoursBelowLow { get; init; }
    public double? CRateAvg { get; init; }
    public double? CRateMax { get; init; }
    /// <summary>Max C-rate below 0.5 C.</summary>
    public bool CRateOk { get; init; }
    public double? CellTempMinC { get; init; }
    public double? CellTempMaxC { get; init; }
    /// <summary>&gt; 5 h at 100 % on a day above 35 °C: accelerated calendar ageing (orange).</summary>
    public bool HeatAndFull { get; init; }
    public bool HadOutage { get; init; }
}

public sealed record HealthKpi(string Key, string Name, double? Value, string Unit, KpiGrade Grade, string GradeText, string? Note);

public sealed record TempAlert(int Pack, DateTime TsUtc, double TempC, string Level, string Message);

public sealed class PackReport
{
    public int Index { get; init; }
    public double NominalCapacityKwh { get; init; }
    public double? DischargedKwh { get; init; }
    public double? Efc { get; init; }
    public CapacitySummary? Capacity { get; init; }
    public List<HealthKpi> Kpis { get; init; } = new();
}

public sealed class OverallGrade
{
    /// <summary>"A", "B" or "C".</summary>
    public string Grade { get; init; } = "B";
    public string Label { get; init; } = "";
    public List<string> Reasons { get; init; } = new();
}

public sealed class WarrantyStatus
{
    public double WarrantyYears { get; init; }
    public double WarrantyMwh { get; init; }
    public double MinCapacityPct { get; init; }
    public double ThroughputMwh { get; init; }
    public double ThroughputUsedPct { get; init; }
    public double? YearsElapsed { get; init; }
    public double? YearsLeft { get; init; }
    public double TrailingYearMwh { get; init; }
    /// <summary>True when less than a year of data was annualised.</summary>
    public bool TrailingYearAnnualised { get; init; }
    public double? YearsLeftByThroughput { get; init; }
    public double? ExpectedRemainingYears { get; init; }
    /// <summary>"time" or "throughput" — whichever ends the warranty first.</summary>
    public string? LimitedBy { get; init; }
}

public sealed record DailyHistoryPoint(DateOnly Date, double ChargedKwh, double DischargedKwh, double? SocMin, double? SocMax, bool HadOutage);

/// <summary>A bar of the "all history in one chart": a day, or a Monday-starting week.</summary>
public sealed record HistoryBucket(DateOnly Start, int Days, double ChargedKwh, double DischargedKwh, double? SocMin, double? SocMax, bool HadOutage);

public sealed class HistorySeries
{
    /// <summary>"day" or "week".</summary>
    public string Resolution { get; init; } = "day";
    public List<HistoryBucket> Buckets { get; init; } = new();
}

/// <summary>Mean SOC per (site-local date, hour). Values[d][h] is null where there is no sample.</summary>
public sealed class SocHeatmap
{
    public List<DateOnly> Dates { get; init; } = new();
    public List<double?[]> Values { get; init; } = new();
}

public sealed class BatteryReport
{
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
    public DateTime? AsOfUtc { get; init; }
    public double NominalCapacityKwh { get; init; }
    public double TotalChargedKwh { get; init; }
    public double TotalDischargedKwh { get; init; }
    public double Efc { get; init; }
    public SessionSummary Sessions { get; init; } = new();
    public CycleSummary Cycles { get; init; } = new();
    public DodHistogram DodHistogram { get; init; } = Rainflow.Histogram(Array.Empty<RainflowCycle>());
    public CapacitySummary Capacity { get; init; } = new();
    public EfficiencySummary Efficiency { get; init; } = new();
    public List<BatteryDay> Days { get; init; } = new();
    public List<PackReport> Packs { get; init; } = new();
    public List<TempAlert> Alerts { get; init; } = new();
    public OverallGrade Overall { get; init; } = new();
    public WarrantyStatus? Warranty { get; init; }
    public List<DailyHistoryPoint> History { get; init; } = new();
    public SocHeatmap Heatmap { get; init; } = new();
    public List<string> Notes { get; init; } = new();
}
