using System.Text.RegularExpressions;
using SolarApp.Models.Enums;

namespace SolarApp.Services.Telemetry;

/// <summary>One site-local day of energy for a gateway site, as the existing EnergyData Daily row wants it.</summary>
public sealed record DailyEnergy(
    double? GenerationKwh, double? GridImportKwh, double? GridExportKwh, double? LoadKwh,
    double? BatteryChargeKwh, double? BatteryDischargeKwh)
{
    /// <summary>
    /// PV used on site = generation − export (what the cloud adapters store);
    /// without a PV figure, load − import says the same thing from the other side.
    /// </summary>
    public double? SelfConsumptionKwh =>
        GenerationKwh is { } g ? Math.Max(0, g - (GridExportKwh ?? 0))
        : LoadKwh is { } l && GridImportKwh is { } imp ? Math.Max(0, l - imp)
        : null;

    public bool IsEmpty => GenerationKwh == null && GridImportKwh == null && GridExportKwh == null
                           && LoadKwh == null && BatteryChargeKwh == null && BatteryDischargeKwh == null;
}

/// <summary>
/// Turns a day of telemetry aggregates into the Daily EnergyData figures, so
/// reports, invoices and alerts built on EnergyData work for gateway sites
/// (DESIGN.md §4.4). Pure: the job reads the aggregates, this does the sums.
///
/// Each figure takes the best source the site has:
/// <list type="number">
/// <item>a cumulative meter counter (<c>*_total_kwh</c>) — the increase of its
/// 1-hour aggregates (see <see cref="TelemetryRollup.Increase"/>), which is
/// what the inverter/meter itself measured;</item>
/// <item>for PV only: a "today" counter (<c>pv.energy_today_kwh</c>) bridged across its
/// midnight reset, then the sum of per-inverter totals;</item>
/// <item>integrating the power channel's 5-minute aggregates, split by sign
/// (<c>grid.power_w</c> + import / − export, <c>battery.power_w</c> + charge /
/// − discharge). 5-minute rather than hourly so a grid that flips between
/// import and export inside an hour is not netted away.</item>
/// </list>
/// A figure with no source is null — the caller leaves that column alone.
/// </summary>
public static partial class EnergyDailyBridge
{
    public const string PvTotal = "pv.energy_total_kwh";
    public const string PvToday = "pv.energy_today_kwh";
    public const string GridImportTotal = "grid.import_total_kwh";
    public const string GridExportTotal = "grid.export_total_kwh";
    public const string LoadTotal = "load.energy_total_kwh";
    public const string BatteryChargeTotal = "battery.charge_total_kwh";
    public const string BatteryDischargeTotal = "battery.discharge_total_kwh";

    public const string PvPower = "pv.power_w";
    public const string GridPower = "grid.power_w";
    public const string BatteryPower = "battery.power_w";
    public const string LoadPower = "load.power_w";

    public static readonly IReadOnlyList<string> CounterKeys = new[]
        { PvTotal, PvToday, GridImportTotal, GridExportTotal, LoadTotal, BatteryChargeTotal, BatteryDischargeTotal };

    public static readonly IReadOnlyList<string> PowerKeys = new[] { PvPower, GridPower, BatteryPower, LoadPower };

    [GeneratedRegex(@"^inverter\d+\.energy_total_kwh$")]
    private static partial Regex InverterTotal();

    /// <summary>Keys whose 1-hour aggregates <see cref="Compute"/> reads.</summary>
    public static bool IsCounterKey(string key) => CounterKeys.Contains(key) || InverterTotal().IsMatch(key);

    /// <summary>Keys whose 5-minute aggregates <see cref="Compute"/> reads.</summary>
    public static bool IsPowerKey(string key) => PowerKeys.Contains(key);

    /// <param name="hourlyCounters">1-hour aggregates of counter keys, for the one day.</param>
    /// <param name="fiveMinutePower">5-minute aggregates of power keys, for the one day.</param>
    public static DailyEnergy Compute(
        IReadOnlyDictionary<string, IReadOnlyList<AggregatePoint>> hourlyCounters,
        IReadOnlyDictionary<string, IReadOnlyList<AggregatePoint>> fiveMinutePower)
    {
        double? Counter(string key) =>
            hourlyCounters.TryGetValue(key, out var pts) && pts.Count > 0 ? pts.Sum(TelemetryRollup.Increase) : null;

        (double Pos, double Neg)? Integrate(string key)
        {
            if (!fiveMinutePower.TryGetValue(key, out var pts) || pts.Count == 0) return null;
            double pos = 0, neg = 0;
            foreach (var p in pts)
            {
                var (a, b) = TelemetryRollup.SplitEnergyKwh(p, TelemetryRollup.LengthOf(p.Resolution));
                pos += a;
                neg += b;
            }
            return (pos, neg);
        }

        // PV: meter counter → daily counter → inverters → integrated power.
        var generation = Counter(PvTotal);
        if (generation == null && hourlyCounters.TryGetValue(PvToday, out var today) && today.Count > 0)
            // Not a cumulative channel, so its aggregates hold real readings:
            // walk First/Last of each hour and bridge the reset at midnight
            // (or at inverter wake-up, whichever the brand does).
            generation = TelemetryRollup.IncreaseOfReadings(
                today.OrderBy(p => p.BucketStartUtc).SelectMany(p => new[] { p.First, p.Last }));
        if (generation == null)
        {
            var inverters = hourlyCounters.Where(kv => InverterTotal().IsMatch(kv.Key) && kv.Value.Count > 0).ToList();
            if (inverters.Count > 0) generation = inverters.Sum(kv => kv.Value.Sum(TelemetryRollup.Increase));
        }
        generation ??= Integrate(PvPower)?.Pos;

        var grid = Integrate(GridPower);
        var import = Counter(GridImportTotal) ?? grid?.Pos;
        var export = Counter(GridExportTotal) ?? grid?.Neg;

        var battery = Integrate(BatteryPower);
        var charge = Counter(BatteryChargeTotal) ?? battery?.Pos;
        var discharge = Counter(BatteryDischargeTotal) ?? battery?.Neg;

        var load = Counter(LoadTotal) ?? Integrate(LoadPower)?.Pos;
        // No load channel: the energy balance gives it once PV and grid are known.
        if (load == null && generation != null && import != null && export != null)
            load = Math.Max(0, generation.Value + import.Value - export.Value + (discharge ?? 0) - (charge ?? 0));

        return new DailyEnergy(generation, import, export, load, charge, discharge);
    }
}
