using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SolarApp.Models.Enums;
using SolarApp.Models.Telemetry;
using SolarApp.Pages.Installations;
using SolarApp.Services.Analytics;
using SolarApp.Services.Authorization;
using SolarApp.Services.Telemetry;
using Xunit;

namespace SolarApp.Tests.Services.Analytics;

public class BatteryCellAnalyzerTests
{
    private static readonly DateTime Now = new(2026, 11, 19, 5, 0, 0, DateTimeKind.Utc);

    private static CellReading R(int cell, double v, int minutesAgo = 1) => new(cell, v, Now.AddMinutes(-minutesAgo));

    [Theory]
    [InlineData(3.345, 3345)]
    [InlineData(3345, 3345)]
    [InlineData(3.3449999, 3345)]   // float storage noise
    [InlineData(100, 100000)]        // 100 is still volts; only above 100 is mV
    [InlineData(100.5, 100.5)]
    public void Values_in_volts_or_millivolts_become_millivolts(double stored, double mv) =>
        BatteryCellAnalyzer.ToMillivolts(stored).Should().Be(mv);

    [Theory]
    [InlineData(0, KpiGrade.Good)]
    [InlineData(10, KpiGrade.Good)]
    [InlineData(-10, KpiGrade.Good)]
    [InlineData(10.1, KpiGrade.Watch)]
    [InlineData(30, KpiGrade.Watch)]
    [InlineData(-30, KpiGrade.Watch)]
    [InlineData(30.1, KpiGrade.Poor)]
    [InlineData(-45, KpiGrade.Poor)]
    [InlineData(double.NaN, KpiGrade.NotAvailable)]
    public void Deviation_from_the_median_is_graded_10_and_30_mV(double dev, KpiGrade grade) =>
        BatteryCellAnalyzer.GradeDeviation(dev).Should().Be(grade);

    [Fact]
    public void Median_of_odd_and_even_counts()
    {
        BatteryCellAnalyzer.Median(new double[] { 3, 1, 2 }).Should().Be(2);
        BatteryCellAnalyzer.Median(new double[] { 4, 1, 3, 2 }).Should().Be(2.5);
        BatteryCellAnalyzer.Median(Array.Empty<double>()).Should().BeNull();
    }

    [Fact]
    public void Stale_after_15_minutes()
    {
        BatteryCellAnalyzer.IsStale(Now.AddMinutes(-15), Now).Should().BeFalse();
        BatteryCellAnalyzer.IsStale(Now.AddMinutes(-16), Now).Should().BeTrue();
    }

    [Fact]
    public void Pack_grid_grades_each_cell_against_the_pack_median_and_marks_extremes()
    {
        var readings = new[]
        {
            R(1, 3.330), R(2, 3.331), R(3, 3.345), R(4, 3.329), R(5, 3.290), R(6, 3.332), R(7, 3.330),
        };

        var p = BatteryCellAnalyzer.Pack(1, readings, Now)!;

        p.MedianMv.Should().Be(3330);
        p.DeltaMv.Should().Be(55);
        p.MaxMv.Should().Be(3345);
        p.MinMv.Should().Be(3290);
        p.StaleCount.Should().Be(0);
        p.IsStale.Should().BeFalse();
        p.Cells.Select(c => c.Cell).Should().Equal(1, 2, 3, 4, 5, 6, 7);
        p.Cells.Single(c => c.Cell == 3).Should().Match<CellView>(c => c.Grade == KpiGrade.Watch && c.IsHighest && c.DeviationMv == 15);
        p.Cells.Single(c => c.Cell == 5).Should().Match<CellView>(c => c.Grade == KpiGrade.Poor && c.IsLowest && c.DeviationMv == -40);
        p.Cells.Where(c => c.Cell is not 3 and not 5).Should().OnlyContain(c => c.Grade == KpiGrade.Good && !c.IsHighest && !c.IsLowest);
        p.DeltaGrade.Should().Be(KpiGrade.NotAvailable);   // SOC unknown
    }

    [Fact]
    public void Millivolt_values_work_the_same_as_volts()
    {
        var v = BatteryCellAnalyzer.Pack(1, new[] { R(1, 3.330), R(2, 3.350), R(3, 3.320) }, Now)!;
        var mv = BatteryCellAnalyzer.Pack(1, new[] { R(1, 3330), R(2, 3350), R(3, 3320) }, Now)!;
        mv.Cells.Select(c => (c.Mv, c.DeviationMv, c.Grade)).Should().Equal(v.Cells.Select(c => (c.Mv, c.DeviationMv, c.Grade)));
        mv.DeltaMv.Should().Be(30);
    }

    [Fact]
    public void A_stale_cell_is_marked_and_left_out_of_median_and_delta()
    {
        var readings = new[] { R(1, 3.330), R(2, 3.332), R(3, 3.331), R(4, 3.100, minutesAgo: 40) };

        var p = BatteryCellAnalyzer.Pack(2, readings, Now)!;

        p.StaleCount.Should().Be(1);
        p.IsStale.Should().BeFalse();
        p.DeltaMv.Should().Be(2);
        p.MedianMv.Should().Be(3331);
        p.NewestUtc.Should().Be(Now.AddMinutes(-1));
        var stale = p.Cells.Single(c => c.Cell == 4);
        stale.IsStale.Should().BeTrue();
        stale.IsLowest.Should().BeFalse();
        stale.Grade.Should().Be(KpiGrade.Poor);   // still coloured: it is far from the others
    }

    [Fact]
    public void All_stale_uses_every_cell_and_marks_the_pack()
    {
        var p = BatteryCellAnalyzer.Pack(1, new[] { R(1, 3.330, 60), R(2, 3.340, 60) }, Now)!;
        p.IsStale.Should().BeTrue();
        p.DeltaMv.Should().Be(10);
        p.Cells.Should().Contain(c => c.IsHighest).And.Contain(c => c.IsLowest);
    }

    [Fact]
    public void Equal_cells_have_no_highest_or_lowest_and_duplicates_keep_the_newest()
    {
        var p = BatteryCellAnalyzer.Pack(1, new[] { R(1, 3.330), R(2, 3.330), R(2, 3.200, 5) }, Now)!;
        p.Cells.Should().HaveCount(2);
        p.DeltaMv.Should().Be(0);
        p.Cells.Should().OnlyContain(c => !c.IsHighest && !c.IsLowest);
        BatteryCellAnalyzer.Pack(1, Array.Empty<CellReading>(), Now).Should().BeNull();
    }

    [Fact]
    public void Cells_tied_at_an_extreme_are_all_marked()
    {
        var p = BatteryCellAnalyzer.Pack(1, new[] { R(1, 3330), R(2, 3330), R(3, 3320), R(4, 3325) }, Now)!;
        p.Cells.Where(c => c.IsHighest).Select(c => c.Cell).Should().Equal(1, 2);
        p.Cells.Where(c => c.IsLowest).Select(c => c.Cell).Should().Equal(3);
    }

    [Theory]
    [InlineData(20, 55, null, KpiGrade.Good)]
    [InlineData(21, 55, 50.0, KpiGrade.Watch)]
    [InlineData(51, 30, null, KpiGrade.Poor)]
    [InlineData(20, 55, 800.0, KpiGrade.NotAvailable)]   // not at rest
    [InlineData(20, 90, null, KpiGrade.NotAvailable)]    // between rest and full
    [InlineData(49, 99, null, KpiGrade.Good)]
    [InlineData(50, 100, null, KpiGrade.Watch)]
    [InlineData(151, 100, 2000.0, KpiGrade.Poor)]        // full: power does not matter
    public void Pack_delta_uses_the_rest_and_full_thresholds(double delta, double soc, double? power, KpiGrade grade) =>
        BatteryCellAnalyzer.GradeDelta(delta, soc, power).Grade.Should().Be(grade);

    [Fact]
    public void Pack_delta_without_soc_is_not_graded() =>
        BatteryCellAnalyzer.GradeDelta(80, null, null).Grade.Should().Be(KpiGrade.NotAvailable);

    [Fact]
    public void History_gives_delta_per_bucket_and_finds_the_cell_most_often_lowest()
    {
        var t0 = new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc);
        var series = new Dictionary<int, IReadOnlyList<Point>>();
        for (var cell = 1; cell <= 5; cell++)
        {
            var pts = new List<Point>();
            for (var i = 0; i < 48; i++)
            {
                // Cell 4 is the low one for the first 36 buckets, cell 2 afterwards; cell 1 always high.
                var v = cell switch
                {
                    1 => 3.350,
                    4 when i < 36 => 3.300,
                    2 when i >= 36 => 3.305,
                    _ => 3.330,
                };
                // Raw-style samples: two per bucket, averaged.
                pts.Add(new Point(t0.AddMinutes(5 * i + 1), v - 0.001));
                pts.Add(new Point(t0.AddMinutes(5 * i + 3), v + 0.001));
            }
            series[cell] = pts;
        }
        // A bucket where only 2 of 5 cells reported is ignored.
        series[1] = series[1].Append(new Point(t0.AddHours(10), 3.4)).ToList();
        series[2] = series[2].Append(new Point(t0.AddHours(10), 3.0)).ToList();

        var h = BatteryCellAnalyzer.History(1, series);

        h.CellCount.Should().Be(5);
        h.Buckets.Should().Be(48);
        h.Points[0].Should().Be(new CellDeltaPoint(t0, 50, 4, 1));
        h.Points[^1].DeltaMv.Should().Be(45);
        h.MostOftenLowest.Select(x => (x.Cell, x.Count, x.Pct)).Should().Equal((4, 36, 75.0), (2, 12, 25.0));
        h.MostOftenHighest.Should().ContainSingle().Which.Should().Be(new CellExtremeCount(1, 48, 100));
        h.MedianDeltaMv.Should().Be(50);
        h.MaxDeltaMv.Should().Be(50);
        h.SuspectWeakCell!.Cell.Should().Be(4);
    }

    [Fact]
    public void Balanced_pack_names_no_suspect_and_empty_history_is_safe()
    {
        var t0 = new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc);
        var series = Enumerable.Range(1, 4).ToDictionary(c => c, c => (IReadOnlyList<Point>)Enumerable.Range(0, 30)
            .Select(i => new Point(t0.AddMinutes(5 * i), 3330 + (c == 2 ? -3 : 0))).ToList());
        var h = BatteryCellAnalyzer.History(1, series);
        h.MostOftenLowest[0].Cell.Should().Be(2);
        h.MedianDeltaMv.Should().Be(3);
        h.SuspectWeakCell.Should().BeNull();   // ΔV too small to matter
        h.MostOftenHighest.Should().BeEmpty();  // cells 1, 3 and 4 tie: no cell is credited
        h.Points.Should().OnlyContain(p => p.LowestCell == 2 && p.HighestCell == 0);

        var empty = BatteryCellAnalyzer.History(1, new Dictionary<int, IReadOnlyList<Point>>());
        empty.Buckets.Should().Be(0);
        empty.MedianDeltaMv.Should().BeNull();
        empty.SuspectWeakCell.Should().BeNull();
    }

    [Fact]
    public void Gaps_longer_than_15_minutes_break_the_chart_line()
    {
        var t0 = new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc);
        var pts = new[] { new CellDeltaPoint(t0, 5, 1, 2), new CellDeltaPoint(t0.AddMinutes(5), 6, 1, 2), new CellDeltaPoint(t0.AddHours(1), 7, 1, 2) };
        BatteryCellAnalyzer.WithGaps(pts).Select(p => p.DeltaMv).Should().Equal(5, 6, null, 7);
    }
}

/// <summary>The battery tab's per-cell section, read through the real page model over an InMemory DB and store.</summary>
public class BatteryCellPageTests
{
    internal static void SeedCells(SiteAnalyticsServiceTests.Fixture f, DateTime nowUtc)
    {
        var channels = new List<TelemetryChannel>();
        // Pack 1: 16 cells in volts; cell 7 sags 40 mV, cell 3 sits 15 mV high.
        // Pack 2: 8 cells in millivolts; cell 5 stopped reporting 40 minutes ago.
        foreach (var (pack, count) in new[] { (1, 16), (2, 8) })
            for (var cell = 1; cell <= count; cell++)
            {
                var key = $"battery.pack{pack}.cell{cell}.voltage_v";
                var p = TelemetryKey.Parse(key);
                double value = pack == 1
                    ? cell switch { 7 => 3.290, 3 => 3.345, _ => 3.330 }
                    : 3328 + cell % 3;
                channels.Add(new TelemetryChannel
                {
                    InstallationId = f.Site.Id, Key = key, Kind = p.Kind, DeviceIndex = p.DeviceIndex, SubIndex = p.SubIndex,
                    Quantity = p.Quantity, Unit = p.Unit, FirstTs = nowUtc.AddDays(-8),
                    LastTs = pack == 2 && cell == 5 ? nowUtc.AddMinutes(-40) : nowUtc.AddMinutes(-2), LastValue = value,
                });
            }
        f.Db.TelemetryChannels.AddRange(channels);
        f.Db.SaveChanges();

        // Pack 1 history: 7 days of 5-minute aggregates with cell 7 lowest. Pack 2: raw only (no rollup yet), last 6 hours.
        var aggs = new List<AggregatePoint>();
        var raw = new List<RawSample>();
        var start = new DateTime(nowUtc.Ticks - nowUtc.Ticks % TimeSpan.FromMinutes(5).Ticks, DateTimeKind.Utc).AddDays(-7).AddMinutes(5);
        foreach (var c in channels)
        {
            if (c.DeviceIndex == 1)
                for (var t = start; t.AddMinutes(5) <= nowUtc; t = t.AddMinutes(5))
                {
                    var v = c.SubIndex == 7 ? 3.295 : 3.330;
                    aggs.Add(new AggregatePoint(c.Id, AggregateResolution.FiveMinutes, t, v, v, v, v, v, 5));
                }
            else
                for (var t = nowUtc.AddHours(-6); t < nowUtc; t = t.AddMinutes(1))
                    raw.Add(new RawSample(c.Id, t, c.SubIndex == 2 ? 3310 : 3330));
        }
        f.Store.UpsertAggregatesAsync(aggs).GetAwaiter().GetResult();
        f.Store.AppendRawAsync(raw).GetAwaiter().GetResult();
    }

    private static AnalyticsModel Page(SiteAnalyticsServiceTests.Fixture f)
    {
        var access = new Mock<IInstallationAccessHelper>();
        access.Setup(a => a.CanAccessAsync(It.IsAny<int>(), It.IsAny<ClaimsPrincipal>())).ReturnsAsync(true);
        return new AnalyticsModel(f.Db, access.Object, f.Service, NullLogger<AnalyticsModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() },
        };
    }

    [Fact]
    public async Task Battery_tab_shows_each_pack_grid_and_the_selected_pack_history()
    {
        var f = SiteAnalyticsServiceTests.Build();
        SeedCells(f, DateTime.UtcNow);

        var page = Page(f);
        await page.OnGetAsync(f.Site.Id, "battery");

        page.HasCellVoltages.Should().BeTrue();
        page.CellPacks.Select(p => p.Pack).Should().Equal(1, 2);

        var p1 = page.CellPacks[0];
        p1.Cells.Should().HaveCount(16);
        p1.MedianMv.Should().Be(3330);
        p1.DeltaMv.Should().Be(55);
        p1.StaleCount.Should().Be(0);
        p1.Cells.Single(c => c.IsLowest).Should().Match<CellView>(c => c.Cell == 7 && c.Grade == KpiGrade.Poor && c.DeviationMv == -40);
        p1.Cells.Single(c => c.IsHighest).Should().Match<CellView>(c => c.Cell == 3 && c.Grade == KpiGrade.Watch);
        p1.Cells.Count(c => c.Grade == KpiGrade.Good).Should().Be(14);

        var p2 = page.CellPacks[1];
        p2.Cells.Should().HaveCount(8);
        p2.Cells.Should().OnlyContain(c => c.Mv > 3300 && c.Mv < 3340);   // stored as mV
        p2.StaleCount.Should().Be(1);
        p2.Cells.Single(c => c.IsStale).Cell.Should().Be(5);

        page.CellPack.Should().Be(1);
        var h = page.CellHistory!;
        h.Pack.Should().Be(1);
        h.Buckets.Should().BeInRange(7 * 288 - 2, 7 * 288);
        h.MedianDeltaMv.Should().Be(35);
        h.MostOftenLowest[0].Should().Match<CellExtremeCount>(x => x.Cell == 7 && x.Pct == 100);
        h.SuspectWeakCell!.Cell.Should().Be(7);
        h.MostOftenHighest.Should().BeEmpty();   // the other 15 cells are equal
    }

    [Fact]
    public async Task Pack_query_selects_the_history_and_falls_back_to_raw_before_any_rollup()
    {
        var f = SiteAnalyticsServiceTests.Build();
        SeedCells(f, DateTime.UtcNow);

        var page = Page(f);
        await page.OnGetAsync(f.Site.Id, "battery", pack: 2);

        page.CellPack.Should().Be(2);
        var h = page.CellHistory!;
        h.Pack.Should().Be(2);
        h.CellCount.Should().Be(8);
        h.Buckets.Should().BeInRange(70, 73);   // 6 hours of raw minutes → 5-minute buckets
        h.MedianDeltaMv.Should().Be(20);
        h.MostOftenLowest[0].Cell.Should().Be(2);

        var unknown = Page(f);
        await unknown.OnGetAsync(f.Site.Id, "battery", pack: 9);
        unknown.CellPack.Should().Be(1);
    }

    [Fact]
    public async Task Battery_site_without_cell_channels_has_no_cell_section()
    {
        var f = SiteAnalyticsServiceTests.Build();   // pack cell_v_max / cell_v_min only
        f.Site.Should().NotBeNull();

        var page = Page(f);
        await page.OnGetAsync(f.Site.Id, "battery");

        page.ActiveTab.Should().Be("battery");
        page.Caps.HasCellData.Should().BeTrue();
        page.HasCellVoltages.Should().BeFalse();
        page.CellHistory.Should().BeNull();
        page.CellPack.Should().BeNull();
    }

    [Fact]
    public async Task Other_tabs_do_not_load_cells()
    {
        var f = SiteAnalyticsServiceTests.Build();
        SeedCells(f, DateTime.UtcNow);
        var page = Page(f);
        await page.OnGetAsync(f.Site.Id, "pv");
        page.HasCellVoltages.Should().BeFalse();
    }
}
