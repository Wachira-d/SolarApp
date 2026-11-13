namespace SolarApp.Services.Analytics;

/// <summary>
/// Battery report (DESIGN.md §7.1). Pure: the same input always gives the
/// same report, "now" is the last sample unless <see cref="BatteryOptions.AsOfUtc"/>
/// says otherwise.
/// </summary>
public static class BatteryAnalyzer
{
    private static readonly TimeSpan AlignTolerance = TimeSpan.FromMinutes(5);

    public static BatteryReport Analyze(BatteryInput input, BatteryOptions? options = null)
    {
        var o = options ?? new BatteryOptions();
        var tz = LocalClock.Resolve(input.TimeZone);
        var cap = input.EffectiveCapacityKwh;
        var soc = input.Soc ?? TimeSeries.Empty;
        var p = input.PowerW ?? TimeSeries.Empty;

        var starts = new[] { soc.StartUtc, p.StartUtc }.Where(t => t.HasValue).Select(t => t!.Value).ToList();
        var ends = new[] { soc.EndUtc, p.EndUtc }.Where(t => t.HasValue).Select(t => t!.Value).ToList();
        if (starts.Count == 0)
        {
            return new BatteryReport
            {
                NominalCapacityKwh = cap,
                Overall = new OverallGrade { Grade = "-", Label = "ไม่มีข้อมูล", Reasons = { "ไม่มีข้อมูลแบตเตอรี่ (SOC / กำลังไฟ)" } },
                Notes = { "ไม่มีข้อมูลแบตเตอรี่ (SOC / กำลังไฟ)" },
            };
        }
        var from = starts.Min();
        var to = ends.Max();
        var asOf = o.AsOfUtc ?? to;
        var notes = new List<string>();
        if (cap <= 0) notes.Add("ไม่ได้ระบุความจุแบตเตอรี่ — ไม่คำนวณ EFC / ความจุ / C-rate");
        if (p.IsEmpty) notes.Add("ไม่มีข้อมูลกำลังไฟแบตเตอรี่ — ไม่คำนวณพลังงานเข้า/ออก");

        var charged = p.IntegrateKwh(from, to, Pos, o.MaxGap);
        var discharged = p.IntegrateKwh(from, to, Neg, o.MaxGap);

        var sessions = FindSessions(p, o);
        var fulls = FullMoments(soc, o.FullSocPct);
        var cycles = FullCycles(soc, fulls, o);
        var efficiency = RoundTrips(p, fulls, cap, asOf, o);

        var systemTemps = input.Packs.SelectMany(k => new[] { k.CellTempMaxC, k.CellTempMinC })
            .Append(input.TemperatureC).Where(t => t is { IsEmpty: false }).Cast<TimeSeries>().ToList();
        var measurements = MeasureCapacity(soc, p, systemTemps, cap, o, out var rejected);
        var sohNow = CurrentSoh(input, asOf);
        var capacity = SummariseCapacity(measurements, rejected, cap, sohNow, asOf, o);

        var days = DailyMetrics(input, soc, p, cap, tz, from, to, o);
        var packs = PackReports(input, asOf, o, out var alerts);
        var overall = Grade(sohNow, capacity, efficiency, packs, alerts, o);

        var rainflow = Rainflow.Count(soc.Points.Select(x => x.Value));

        return new BatteryReport
        {
            FromUtc = from,
            ToUtc = to,
            AsOfUtc = asOf,
            NominalCapacityKwh = cap,
            TotalChargedKwh = charged,
            TotalDischargedKwh = discharged,
            Efc = cap > 0 ? discharged / cap : 0,
            Sessions = new SessionSummary
            {
                ChargeCount = sessions.Count(s => s.Direction == SessionDirection.Charge),
                DischargeCount = sessions.Count(s => s.Direction == SessionDirection.Discharge),
                ChargeKwh = sessions.Where(s => s.Direction == SessionDirection.Charge).Sum(s => s.Kwh),
                DischargeKwh = sessions.Where(s => s.Direction == SessionDirection.Discharge).Sum(s => s.Kwh),
                Items = sessions,
            },
            Cycles = new CycleSummary
            {
                Deep = cycles.Count(c => c.Depth == CycleDepth.Deep),
                Mid = cycles.Count(c => c.Depth == CycleDepth.Mid),
                Shallow = cycles.Count(c => c.Depth == CycleDepth.Shallow),
                Items = cycles,
            },
            DodHistogram = Rainflow.Histogram(rainflow),
            Capacity = capacity,
            Efficiency = efficiency,
            Days = days,
            Packs = packs,
            Alerts = alerts,
            Overall = overall,
            Warranty = Warranty(input, p, discharged, asOf),
            History = days.Select(d => new DailyHistoryPoint(d.Date, d.ChargedKwh, d.DischargedKwh, d.SocMin, d.SocMax, d.HadOutage)).ToList(),
            Heatmap = Heatmap(soc, tz),
            Notes = notes,
        };
    }

    private static double Pos(double v) => v > 0 ? v : 0;
    private static double Neg(double v) => v < 0 ? -v : 0;

    // ───────────── Sessions ─────────────

    /// <summary>
    /// Runs of |P| &gt; threshold in one direction lasting &gt; 3 min, then
    /// same-direction runs separated by &lt; 7 min merged. Filtering BEFORE
    /// merging is what keeps zero-export noon oscillation (±P flipping every
    /// minute) from becoming a session.
    /// </summary>
    public static List<BatterySession> FindSessions(TimeSeries power, BatteryOptions? options = null)
    {
        var o = options ?? new BatteryOptions();
        var pts = power.Points;
        int Dir(double v) => v > o.SessionThresholdW ? 1 : v < -o.SessionThresholdW ? -1 : 0;

        var runs = new List<(int Dir, DateTime Start, DateTime End)>();
        var i = 0;
        while (i < pts.Count)
        {
            var d = Dir(pts[i].Value);
            var j = i;
            while (j + 1 < pts.Count && Dir(pts[j + 1].Value) == d && pts[j + 1].TsUtc - pts[j].TsUtc <= o.MaxGap) j++;
            // A run lasts until the sample that ended it (if that sample is not across a gap).
            var end = j + 1 < pts.Count && pts[j + 1].TsUtc - pts[j].TsUtc <= o.MaxGap ? pts[j + 1].TsUtc : pts[j].TsUtc;
            if (d != 0 && end - pts[i].TsUtc > o.SessionMinDuration) runs.Add((d, pts[i].TsUtc, end));
            i = j + 1;
        }

        var merged = new List<(int Dir, DateTime Start, DateTime End)>();
        foreach (var r in runs)
        {
            if (merged.Count > 0 && merged[^1].Dir == r.Dir && r.Start - merged[^1].End < o.SessionMergeGap)
                merged[^1] = (r.Dir, merged[^1].Start, r.End);
            else merged.Add(r);
        }

        return merged.Select(r => new BatterySession(
            r.Dir > 0 ? SessionDirection.Charge : SessionDirection.Discharge,
            r.Start, r.End,
            power.IntegrateKwh(r.Start, r.End, r.Dir > 0 ? Pos : Neg, o.MaxGap),
            (r.End - r.Start).TotalMinutes)).ToList();
    }

    // ───────────── Full cycles and round-trip efficiency ─────────────

    /// <summary>Moments SOC reaches ≥ full from below (the first sample counts if already full).</summary>
    public static List<DateTime> FullMoments(TimeSeries soc, double fullPct = 98)
    {
        var list = new List<DateTime>();
        var pts = soc.Points;
        for (var i = 0; i < pts.Count; i++)
            if (pts[i].Value >= fullPct && (i == 0 || pts[i - 1].Value < fullPct)) list.Add(pts[i].TsUtc);
        return list;
    }

    private static List<FullCycle> FullCycles(TimeSeries soc, List<DateTime> fulls, BatteryOptions o)
    {
        var cycles = new List<FullCycle>();
        for (var k = 0; k + 1 < fulls.Count; k++)
        {
            var w = soc.Between(fulls[k], fulls[k + 1]).Points;
            if (w.Count == 0) continue;
            var startSoc = w.TakeWhile(x => x.Value >= o.FullSocPct).Select(x => x.Value).DefaultIfEmpty(w[0].Value).Max();
            var min = w.MinBy(x => x.Value);   // MinBy returns the first minimum
            var depth = min.Value <= o.DeepMaxSocPct ? CycleDepth.Deep
                : min.Value <= o.MidMaxSocPct ? CycleDepth.Mid : CycleDepth.Shallow;
            cycles.Add(new FullCycle(fulls[k], fulls[k + 1], startSoc, min.Value, min.TsUtc, startSoc - min.Value, depth));
        }
        return cycles;
    }

    /// <summary>
    /// Discharged ÷ charged between consecutive full moments. Dividing daily
    /// totals is wrong (a day ends at a different SOC than it began); two
    /// "full" moments are the only points where stored energy is known equal.
    /// </summary>
    private static EfficiencySummary RoundTrips(TimeSeries p, List<DateTime> fulls, double cap, DateTime asOf, BatteryOptions o)
    {
        var items = new List<RoundTrip>();
        if (p.IsEmpty) return new EfficiencySummary();
        for (var k = 0; k + 1 < fulls.Count; k++)
        {
            var s = fulls[k];
            var e = fulls[k + 1];
            var ch = p.IntegrateKwh(s, e, Pos, o.MaxGap);
            var dis = p.IntegrateKwh(s, e, Neg, o.MaxGap);
            if (cap > 0 && ch < cap * o.RteMinChargedPctOfCapacity / 100.0) continue;
            if (cap <= 0 && ch <= 0) continue;

            var gappy = p.ValueAt(s, o.MaxGap) is null || p.IndexAtOrBefore(e) < 0
                        || p.LargestGap(s, e) > o.MaxGap
                        || (p.EndUtc is { } pe && pe < e - o.MaxGap);
            if (gappy)
            {
                items.Add(new RoundTrip(s, e, ch, dis, null, EfficiencyGrade.DataError, "ข้อมูลกำลังไฟขาดช่วง — ไม่คำนวณประสิทธิภาพ"));
                continue;
            }
            var eff = ch > 0 ? dis / ch * 100 : (double?)null;
            if (eff is null or > 100)
            {
                items.Add(new RoundTrip(s, e, ch, dis, eff, EfficiencyGrade.DataError,
                    "เกิน 100 % — ข้อมูลผิดพลาด (ไม่ใช่ประสิทธิภาพ)"));
                continue;
            }
            items.Add(new RoundTrip(s, e, ch, dis, eff, GradeEfficiency(eff.Value), null));
        }

        var valid = items.Where(r => r.Grade != EfficiencyGrade.DataError && r.EfficiencyPct.HasValue).ToList();
        var recent = valid.Where(r => r.EndUtc > asOf.AddDays(-o.SummaryWindowDays) && r.EndUtc <= asOf).ToList();
        var median = Stats.Median((recent.Count > 0 ? recent : valid).Select(r => r.EfficiencyPct!.Value));
        return new EfficiencySummary
        {
            Items = items,
            MedianPct = median,
            Grade = median is { } m ? GradeEfficiency(m) : null,
        };
    }

    public static EfficiencyGrade GradeEfficiency(double pct) =>
        pct > 100 ? EfficiencyGrade.DataError
        : pct >= 92 ? EfficiencyGrade.Good
        : pct >= 88 ? EfficiencyGrade.Watch
        : EfficiencyGrade.Poor;

    // ───────────── Measured capacity ─────────────

    /// <summary>
    /// Each qualifying discharge: starts within 30 min after SOC ≥ 98 %, runs
    /// until the battery charges again (≥ 0.2 kWh) or data stops, falls ≥ 50
    /// points, and stays within 20–40 °C if temperatures exist.
    /// </summary>
    public static List<CapacityMeasurement> MeasureCapacity(TimeSeries soc, TimeSeries power, IReadOnlyList<TimeSeries> temps,
        double nominalKwh, BatteryOptions o, out Dictionary<string, int> rejected)
    {
        var rej = new Dictionary<string, int>();
        rejected = rej;
        var result = new List<CapacityMeasurement>();
        if (soc.IsEmpty || power.Count < 2 || nominalKwh <= 0) return result;
        void Reject(string why) => rej[why] = rej.TryGetValue(why, out var n) ? n + 1 : 1;

        var pts = power.Points;
        var skipUntil = DateTime.MinValue;
        for (var i = 0; i < pts.Count; i++)
        {
            var isStart = pts[i].Value < -o.SessionThresholdW && (i == 0 || pts[i - 1].Value >= -o.SessionThresholdW);
            if (!isStart || pts[i].TsUtc <= skipUntil) continue;
            var t0 = pts[i].TsUtc;

            // SOC was ≥ full at some point in the 30 min before (or at) the start.
            var recentMax = Math.Max(soc.ValueAt(t0 - o.CapacityStartWindow) ?? double.MinValue,
                soc.Between(t0 - o.CapacityStartWindow, t0.AddTicks(1)).Max() ?? double.MinValue);
            if (recentMax < o.FullSocPct) continue;

            // Walk forward until charging accumulates past the limit or the data has a gap.
            double chargeSoFar = 0;
            var tStop = t0;
            for (var k = i; k + 1 < pts.Count; k++)
            {
                var a = pts[k];
                var b = pts[k + 1];
                if (b.TsUtc - a.TsUtc > o.MaxGap) break;
                var seg = (Pos(a.Value) + Pos(b.Value)) / 2 * (b.TsUtc - a.TsUtc).TotalHours / 1000;
                if (chargeSoFar + seg >= o.CapacityMaxChargeKwh) break;
                chargeSoFar += seg;
                tStop = b.TsUtc;
            }

            var socStart = soc.ValueAt(t0);
            var window = soc.Between(t0, tStop.AddTicks(1)).Points;
            if (socStart is null || window.Count == 0) { Reject("no_soc"); continue; }
            var min = window.MinBy(x => x.Value);
            skipUntil = min.TsUtc > t0 ? min.TsUtc : tStop;
            var dSoc = socStart.Value - min.Value;
            if (dSoc < o.CapacityMinDeltaSoc) { Reject("delta_soc_too_small"); continue; }

            var dis = power.IntegrateKwh(t0, min.TsUtc, Neg, o.MaxGap);
            var chIn = power.IntegrateKwh(t0, min.TsUtc, Pos, o.MaxGap);
            if (chIn >= o.CapacityMaxChargeKwh) { Reject("charging_inside"); continue; }

            var tempsInWindow = temps.SelectMany(t => t.Between(t0, min.TsUtc.AddTicks(1)).Points).Select(x => x.Value).ToList();
            if (tempsInWindow.Count > 0 &&
                (tempsInWindow.Min() < o.CapacityTempMinC || tempsInWindow.Max() > o.CapacityTempMaxC))
            {
                Reject("temperature_out_of_range");
                continue;
            }

            var capKwh = dis / (dSoc / 100.0);
            result.Add(new CapacityMeasurement(t0, min.TsUtc, socStart.Value, min.Value, dis, chIn, capKwh,
                capKwh / nominalKwh * 100, 200.0 / dSoc));
        }
        return result;
    }

    private static CapacitySummary SummariseCapacity(List<CapacityMeasurement> m, Dictionary<string, int> rejected,
        double nominal, double? soh, DateTime asOf, BatteryOptions o)
    {
        var notes = new List<string>
        {
            "SOC เป็นจำนวนเต็ม: ความจุที่วัดได้แต่ละรอบคลาดเคลื่อน ±3–6 %, ค่ามัธยฐานหลายรอบ ±1–2 %",
        };
        var recent = m.Where(x => x.EndUtc > asOf.AddDays(-o.SummaryWindowDays) && x.EndUtc <= asOf).Select(x => x.CapacityKwh).ToList();
        var priorTo = asOf.AddDays(-o.CompareLagDays);
        var prior = m.Where(x => x.EndUtc > priorTo.AddDays(-o.SummaryWindowDays) && x.EndUtc <= priorTo).Select(x => x.CapacityKwh).ToList();

        var median = Stats.Median(recent);
        var priorMedian = Stats.Median(prior);
        double? spread = recent.Count switch
        {
            0 => null,
            < 4 => (recent.Max() - recent.Min()) / 2,
            _ => (Stats.Percentile(recent, 75)!.Value - Stats.Percentile(recent, 25)!.Value) / 2,
        };
        double? medianPct = median is { } md && nominal > 0 ? md / nominal * 100 : null;
        double? change = median is { } a && priorMedian is { } b ? a - b : null;
        double? changePct = change is { } c && priorMedian is > 0 ? c / priorMedian.Value * 100 : null;
        string? trend = changePct switch
        {
            null => null,
            > 0.5 => "▲",
            < -0.5 => "▼",
            _ => "≈",
        };
        double? gap = medianPct is { } mp && soh is { } s ? mp - s : null;
        var flag = gap is { } g && Math.Abs(g) > o.SohGapFlagPts;
        if (flag) notes.Add($"ความจุที่วัดได้ต่างจาก SOH ของ BMS {gap:+0.0;-0.0} จุด (> {o.SohGapFlagPts:0}) — ควรตรวจสอบ/ปรับเทียบ BMS");
        if (m.Count == 0) notes.Add("ยังไม่มีรอบคายประจุที่เข้าเกณฑ์วัดความจุ (เริ่มจาก ≥ 98 %, ลดลง ≥ 50 จุด)");

        return new CapacitySummary
        {
            Measurements = m,
            Rejected = rejected,
            NominalKwh = nominal,
            WindowCount = recent.Count,
            MedianKwh = median,
            MedianPct = medianPct,
            SpreadKwh = spread,
            PriorMedianKwh = priorMedian,
            ChangeKwh = change,
            ChangePct = changePct,
            Trend = trend,
            BmsSohPct = soh,
            GapToSohPts = gap,
            SohGapFlag = flag,
            Notes = notes,
        };
    }

    private static double? CurrentSoh(BatteryInput input, DateTime asOf)
    {
        if (input.SystemSoh?.ValueAt(asOf) is { } s) return s;
        var packs = input.Packs.Select(k => k.Soh?.ValueAt(asOf)).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return packs.Count > 0 ? packs.Average() : null;
    }

    // ───────────── Daily metrics ─────────────

    private static List<BatteryDay> DailyMetrics(BatteryInput input, TimeSeries soc, TimeSeries p, double cap,
        TimeZoneInfo tz, DateTime from, DateTime to, BatteryOptions o)
    {
        var days = new List<BatteryDay>();
        var maxTemps = input.Packs.Select(k => k.CellTempMaxC).Where(t => t is { IsEmpty: false }).Cast<TimeSeries>().ToList();
        var minTemps = input.Packs.Select(k => k.CellTempMinC).Where(t => t is { IsEmpty: false }).Cast<TimeSeries>().ToList();
        if (maxTemps.Count == 0 && input.TemperatureC is { IsEmpty: false } sysT) { maxTemps.Add(sysT); minTemps.Add(sysT); }
        if (minTemps.Count == 0) minTemps = maxTemps;

        foreach (var day in LocalClock.Days(from, to, tz))
        {
            var ds = LocalClock.DayStartUtc(day, tz);
            var de = LocalClock.DayStartUtc(day.AddDays(1), tz);
            var ch = p.IntegrateKwh(ds, de, Pos, o.MaxGap);
            var dis = p.IntegrateKwh(ds, de, Neg, o.MaxGap);
            var daySoc = soc.Between(ds, de);

            // Overnight: evening max after 16:00, then the minimum before mid-morning.
            double? oFrom = null, oTo = null, oKwh = null;
            string? oText = null;
            var evening = soc.Between(LocalClock.AtLocalHour(day, o.OvernightStartHour, tz), de).Points;
            if (evening.Count > 0)
            {
                var max = evening.MaxBy(x => x.Value);
                var morning = soc.Between(max.TsUtc.AddTicks(1), LocalClock.AtLocalHour(day, 24 + o.OvernightEndHour, tz).AddTicks(1)).Points;
                if (morning.Count > 0)
                {
                    var min = morning.MinBy(x => x.Value);
                    if (min.Value < max.Value)
                    {
                        oFrom = max.Value;
                        oTo = min.Value;
                        oKwh = p.IntegrateKwh(max.TsUtc, min.TsUtc, Neg, o.MaxGap);
                        oText = $"{max.Value:0} % → {min.Value:0} %, {oKwh:0.0} kWh";
                    }
                }
            }

            var dayP = p.Between(ds, de).Points;
            double? cAvg = null, cMax = null;
            if (cap > 0 && dayP.Count > 0)
            {
                var active = dayP.Where(x => Math.Abs(x.Value) > o.SessionThresholdW).Select(x => Math.Abs(x.Value)).ToList();
                cAvg = active.Count > 0 ? active.Average() / (cap * 1000) : 0;
                cMax = dayP.Max(x => Math.Abs(x.Value)) / (cap * 1000);
            }

            double? tMax = maxTemps.Select(t => t.Between(ds, de).Max()).Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(double.NaN).Max();
            double? tMin = minTemps.Select(t => t.Between(ds, de).Min()).Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(double.NaN).Min();
            if (tMax is double.NaN) tMax = null;
            if (tMin is double.NaN) tMin = null;

            var hoursFull = soc.DurationWhere(v => v >= o.FullHoldPct, ds, de, o.MaxGap).TotalHours;
            var outage = input.GridPresent is { IsEmpty: false } g &&
                         (g.Between(ds, de).Points.Any(x => x.Value < 0.5) || g.ValueAt(ds) is < 0.5);

            if (daySoc.IsEmpty && dayP.Count == 0 && !outage) continue;

            days.Add(new BatteryDay
            {
                Date = day,
                ChargedKwh = ch,
                DischargedKwh = dis,
                SocMax = daySoc.Max(),
                SocMin = daySoc.Min(),
                DodPct = daySoc.IsEmpty ? null : daySoc.Max() - daySoc.Min(),
                EfcToday = cap > 0 ? dis / cap : 0,
                OvernightFromSoc = oFrom,
                OvernightToSoc = oTo,
                OvernightKwh = oKwh,
                OvernightText = oText,
                HoursFullDaytime = soc.DurationWhere(v => v >= o.FullHoldPct,
                    LocalClock.AtLocalHour(day, o.DaytimeFullFromHour, tz), LocalClock.AtLocalHour(day, o.DaytimeFullToHour, tz), o.MaxGap).TotalHours,
                HoursAtFull = hoursFull,
                HoursBelowLow = soc.DurationWhere(v => v < o.LowSocPct, ds, de, o.MaxGap).TotalHours,
                CRateAvg = cAvg,
                CRateMax = cMax,
                CRateOk = cMax is null or < 0.5,
                CellTempMinC = tMin,
                CellTempMaxC = tMax,
                HeatAndFull = hoursFull > o.HeatFullHours && tMax is { } hot && hot > o.HeatFullTempC,
                HadOutage = outage,
            });
        }
        return days;
    }

    // ───────────── Per-pack health KPIs ─────────────

    private static List<PackReport> PackReports(BatteryInput input, DateTime asOf, BatteryOptions o, out List<TempAlert> alerts)
    {
        alerts = new List<TempAlert>();
        var wFrom = asOf.AddDays(-o.KpiWindowDays);
        var wTo = asOf.AddTicks(1);
        var packs = input.Packs.OrderBy(k => k.Index).ToList();

        // Pack-vs-packs needs everyone's number first.
        Dictionary<int, double>? packMetric = null;
        var metricIsKwh = false;
        if (packs.Count >= 2)
        {
            if (packs.All(k => k.PowerW is { Count: >= 2 }))
            {
                packMetric = packs.ToDictionary(k => k.Index, k => k.PowerW!.IntegrateKwh(wFrom, wTo, Neg, o.MaxGap));
                metricIsKwh = true;
            }
            else if (packs.All(k => k.Soc is { IsEmpty: false } s && s.Between(wFrom, wTo).Count > 0))
                packMetric = packs.ToDictionary(k => k.Index, k => k.Soc!.Between(wFrom, wTo).Mean()!.Value);
        }

        var reports = new List<PackReport>();
        foreach (var pk in packs)
        {
            var kpis = new List<HealthKpi>();
            var pPow = pk.PowerW ?? input.PowerW;
            var pSoc = pk.Soc ?? input.Soc;

            // Cell ΔV at rest and at full.
            var restDv = new List<double>();
            var fullDv = new List<double>();
            if (pk.CellVMax is { IsEmpty: false } vmax && pk.CellVMin is { IsEmpty: false } vmin)
            {
                foreach (var pt in vmax.Between(wFrom, wTo).Points)
                {
                    if (vmin.ValueAt(pt.TsUtc, AlignTolerance) is not { } lo) continue;
                    var dv = (ToVolts(pt.Value) - ToVolts(lo)) * 1000;
                    var s = pSoc?.ValueAt(pt.TsUtc, AlignTolerance);
                    var pw = pPow?.ValueAt(pt.TsUtc, AlignTolerance);
                    if (s is >= 99) fullDv.Add(dv);
                    if (s is >= 30 and <= 80 && pw is { } w && Math.Abs(w) < o.RestPowerW) restDv.Add(dv);
                }
            }
            var rest = Stats.Median(restDv);
            kpis.Add(Kpi("cell_dv_rest", "ผลต่างแรงดันเซลล์ขณะพัก", rest, "mV",
                KpiGrades.LowerIsBetter(rest, 20, 50), rest is null ? "ต้องมีแรงดันเซลล์สูงสุด/ต่ำสุด และช่วงพัก (|P| < 100 W, SOC 30–80 %)" : null));
            var full = Stats.Median(fullDv);
            kpis.Add(Kpi("cell_dv_full", "ผลต่างแรงดันเซลล์ขณะเต็ม", full, "mV",
                KpiGrades.LowerIsBetter(full, 50, 150, goodInclusive: false), full is null ? "ต้องมีช่วง SOC ≥ 99 %" : null));

            // Temperatures.
            double? maxT = null, spread = null;
            if (pk.CellTempMaxC is { IsEmpty: false } tmax)
            {
                var w = tmax.Between(wFrom, wTo);
                maxT = w.Max();
                var alertedWarn = false;
                foreach (var pt in w.Points)
                {
                    if (pt.Value >= o.TempCriticalC)
                    {
                        alerts.Add(new TempAlert(pk.Index, pt.TsUtc, pt.Value, "critical",
                            $"แบตเตอรี่ชุด {pk.Index}: อุณหภูมิเซลล์ {pt.Value:0.0} °C (≥ {o.TempCriticalC:0}) — วิกฤต ต้องแจ้งช่างทันที"));
                        break;   // one critical alert per pack is enough
                    }
                    if (pt.Value >= o.TempWarningC && !alertedWarn)
                    {
                        alertedWarn = true;
                        alerts.Add(new TempAlert(pk.Index, pt.TsUtc, pt.Value, "warning",
                            $"แบตเตอรี่ชุด {pk.Index}: อุณหภูมิเซลล์ {pt.Value:0.0} °C (≥ {o.TempWarningC:0}) — เฝ้าระวัง"));
                    }
                }
                if (pk.CellTempMinC is { IsEmpty: false } tmin)
                {
                    var spreads = w.Points.Select(x => tmin.ValueAt(x.TsUtc, AlignTolerance) is { } lo ? x.Value - lo : double.NaN)
                        .Where(double.IsFinite).ToList();
                    spread = Stats.Percentile(spreads, 95);
                }
            }
            kpis.Add(Kpi("cell_temp_max", "อุณหภูมิเซลล์สูงสุด", maxT, "°C", KpiGrades.LowerIsBetter(maxT, 35, 45), null));
            kpis.Add(Kpi("cell_temp_spread", "ผลต่างอุณหภูมิในชุด", spread, "°C", KpiGrades.LowerIsBetter(spread, 3, 6), null));

            // SOH trend, points lost per year over the last 90 days.
            double? sohTrend = null;
            if (pk.Soh is { IsEmpty: false } soh && soh.StartUtc <= asOf.AddDays(-o.CompareLagDays)
                && soh.ValueAt(asOf) is { } now && soh.ValueAt(asOf.AddDays(-o.CompareLagDays)) is { } old)
                sohTrend = (old - now) * 365.0 / o.CompareLagDays;
            kpis.Add(Kpi("soh_trend", "SOH ลดลงต่อปี", sohTrend, "จุด/ปี", KpiGrades.LowerIsBetter(sohTrend, 2, 4),
                sohTrend is null ? "ต้องมีข้อมูล SOH ย้อนหลัง ≥ 90 วัน" : null));

            // Pack vs mean of packs.
            double? dev = null;
            if (packMetric is not null)
            {
                var mean = packMetric.Values.Average();
                if (metricIsKwh && mean > 0) dev = Math.Abs(packMetric[pk.Index] - mean) / mean * 100;
                else if (!metricIsKwh) dev = Math.Abs(packMetric[pk.Index] - mean);
            }
            kpis.Add(Kpi("pack_vs_packs", "ต่างจากค่าเฉลี่ยของชุดอื่น", dev, "%",
                KpiGrades.LowerIsBetter(dev, 3, 7, goodInclusive: false),
                packMetric is null ? "ต้องมีแบตเตอรี่ ≥ 2 ชุด" : metricIsKwh ? "เทียบพลังงานที่คายประจุ" : "เทียบ SOC เฉลี่ย"));

            // Internal resistance vs first month.
            var ir = InternalResistanceChange(pk, asOf, o);
            kpis.Add(Kpi("internal_resistance", "ความต้านทานภายในเทียบเดือนแรก", ir, "%",
                KpiGrades.LowerIsBetter(ir, 20, 50), ir is null ? "ต้องมีแรงดันและกระแสของชุด ย้อนหลัง ≥ 60 วัน" : null));

            double? packDis = pk.PowerW is { Count: >= 2 } pp && pp.StartUtc is { } ps
                ? pp.IntegrateKwh(ps, pp.EndUtc!.Value, Neg, o.MaxGap) : null;
            CapacitySummary? packCap = null;
            if (pk.Soc is { IsEmpty: false } && pk.PowerW is { Count: >= 2 } && pk.NominalCapacityKwh > 0)
            {
                var temps = new[] { pk.CellTempMaxC, pk.CellTempMinC }.Where(t => t is { IsEmpty: false }).Cast<TimeSeries>().ToList();
                var m = MeasureCapacity(pk.Soc, pk.PowerW, temps, pk.NominalCapacityKwh, o, out var rej);
                packCap = SummariseCapacity(m, rej, pk.NominalCapacityKwh, pk.Soh?.ValueAt(asOf), asOf, o);
            }

            reports.Add(new PackReport
            {
                Index = pk.Index,
                NominalCapacityKwh = pk.NominalCapacityKwh,
                DischargedKwh = packDis,
                Efc = packDis is { } d && pk.NominalCapacityKwh > 0 ? d / pk.NominalCapacityKwh : null,
                Capacity = packCap,
                Kpis = kpis,
            });
        }
        return reports;
    }

    /// <summary>BMS registers report cell voltage in V or mV depending on brand; no cell is above 20 V.</summary>
    private static double ToVolts(double v) => v > 20 ? v / 1000.0 : v;

    private static HealthKpi Kpi(string key, string name, double? value, string unit, KpiGrade grade, string? note) =>
        new(key, name, value, unit, grade, KpiGrades.Thai(grade), note);

    /// <summary>
    /// ΔV/ΔI on load steps (|ΔI| ≥ 10 A within 2 min), median of the first
    /// 30 days vs median of the last 30 days, as % change. Null without pack
    /// voltage+current or without two separate 30-day windows.
    /// </summary>
    public static double? InternalResistanceChange(BatteryPackInput pk, DateTime asOf, BatteryOptions o)
    {
        if (pk.VoltageV is not { Count: >= 2 } v || pk.CurrentA is not { Count: >= 2 } cur) return null;
        var first = cur.StartUtc!.Value;
        var baseEnd = first.AddDays(30);
        var recentStart = asOf.AddDays(-30);
        if (recentStart < baseEnd) return null;

        List<double> Steps(DateTime f, DateTime t)
        {
            var r = new List<double>();
            var pts = cur.Between(f, t).Points;
            for (var i = 1; i < pts.Count; i++)
            {
                var a = pts[i - 1];
                var b = pts[i];
                if (b.TsUtc - a.TsUtc > TimeSpan.FromMinutes(2)) continue;
                var di = b.Value - a.Value;
                if (Math.Abs(di) < o.InternalResistanceMinStepA) continue;
                if (v.ValueAt(a.TsUtc, TimeSpan.FromMinutes(1)) is not { } va || v.ValueAt(b.TsUtc, TimeSpan.FromMinutes(1)) is not { } vb) continue;
                r.Add(Math.Abs((vb - va) / di));
            }
            return r;
        }

        var baseSteps = Steps(first, baseEnd);
        var recentSteps = Steps(recentStart, asOf.AddTicks(1));
        if (baseSteps.Count < 3 || recentSteps.Count < 3) return null;
        var b0 = Stats.Median(baseSteps)!.Value;
        if (b0 <= 0) return null;
        return (Stats.Median(recentSteps)!.Value / b0 - 1) * 100;
    }

    // ───────────── Overall grade ─────────────

    private static OverallGrade Grade(double? soh, CapacitySummary cap, EfficiencySummary eff, List<PackReport> packs,
        List<TempAlert> alerts, BatteryOptions o)
    {
        var reasons = new List<string>();
        var capPct = cap.MedianPct ?? Stats.Median(cap.Measurements.Select(m => m.PctOfNominal));
        var effPct = eff.MedianPct;
        var dvs = packs.SelectMany(k => k.Kpis).Where(k => k.Key == "cell_dv_rest" && k.Value.HasValue).Select(k => k.Value!.Value).ToList();
        double? dv = dvs.Count > 0 ? dvs.Max() : null;
        var temps = packs.SelectMany(k => k.Kpis).Where(k => k.Key == "cell_temp_max" && k.Value.HasValue).Select(k => k.Value!.Value).ToList();
        double? maxTemp = temps.Count > 0 ? temps.Max() : null;

        var poor = packs.SelectMany(k => k.Kpis.Where(x => x.Grade == KpiGrade.Poor).Select(x => $"ชุด {k.Index}: {x.Name} {Fmt(x.Value)} {x.Unit} ({x.GradeText})")).ToList();
        if (eff.Grade == EfficiencyGrade.Poor) poor.Add($"ประสิทธิภาพไป-กลับ {effPct:0.0} % (< 88 %)");
        var hot = maxTemp is { } mt && mt > o.TempCriticalC;
        if (poor.Count > 0 || hot)
        {
            reasons.AddRange(poor);
            if (hot) reasons.Add($"อุณหภูมิเซลล์สูงสุด {maxTemp:0.0} °C (> {o.TempCriticalC:0} °C)");
            return new OverallGrade { Grade = "C", Label = "ต้องแจ้งช่าง", Reasons = reasons };
        }

        var checks = new List<(string Name, double? Value, bool Pass, string Rule)>
        {
            ("SOH", soh, soh >= 90, "≥ 90 %"),
            ("ความจุที่วัดได้", capPct, capPct >= 90, "≥ 90 %"),
            ("ประสิทธิภาพไป-กลับ", effPct, effPct >= 90, "≥ 90 %"),
            ("ผลต่างแรงดันเซลล์", dv, dv < 30, "< 30 mV"),
        };
        var available = checks.Where(c => c.Value.HasValue).ToList();
        foreach (var c in checks)
        {
            reasons.Add(c.Value is null
                ? $"{c.Name}: ไม่มีข้อมูล (ไม่นำมาคิด)"
                : $"{c.Name} {c.Value:0.0} {(c.Pass ? "ผ่าน" : "ไม่ผ่าน")} เกณฑ์ {c.Rule}");
        }
        // A needs every available criterion to pass, and at least two to judge on.
        if (available.Count >= 2 && available.All(c => c.Pass))
            return new OverallGrade { Grade = "A", Label = "ดีมาก", Reasons = reasons };
        if (available.Count < 2) reasons.Add("ข้อมูลไม่พอให้เกรด A (ต้องมีอย่างน้อย 2 เกณฑ์)");
        return new OverallGrade { Grade = "B", Label = "ปกติ — เฝ้าระวัง", Reasons = reasons };
    }

    private static string Fmt(double? v) => v is { } x ? x.ToString("0.0") : "-";

    // ───────────── Warranty ─────────────

    private static WarrantyStatus? Warranty(BatteryInput input, TimeSeries p, double dischargedKwh, DateTime asOf)
    {
        if (input.Warranty is not { } w) return null;
        var counter = input.DischargeTotalKwh is { IsEmpty: false } c ? c : null;
        var throughputKwh = counter?.ValueAt(asOf) ?? counter?.Points[^1].Value ?? dischargedKwh;

        // Trailing 12 months; annualise when we hold less than a year.
        var yearStart = asOf.AddDays(-365);
        var dataStart = counter?.StartUtc ?? p.StartUtc ?? asOf;
        var coveredStart = dataStart > yearStart ? dataStart : yearStart;
        var spanDays = (asOf - coveredStart).TotalDays;
        var trailingKwh = counter is not null
            ? (counter.ValueAt(asOf) ?? counter.Points[^1].Value) - (counter.ValueAt(coveredStart) ?? counter.Points[0].Value)
            : p.IntegrateKwh(coveredStart, asOf, Neg);
        var annualised = spanDays < 365 - 1e-9;
        var annualMwh = spanDays >= 1 ? Math.Max(0, trailingKwh) / 1000 * (annualised ? 365 / spanDays : 1) : 0;

        double? elapsed = input.InstallDateUtc is { } inst ? (asOf - inst).TotalDays / 365.25 : null;
        double? yearsLeft = elapsed is { } e ? Math.Max(0, w.Years - e) : null;
        var throughputMwh = throughputKwh / 1000;
        var remainingMwh = Math.Max(0, w.ThroughputMwh - throughputMwh);
        double? byThroughput = annualMwh > 0 ? remainingMwh / annualMwh : null;
        double? expected = (yearsLeft, byThroughput) switch
        {
            ({ } a, { } b) => Math.Min(a, b),
            ({ } a, null) => a,
            (null, { } b) => b,
            _ => null,
        };
        string? limitedBy = (yearsLeft, byThroughput) switch
        {
            ({ } a, { } b) => b < a ? "throughput" : "time",
            ({ }, null) => "time",
            (null, { }) => "throughput",
            _ => null,
        };

        return new WarrantyStatus
        {
            WarrantyYears = w.Years,
            WarrantyMwh = w.ThroughputMwh,
            MinCapacityPct = w.MinCapacityPct,
            ThroughputMwh = throughputMwh,
            ThroughputUsedPct = w.ThroughputMwh > 0 ? throughputMwh / w.ThroughputMwh * 100 : 0,
            YearsElapsed = elapsed,
            YearsLeft = yearsLeft,
            TrailingYearMwh = annualMwh,
            TrailingYearAnnualised = annualised,
            YearsLeftByThroughput = byThroughput,
            ExpectedRemainingYears = expected,
            LimitedBy = limitedBy,
        };
    }

    // ───────────── Heatmap ─────────────

    private static SocHeatmap Heatmap(TimeSeries soc, TimeZoneInfo tz)
    {
        var map = new SortedDictionary<DateOnly, (double Sum, int N)[]>();
        foreach (var pt in soc.Points)
        {
            var local = LocalClock.ToLocal(pt.TsUtc, tz);
            var d = DateOnly.FromDateTime(local);
            if (!map.TryGetValue(d, out var row)) map[d] = row = new (double, int)[24];
            row[local.Hour] = (row[local.Hour].Sum + pt.Value, row[local.Hour].N + 1);
        }
        var hm = new SocHeatmap();
        foreach (var (d, row) in map)
        {
            hm.Dates.Add(d);
            hm.Values.Add(row.Select(c => c.N > 0 ? c.Sum / c.N : (double?)null).ToArray());
        }
        return hm;
    }
}

/// <summary>Bars for the battery "all history in one chart" (DESIGN.md §8).</summary>
public static class BatteryHistory
{
    /// <summary>
    /// Daily bars, or Monday-starting weekly bars once the range is longer
    /// than <paramref name="maxDailyDays"/> (365): 3 years of daily bars are
    /// unreadable on a phone.
    /// </summary>
    public static HistorySeries Buckets(IReadOnlyList<DailyHistoryPoint> days, int maxDailyDays = 365)
    {
        if (days.Count == 0) return new HistorySeries();
        var ordered = days.OrderBy(d => d.Date).ToList();
        var rangeDays = ordered[^1].Date.DayNumber - ordered[0].Date.DayNumber + 1;
        if (rangeDays <= maxDailyDays)
        {
            return new HistorySeries
            {
                Resolution = "day",
                Buckets = ordered.Select(d => new HistoryBucket(d.Date, 1, d.ChargedKwh, d.DischargedKwh, d.SocMin, d.SocMax, d.HadOutage)).ToList(),
            };
        }
        var weeks = ordered
            .GroupBy(d => d.Date.AddDays(-(((int)d.Date.DayOfWeek + 6) % 7)))
            .Select(g => new HistoryBucket(g.Key, g.Count(), g.Sum(x => x.ChargedKwh), g.Sum(x => x.DischargedKwh),
                g.Where(x => x.SocMin.HasValue).Select(x => x.SocMin).DefaultIfEmpty(null).Min(),
                g.Where(x => x.SocMax.HasValue).Select(x => x.SocMax).DefaultIfEmpty(null).Max(),
                g.Any(x => x.HadOutage)))
            .ToList();
        return new HistorySeries { Resolution = "week", Buckets = weeks };
    }
}
