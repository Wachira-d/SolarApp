namespace SolarApp.Services.Analytics;

public sealed class StringInput
{
    public int Index { get; init; }
    public TimeSeries PowerW { get; init; } = TimeSeries.Empty;
    /// <summary>String size; when every string has one, power is compared per kWp.</summary>
    public double? Kwp { get; init; }
    /// <summary>Panels in series; sharpens the "one panel lost" step test (≈ 1/N).</summary>
    public int? PanelsInString { get; init; }
}

public sealed class StringCompareOptions
{
    public double WindowFromHour { get; init; } = 10;
    public double WindowToHour { get; init; } = 14;
    /// <summary>Total PV must exceed this share of system kWp (low light makes ratios meaningless).</summary>
    public double MinTotalFractionOfKwp { get; init; } = 0.30;
    public TimeSpan Step { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>A string "reports" at a step if it has a sample at most this old.</summary>
    public TimeSpan MaxSampleAge { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>A day-ratio below 1 − this is a deviation.</summary>
    public double DeviationThreshold { get; init; } = 0.05;
    public int PersistDays { get; init; } = 3;
    /// <summary>A sample below this ratio counts as a drop to ~0.</summary>
    public double DropRatio { get; init; } = 0.10;
    /// <summary>Share of a day's samples that must be drops for an "intermittent" day.</summary>
    public double DropDayFraction { get; init; } = 0.05;
    public int MinSamplesPerDay { get; init; } = 6;
}

public enum StringIssue { Soiling, PanelFault, LooseConnection, Underperforming }

public sealed record StringDay(DateOnly Date, int ValidSamples, Dictionary<int, double?> Ratio, Dictionary<int, double> DropFraction);

public sealed record StringFinding(int StringIndex, StringIssue Issue, DateOnly Since, int Days, double? ChangePct, bool Recovered, string MessageTh);

public sealed record StringSummary(int StringIndex, double? LatestRatio, double? MeanRatio7d, KpiGrade Grade);

public sealed class StringComparisonReport
{
    public bool PerKwp { get; init; }
    public List<StringDay> Days { get; init; } = new();
    public List<StringSummary> Strings { get; init; } = new();
    public List<StringFinding> Findings { get; init; } = new();
    public List<string> Notes { get; init; } = new();
}

/// <summary>
/// String-vs-string comparison (DESIGN.md §7.2). Comparing a string to the
/// median of its neighbours cancels weather, so only a fault on that string
/// is left. Only bright, uncurtailed midday samples with every string
/// reporting are used.
/// </summary>
public static class StringComparator
{
    public static StringComparisonReport Compare(IReadOnlyList<StringInput> strings, double systemKwp,
        TimeSeries? totalPvW = null, IReadOnlyList<TimeRange>? curtailed = null, TimeZoneInfo? timeZone = null,
        IReadOnlyCollection<DateOnly>? rainOrCleaningDays = null, StringCompareOptions? options = null)
    {
        var o = options ?? new StringCompareOptions();
        var tz = LocalClock.Resolve(timeZone);
        var report = new StringComparisonReport { PerKwp = strings.Count > 0 && strings.All(s => s.Kwp is > 0) };
        if (strings.Count < 2)
        {
            report.Notes.Add("ต้องมีอย่างน้อย 2 สตริงจึงจะเปรียบเทียบได้");
            return report;
        }
        var starts = strings.Where(s => !s.PowerW.IsEmpty).Select(s => s.PowerW.StartUtc!.Value).ToList();
        if (starts.Count == 0)
        {
            report.Notes.Add("ไม่มีข้อมูลกำลังไฟของสตริง");
            return report;
        }
        var from = starts.Min();
        var to = strings.Where(s => !s.PowerW.IsEmpty).Max(s => s.PowerW.EndUtc!.Value);
        var ranges = curtailed ?? Array.Empty<TimeRange>();
        var idx = strings.Select(s => s.Index).ToList();

        foreach (var day in LocalClock.Days(from, to, tz))
        {
            var ratios = idx.ToDictionary(i => i, _ => new List<double>());
            var ws = LocalClock.AtLocalHour(day, o.WindowFromHour, tz);
            var we = LocalClock.AtLocalHour(day, o.WindowToHour, tz);
            var valid = 0;
            for (var t = ws; t < we; t += o.Step)
            {
                if (ranges.Any(r => r.Contains(t))) continue;
                var vals = new double[strings.Count];
                var ok = true;
                for (var k = 0; k < strings.Count && ok; k++)
                {
                    if (strings[k].PowerW.ValueAt(t, o.MaxSampleAge) is { } v) vals[k] = Math.Max(0, v);
                    else ok = false;   // all strings must be reporting
                }
                if (!ok) continue;
                var total = totalPvW?.ValueAt(t, o.MaxSampleAge) ?? vals.Sum();
                if (systemKwp > 0 && total < o.MinTotalFractionOfKwp * systemKwp * 1000) continue;

                var norm = vals.Select((v, k) => report.PerKwp ? v / strings[k].Kwp!.Value : v).ToArray();
                var med = Stats.Median(norm)!.Value;
                if (med <= 0) continue;
                valid++;
                for (var k = 0; k < strings.Count; k++) ratios[strings[k].Index].Add(norm[k] / med);
            }
            if (valid < o.MinSamplesPerDay) continue;
            report.Days.Add(new StringDay(day, valid,
                ratios.ToDictionary(kv => kv.Key, kv => Stats.Median(kv.Value)),
                ratios.ToDictionary(kv => kv.Key, kv => kv.Value.Count(r => r < o.DropRatio) / (double)kv.Value.Count)));
        }

        if (report.Days.Count == 0)
            report.Notes.Add("ยังไม่มีช่วงที่เข้าเกณฑ์ (10:00–14:00, PV > 30 % ของ kWp, ไม่ถูกจำกัด, ทุกสตริงรายงานค่า)");

        var rain = rainOrCleaningDays?.ToHashSet() ?? new HashSet<DateOnly>();
        foreach (var s in strings)
        {
            var series = report.Days.Where(d => d.Ratio[s.Index].HasValue)
                .Select(d => (d.Date, Ratio: d.Ratio[s.Index]!.Value, Drop: d.DropFraction[s.Index])).ToList();
            var last7 = series.TakeLast(7).Select(x => x.Ratio).ToList();
            double? latest = series.Count > 0 ? series[^1].Ratio : null;
            double? mean7 = last7.Count > 0 ? last7.Average() : null;
            report.Strings.Add(new StringSummary(s.Index, latest, mean7,
                mean7 is { } m ? KpiGrades.LowerIsBetter(1 - m, o.DeviationThreshold, 0.15) : KpiGrade.NotAvailable));

            if (Classify(s, series, rain, o) is { } finding) report.Findings.Add(finding);
        }
        return report;
    }

    private static StringFinding? Classify(StringInput s, List<(DateOnly Date, double Ratio, double Drop)> days,
        HashSet<DateOnly> rain, StringCompareOptions o)
    {
        if (days.Count < o.PersistDays) return null;

        // 1. Intermittent drops to ~0 while the rest of the day looks normal → connector / fuse.
        var dropDays = days.Where(d => d.Drop >= o.DropDayFraction && d.Drop < 0.9).ToList();
        if (dropDays.Count >= o.PersistDays)
        {
            return new StringFinding(s.Index, StringIssue.LooseConnection, dropDays[0].Date, dropDays.Count, null, false,
                $"สตริง {s.Index}: กำลังไฟตกเป็นศูนย์เป็นช่วง ๆ {dropDays.Count} วัน — สงสัยขั้วต่อ MC4 หลวม หรือฟิวส์/สายมีปัญหา ควรให้ช่างตรวจ");
        }

        // 2. A sudden, lasting step down ≈ 1/N → a panel or bypass diode lost.
        var step = BestStep(days.Select(d => d.Ratio).ToList(), o.PersistDays);
        if (step is { } st)
        {
            var expected = s.PanelsInString is > 0 ? 1.0 / s.PanelsInString.Value : (double?)null;
            var plausible = expected is { } e ? Math.Abs(st.Drop - e) <= e * 0.5 : st.Drop is >= 0.04 and <= 0.35;
            if (plausible && st.After >= o.PersistDays)
            {
                var since = days[st.Index].Date;
                var n = expected is { } ex ? Math.Max(1, (int)Math.Round(st.Drop / ex)) : 0;
                var what = n > 0 ? $"ประมาณ {n} แผงจาก {s.PanelsInString}" : "ประมาณ 1 แผง/ไดโอดบายพาส";
                return new StringFinding(s.Index, StringIssue.PanelFault, since, st.After, -st.Drop * 100, false,
                    $"สตริง {s.Index}: กำลังไฟลดลงทันที {st.Drop * 100:0} % ตั้งแต่ {since:yyyy-MM-dd} ({what}) — สงสัยแผงเสียหรือไดโอดบายพาสเสีย");
            }
        }

        // 3. Gradual decline that resets after rain/cleaning → soiling.
        var soil = Soiling(days, rain, o);
        if (soil is not null) return soil with { StringIndex = s.Index, MessageTh = string.Format(soil.MessageTh, s.Index) };

        // 4. Anything else that stays low for ≥ PersistDays.
        var run = 0;
        DateOnly? runStart = null;
        foreach (var d in days)
        {
            if (d.Ratio < 1 - o.DeviationThreshold) { run++; runStart ??= d.Date; }
            else { run = 0; runStart = null; }
        }
        if (run >= o.PersistDays)
        {
            var lastRatio = days[^1].Ratio;
            return new StringFinding(s.Index, StringIssue.Underperforming, runStart!.Value, run, (lastRatio - 1) * 100, false,
                $"สตริง {s.Index}: ผลิตต่ำกว่าสตริงอื่น {(1 - lastRatio) * 100:0} % ต่อเนื่อง {run} วัน — ควรตรวจสอบ");
        }
        return null;
    }

    /// <summary>The split with the largest clean drop: both sides flat, after-side ≥ persist days.</summary>
    private static (int Index, double Drop, int After)? BestStep(List<double> r, int persist)
    {
        (int, double, int)? best = null;
        for (var k = 2; k <= r.Count - persist; k++)
        {
            var before = r.Take(k).ToList();
            var after = r.Skip(k).ToList();
            var mb = before.Average();
            var ma = after.Average();
            var drop = mb - ma;
            if (drop <= 0) continue;
            var sd = Math.Max(Sd(before), Sd(after));
            // Sudden: the change happens between two days, not spread over them.
            if (sd > drop / 4) continue;
            if (after.Any(x => x > mb - drop / 2)) continue;
            if (best is null || drop > best.Value.Item2) best = (k, drop, after.Count);
        }
        return best;
    }

    private static StringFinding? Soiling(List<(DateOnly Date, double Ratio, double Drop)> days, HashSet<DateOnly> rain, StringCompareOptions o)
    {
        var minDecline = Math.Max(0.03, o.DeviationThreshold * 0.6);
        // Recovered episode: a decline followed by a jump back up (after rain/cleaning if we know those days).
        for (var j = days.Count - 1; j >= o.PersistDays; j--)
        {
            var jump = days[j].Ratio - days[j - 1].Ratio;
            var rainNear = rain.Count == 0 || rain.Any(r => r > days[j - 1].Date.AddDays(-1) && r <= days[j].Date);
            if (jump <= 0 || !rainNear) continue;
            var startIdx = Math.Max(0, j - 30);
            var seg = days.Skip(startIdx).Take(j - startIdx).ToList();
            // The decline runs from the segment's best day to the day before the jump.
            var peak = seg.FindIndex(d => d.Ratio == seg.Max(x => x.Ratio));
            seg = seg.Skip(peak).ToList();
            if (seg.Count < o.PersistDays) continue;
            var x = seg.Select((_, i) => (double)i).ToList();
            var fit = Stats.LinearFit(x, seg.Select(d => d.Ratio).ToList());
            var decline = seg[0].Ratio - seg[^1].Ratio;
            if (fit is { A: < 0 } && decline >= minDecline && jump >= decline * 2 / 3 && Gradual(seg, decline))
            {
                return new StringFinding(0, StringIssue.Soiling, seg[0].Date, seg.Count, -decline * 100, true,
                    "สตริง {0}: ผลิตลดลงทีละน้อย " + $"{decline * 100:0} % ใน {seg.Count} วัน แล้วกลับมาปกติหลังฝนตก/ล้างแผง — เป็นคราบสกปรก ควรล้างแผงเป็นระยะ");
            }
        }
        // Ongoing: steady decline over the recent days, not a step.
        var recent = days.TakeLast(Math.Min(days.Count, 14)).ToList();
        if (recent.Count >= Math.Max(o.PersistDays, 5))
        {
            var fit = Stats.LinearFit(recent.Select((_, i) => (double)i).ToList(), recent.Select(d => d.Ratio).ToList());
            var decline = recent[0].Ratio - recent[^1].Ratio;
            var below = recent.TakeLast(o.PersistDays).All(d => d.Ratio < 1 - o.DeviationThreshold);
            if (fit is { A: < 0, R2: >= 0.6 } && decline >= minDecline && below && Gradual(recent, decline))
            {
                return new StringFinding(0, StringIssue.Soiling, recent[0].Date, recent.Count, -decline * 100, false,
                    "สตริง {0}: ผลิตลดลงทีละน้อย " + $"{decline * 100:0} % ใน {recent.Count} วัน — น่าจะเป็นคราบสกปรก แนะนำล้างแผง");
            }
        }
        return null;
    }

    /// <summary>Soiling builds up: no single day may account for more than half of the decline.</summary>
    private static bool Gradual(List<(DateOnly Date, double Ratio, double Drop)> seg, double decline)
    {
        for (var i = 1; i < seg.Count; i++)
            if (seg[i - 1].Ratio - seg[i].Ratio > decline / 2) return false;
        return true;
    }

    private static double Sd(List<double> v)
    {
        if (v.Count < 2) return 0;
        var m = v.Average();
        return Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / (v.Count - 1));
    }
}
