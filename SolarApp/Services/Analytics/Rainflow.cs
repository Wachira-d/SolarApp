namespace SolarApp.Services.Analytics;

/// <summary>One counted rainflow cycle: range (depth) in the input's units, and 1.0 (full) or 0.5 (half).</summary>
public sealed record RainflowCycle(double Range, double Mean, double Count);

/// <summary>One 10 %-wide depth-of-discharge bin.</summary>
public sealed record DodBin(int FromPct, int ToPct, double Cycles);

public sealed record DodHistogram(IReadOnlyList<DodBin> Bins, double TotalCycles);

/// <summary>
/// ASTM E1049-85 §5.4.4 rainflow counting on SOC turning points. Battery
/// ageing depends on the depth of each swing, not on how many kWh moved, so
/// a day of 100→40→45→25→100 is one 75 % cycle plus one 5 % cycle — not
/// "two discharges".
/// </summary>
public static class Rainflow
{
    /// <summary>Reduce a signal to its turning points (peaks and valleys), dropping flats.</summary>
    public static List<double> TurningPoints(IEnumerable<double> values)
    {
        var tp = new List<double>();
        foreach (var v in values)
        {
            if (!double.IsFinite(v)) continue;
            if (tp.Count == 0) { tp.Add(v); continue; }
            if (v == tp[^1]) continue;                          // flat: not a reversal
            if (tp.Count >= 2 && (tp[^1] - tp[^2]) * (v - tp[^1]) > 0)
                tp[^1] = v;                                      // same direction: extend
            else
                tp.Add(v);
        }
        return tp;
    }

    /// <summary>ASTM three-point rainflow. Residue ranges count as half cycles.</summary>
    public static List<RainflowCycle> Count(IEnumerable<double> values)
    {
        var cycles = new List<RainflowCycle>();
        var stack = new List<double>();
        foreach (var p in TurningPoints(values))
        {
            stack.Add(p);
            while (stack.Count >= 3)
            {
                var x = Math.Abs(stack[^1] - stack[^2]);
                var y = Math.Abs(stack[^2] - stack[^3]);
                if (x < y) break;
                if (stack.Count == 3)
                {
                    // Y contains the starting point: half cycle, drop the first point.
                    cycles.Add(new RainflowCycle(y, (stack[0] + stack[1]) / 2, 0.5));
                    stack.RemoveAt(0);
                }
                else
                {
                    cycles.Add(new RainflowCycle(y, (stack[^2] + stack[^3]) / 2, 1.0));
                    stack.RemoveAt(stack.Count - 2);
                    stack.RemoveAt(stack.Count - 2);
                }
            }
        }
        for (var i = 1; i < stack.Count; i++)
            cycles.Add(new RainflowCycle(Math.Abs(stack[i] - stack[i - 1]), (stack[i] + stack[i - 1]) / 2, 0.5));
        return cycles;
    }

    /// <summary>Bins 0–10 … 90–100 %; a 100 % swing goes in the last bin, zero-range cycles are ignored.</summary>
    public static DodHistogram Histogram(IEnumerable<RainflowCycle> cycles)
    {
        var counts = new double[10];
        foreach (var c in cycles)
        {
            if (c.Range <= 0) continue;
            var bin = Math.Clamp((int)Math.Floor(c.Range / 10.0), 0, 9);
            counts[bin] += c.Count;
        }
        var bins = Enumerable.Range(0, 10).Select(i => new DodBin(i * 10, i * 10 + 10, counts[i])).ToList();
        return new DodHistogram(bins, counts.Sum());
    }
}
