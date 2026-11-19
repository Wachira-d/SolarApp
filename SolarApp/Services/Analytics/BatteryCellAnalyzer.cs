namespace SolarApp.Services.Analytics;

/// <summary>Latest reading of one cell, as stored (volts or millivolts).</summary>
public readonly record struct CellReading(int Cell, double Value, DateTime TsUtc);

/// <summary>One cell in the per-pack grid.</summary>
public sealed record CellView(int Cell, double Mv, double DeviationMv, KpiGrade Grade,
    bool IsHighest, bool IsLowest, bool IsStale, DateTime TsUtc);

/// <summary>
/// The latest cell voltages of one pack. Median, ΔV and the highest/lowest
/// cell come from the fresh cells (≥ 2 of them), so one cell stuck at an old
/// value does not show up as an imbalance; with fewer fresh cells every cell
/// is used and the pack is marked stale.
/// </summary>
public sealed record CellPackView(int Pack, List<CellView> Cells, double MedianMv, double MinMv, double MaxMv,
    double DeltaMv, DateTime NewestUtc, int StaleCount, KpiGrade DeltaGrade, string DeltaNote)
{
    /// <summary>Nothing in this pack is fresh: the grid shows the last values the BMS sent.</summary>
    public bool IsStale => StaleCount == Cells.Count;
}

/// <summary>ΔV of one pack in one 5-minute bucket, with the cells at either end (0 when several cells tie there).</summary>
public readonly record struct CellDeltaPoint(DateTime BucketUtc, double DeltaMv, int LowestCell, int HighestCell);

/// <summary>How often a cell alone was the pack's lowest (or highest); Pct is of all buckets counted.</summary>
public sealed record CellExtremeCount(int Cell, int Count, double Pct);

public sealed record CellHistoryReport(int Pack, int CellCount, List<CellDeltaPoint> Points,
    List<CellExtremeCount> MostOftenLowest, List<CellExtremeCount> MostOftenHighest,
    double? MedianDeltaMv, double? MaxDeltaMv)
{
    public int Buckets => Points.Count;

    /// <summary>
    /// A cell that is the lowest most of the time while the pack is clearly
    /// out of balance is the usual sign of a weak cell (less capacity than its
    /// neighbours). Null when no cell stands out.
    /// </summary>
    public CellExtremeCount? SuspectWeakCell =>
        Buckets >= BatteryCellAnalyzer.MinBucketsForSuspect && MedianDeltaMv >= BatteryCellAnalyzer.NeutralMv
        && MostOftenLowest.FirstOrDefault() is { Pct: >= BatteryCellAnalyzer.SuspectPct } c ? c : null;
}

/// <summary>
/// Per-cell battery view (DESIGN.md §3 battery.pack{n}.cell{m}.voltage_v,
/// §11 phase 2). Pure — the page feeds it channel catalog values and store
/// series — so the grading, median and staleness rules are unit-tested.
/// </summary>
public static class BatteryCellAnalyzer
{
    /// <summary>Deviation from the pack median up to this (inclusive) is normal.</summary>
    public const double NeutralMv = 10;
    /// <summary>Deviation up to this (inclusive) is "watch"; beyond it "poor".</summary>
    public const double WatchMv = 30;
    /// <summary>Readings older than this are shown as stale.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Bucket = TimeSpan.FromMinutes(5);
    /// <summary>A bucket counts only when at least this share of the pack's cells reported in it.</summary>
    public const double MinCoverage = 0.8;
    public const double SuspectPct = 50;
    /// <summary>Two hours of 5-minute buckets before one cell is named as suspect.</summary>
    public const int MinBucketsForSuspect = 24;
    /// <summary>Cells closer than this count as equal when picking the highest / lowest.</summary>
    public const double TieMv = 0.5;

    /// <summary>Millivolts from a stored value: anything above 100 is already mV, otherwise volts.</summary>
    public static double ToMillivolts(double value) =>
        Math.Round(Math.Abs(value) > 100 ? value : value * 1000, 1);

    public static double? Median(IEnumerable<double> values) => Stats.Median(values);

    /// <summary>Within 10 mV of the median: normal (Good); 10–30 mV: Watch; more than 30 mV: Poor.</summary>
    public static KpiGrade GradeDeviation(double deviationMv)
    {
        if (!double.IsFinite(deviationMv)) return KpiGrade.NotAvailable;
        var d = Math.Abs(deviationMv);
        return d <= NeutralMv ? KpiGrade.Good : d <= WatchMv ? KpiGrade.Watch : KpiGrade.Poor;
    }

    public static bool IsStale(DateTime tsUtc, DateTime nowUtc) => nowUtc - tsUtc > StaleAfter;

    /// <summary>
    /// Grade the pack's ΔV with the thresholds of the battery KPIs, which only
    /// mean something in two states: at rest (SOC 30–80 %, |P| &lt; 100 W) —
    /// good ≤ 20 mV, watch 20–50, poor &gt; 50; and full (SOC ≥ 99 %) — good
    /// &lt; 50, watch 50–150, poor &gt; 150. Otherwise not graded.
    /// </summary>
    public static (KpiGrade Grade, string Note) GradeDelta(double deltaMv, double? socPct, double? powerW)
    {
        if (socPct is not { } soc) return (KpiGrade.NotAvailable, "ไม่ทราบ SOC ตอนนี้ จึงไม่ให้เกรด ΔV");
        if (soc >= 99) return (KpiGrades.LowerIsBetter(deltaMv, 50, 150, goodInclusive: false), $"แบตเต็ม (SOC {soc:0} %) เกณฑ์ดี < 50 mV");
        if (soc is >= 30 and <= 80 && (powerW is not { } p || Math.Abs(p) < 100))
            return (KpiGrades.LowerIsBetter(deltaMv, 20, 50), $"ช่วงพัก (SOC {soc:0} %) เกณฑ์ดี ≤ 20 mV");
        return (KpiGrade.NotAvailable, $"SOC {soc:0} %{(powerW is { } pw && Math.Abs(pw) >= 100 ? " กำลังชาร์จ/จ่าย" : "")} — ให้เกรด ΔV เฉพาะตอนพัก (SOC 30–80 %) หรือตอนเต็ม (≥ 99 %)");
    }

    /// <summary>The latest-reading grid of one pack, or null when it has no readings.</summary>
    public static CellPackView? Pack(int pack, IEnumerable<CellReading> readings, DateTime nowUtc,
        double? socPct = null, double? powerW = null)
    {
        // One reading per cell: the newest wins if a cell arrived under two keys (cell7 / cell07).
        var cells = readings
            .Where(r => r.Cell > 0 && double.IsFinite(r.Value))
            .GroupBy(r => r.Cell)
            .Select(g => g.OrderByDescending(r => r.TsUtc).First())
            .OrderBy(r => r.Cell)
            .Select(r => (r.Cell, Mv: ToMillivolts(r.Value), r.TsUtc, Stale: IsStale(r.TsUtc, nowUtc)))
            .ToList();
        if (cells.Count == 0) return null;

        var fresh = cells.Where(c => !c.Stale).ToList();
        var basis = fresh.Count >= 2 ? fresh : cells;
        var median = Median(basis.Select(c => c.Mv))!.Value;
        var min = basis.Min(c => c.Mv);
        var max = basis.Max(c => c.Mv);
        var delta = Math.Round(max - min, 1);
        // Every fresh cell at an extreme is marked (BMS values are whole mV, so ties are common).
        var multi = basis.Count >= 2 && delta > TieMv;
        var inBasis = basis.Select(c => c.Cell).ToHashSet();

        var views = cells.Select(c =>
        {
            var dev = Math.Round(c.Mv - median, 1);
            var ext = multi && inBasis.Contains(c.Cell);
            return new CellView(c.Cell, c.Mv, dev, GradeDeviation(dev),
                IsHighest: ext && max - c.Mv <= TieMv, IsLowest: ext && c.Mv - min <= TieMv, c.Stale, c.TsUtc);
        }).ToList();

        var (grade, note) = basis.Count >= 2 ? GradeDelta(delta, socPct, powerW) : (KpiGrade.NotAvailable, "มีเซลล์เดียว");
        return new CellPackView(pack, views, Math.Round(median, 1), min, max, delta,
            cells.Max(c => c.TsUtc), cells.Count(c => c.Stale), grade, note);
    }

    /// <summary>
    /// ΔV (max − min across the pack's cells) per 5-minute bucket and how often
    /// each cell was the lowest / highest. Input is each cell's series as read
    /// from the store — 5-minute aggregates (one point per bucket) and/or raw
    /// samples, which are averaged into their bucket. Values in V or mV.
    /// </summary>
    public static CellHistoryReport History(int pack, IReadOnlyDictionary<int, IReadOnlyList<Point>> cellSeries, int top = 3)
    {
        var cellCount = cellSeries.Count(kv => kv.Key > 0);
        var need = Math.Max(2, (int)Math.Ceiling(cellCount * MinCoverage));
        var byBucket = new SortedDictionary<DateTime, Dictionary<int, (double Sum, int N)>>();
        foreach (var (cell, points) in cellSeries)
        {
            if (cell <= 0) continue;
            foreach (var p in points)
            {
                if (!double.IsFinite(p.Value)) continue;
                var b = Floor(p.TsUtc);
                if (!byBucket.TryGetValue(b, out var cells)) byBucket[b] = cells = new();
                var mv = ToMillivolts(p.Value);
                cells[cell] = cells.TryGetValue(cell, out var acc) ? (acc.Sum + mv, acc.N + 1) : (mv, 1);
            }
        }

        var points2 = new List<CellDeltaPoint>();
        var low = new Dictionary<int, int>();
        var high = new Dictionary<int, int>();
        foreach (var (bucket, cells) in byBucket)
        {
            if (cells.Count < need) continue;
            var avg = cells.Select(kv => (Cell: kv.Key, Mv: kv.Value.Sum / kv.Value.N)).OrderBy(c => c.Cell).ToList();
            var minMv = avg.Min(c => c.Mv);
            var maxMv = avg.Max(c => c.Mv);
            // A cell is credited only when it alone is at the extreme; a tie says nothing about which cell is weak.
            var lowCell = Sole(avg.Where(c => c.Mv - minMv <= TieMv));
            var highCell = Sole(avg.Where(c => maxMv - c.Mv <= TieMv));
            if (maxMv - minMv <= TieMv) lowCell = highCell = 0;
            points2.Add(new CellDeltaPoint(bucket, Math.Round(maxMv - minMv, 1), lowCell, highCell));
            if (lowCell > 0) low[lowCell] = low.GetValueOrDefault(lowCell) + 1;
            if (highCell > 0) high[highCell] = high.GetValueOrDefault(highCell) + 1;
        }

        List<CellExtremeCount> Rank(Dictionary<int, int> counts) => counts
            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(top)
            .Select(kv => new CellExtremeCount(kv.Key, kv.Value, Math.Round(100.0 * kv.Value / points2.Count, 1)))
            .ToList();

        return new CellHistoryReport(pack, cellCount, points2, Rank(low), Rank(high),
            points2.Count > 0 ? Median(points2.Select(p => p.DeltaMv)) : null,
            points2.Count > 0 ? points2.Max(p => p.DeltaMv) : null);
    }

    /// <summary>The ΔV points with a null inserted wherever readings stop for longer than <paramref name="maxGap"/>, so a chart does not draw a line across an outage.</summary>
    public static List<(DateTime TsUtc, double? DeltaMv)> WithGaps(IReadOnlyList<CellDeltaPoint> points, TimeSpan? maxGap = null)
    {
        var gap = maxGap ?? StaleAfter;
        var list = new List<(DateTime, double?)>(points.Count + 8);
        for (var i = 0; i < points.Count; i++)
        {
            if (i > 0 && points[i].BucketUtc - points[i - 1].BucketUtc > gap)
                list.Add((points[i - 1].BucketUtc + Bucket, null));
            list.Add((points[i].BucketUtc, points[i].DeltaMv));
        }
        return list;
    }

    private static int Sole(IEnumerable<(int Cell, double Mv)> cells)
    {
        using var e = cells.GetEnumerator();
        if (!e.MoveNext()) return 0;
        var first = e.Current.Cell;
        return e.MoveNext() ? 0 : first;
    }

    private static DateTime Floor(DateTime t)
    {
        var utc = t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime();
        return new DateTime(utc.Ticks - utc.Ticks % Bucket.Ticks, DateTimeKind.Utc);
    }
}
