using FluentAssertions;
using SolarApp.Services.Analytics;
using Xunit;

namespace SolarApp.Tests.Services.Analytics;

public class ClearSkyTests
{
    private const double Lat = 13.75, Lon = 100.5;   // Bangkok

    [Fact]
    public void Equinox_noon_zenith_equals_latitude_in_bangkok()
    {
        var day = TD.Utc(2026, 3, 20);
        var best = Enumerable.Range(0, 24 * 60).Select(m => day.AddMinutes(m))
            .Select(t => (T: t, P: ClearSky.Position(t, Lat, Lon))).MinBy(x => x.P.ZenithDeg);
        best.P.ZenithDeg.Should().BeApproximately(Lat, 0.7);
        // Solar noon at 100.5° E is ~12:25 local = ~05:25 UTC.
        best.T.TimeOfDay.TotalMinutes.Should().BeInRange(5 * 60 + 15, 5 * 60 + 35);
        best.P.DeclinationDeg.Should().BeApproximately(0, 0.6);
    }

    [Fact]
    public void November_noon_elevation_and_morning_azimuth()
    {
        // 13 Nov: declination ≈ −18°, so noon zenith ≈ 13.75 + 18 ≈ 31.9°.
        var noon = ClearSky.Position(TD.Utc(2026, 11, 13, 5, 0), Lat, Lon);
        noon.ZenithDeg.Should().BeApproximately(31.9, 1.5);
        var morning = ClearSky.Position(TD.Local(2026, 3, 20, 8), Lat, Lon);
        morning.AzimuthDeg.Should().BeInRange(80, 100);   // due east at the equinox
        var evening = ClearSky.Position(TD.Local(2026, 3, 20, 16), Lat, Lon);
        evening.AzimuthDeg.Should().BeInRange(250, 290);
    }

    [Fact]
    public void Haurwitz_ghi_values()
    {
        ClearSky.HaurwitzGhi(0).Should().BeApproximately(1098 * Math.Exp(-0.059), 1e-9);   // ≈ 1035 W/m²
        ClearSky.HaurwitzGhi(90).Should().Be(0);
        ClearSky.Ghi(TD.Local(2026, 3, 20, 23), Lat, Lon).Should().Be(0);
        ClearSky.Ghi(TD.Local(2026, 3, 20, 12, 25), Lat, Lon).Should().BeInRange(950, 1030);
        // A clear day in Bangkok gives ~6.5–7.5 kWh/m².
        ClearSky.InsolationKwhM2(TD.Local(2026, 3, 20), TD.Local(2026, 3, 21), Lat, Lon).Should().BeInRange(6.3, 7.8);
    }
}

public class ExpectedPvModelTests
{
    private const double Lat = 13.75, Lon = 100.5, Kwp = 10;
    private static readonly TimeSpan Five = TimeSpan.FromMinutes(5);

    private static TimeSeries ClearSkyPv(DateTime from, int days, double ratio, Func<DateTime, double, double>? shape = null)
    {
        var pts = new List<Point>();
        for (var t = from; t < from.AddDays(days); t += Five)
        {
            var p = ClearSky.Ghi(t, Lat, Lon) * Kwp * ratio;
            pts.Add(new Point(t, shape?.Invoke(t, p) ?? p));
        }
        return new TimeSeries(pts);
    }

    [Fact]
    public void Trains_month_hour_median_ratio_and_predicts_it()
    {
        var pv = ClearSkyPv(TD.Local(2026, 3, 1), 14, 0.8);
        var m = ExpectedPvModel.Train(pv, Kwp, Lat, Lon, timeZone: TD.Ict);
        m.Ratios[2][12]!.Value.Should().BeApproximately(0.8, 1e-6);
        m.Ratios[2][3].Should().BeNull();   // night
        var t = TD.Local(2026, 3, 20, 11);
        m.PredictW(t).Should().BeApproximately(0.8 * ClearSky.Ghi(t, Lat, Lon) * Kwp, 1e-6);
        m.PredictW(TD.Local(2026, 3, 20, 23)).Should().Be(0);
        m.SampleCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Curtailed_intervals_are_excluded_from_training()
    {
        var from = TD.Local(2026, 3, 1);
        // Zero-export clips output to 2 kW at 11:00–13:00 every day.
        bool Clipped(DateTime t) { var h = t.AddHours(7).Hour; return h is >= 11 and < 13; }
        var pv = ClearSkyPv(from, 14, 0.8, (t, p) => Clipped(t) ? Math.Min(p, 2000) : p);
        var mask = Enumerable.Range(0, 14).Select(d => new TimeRange(TD.Local(2026, 3, 1 + d, 11), TD.Local(2026, 3, 1 + d, 13))).ToList();

        var naive = ExpectedPvModel.Train(pv, Kwp, Lat, Lon, timeZone: TD.Ict);
        naive.Ratios[2][12]!.Value.Should().BeLessThan(0.3);

        var masked = ExpectedPvModel.Train(pv, Kwp, Lat, Lon, curtailed: mask, timeZone: TD.Ict);
        masked.CurtailedSamplesExcluded.Should().Be(14 * 24);
        masked.Ratios[2][12].Should().BeNull();
        // The hour falls back to the nearest month with data — none here — so the fallback ratio applies.
        masked.RatioFor(3, 12).Should().Be(ExpectedPvModel.FallbackRatio);
        masked.Ratios[2][10]!.Value.Should().BeApproximately(0.8, 1e-6);
    }

    [Fact]
    public void Lux_sensor_fit_replaces_clear_sky_ratio()
    {
        var from = TD.Local(2026, 3, 1);
        var rnd = new Random(42);
        var lux = new List<Point>();
        var pv = new List<Point>();
        for (var t = from; t < from.AddDays(10); t += Five)
        {
            var ghi = ClearSky.Ghi(t, Lat, Lon);
            if (ghi <= 0) continue;
            var cloud = 0.3 + 0.7 * rnd.NextDouble();
            var l = ghi * cloud * 110;
            lux.Add(new Point(t, l));
            pv.Add(new Point(t, 0.07 * l + 50));
        }
        var m = ExpectedPvModel.Train(new TimeSeries(pv), Kwp, Lat, Lon, new TimeSeries(lux), timeZone: TD.Ict);
        m.SensorKind.Should().Be("lux");
        var fit = m.SensorFits[12]!;
        fit.A.Should().BeApproximately(0.07, 1e-6);
        fit.B.Should().BeApproximately(50, 1e-3);
        m.PredictW(TD.Local(2026, 3, 12, 12), 50_000).Should().BeApproximately(0.07 * 50_000 + 50, 1e-3);
        // No sensor value → clear-sky ratio path.
        m.PredictW(TD.Local(2026, 3, 12, 12)).Should().BeGreaterThan(0);
    }

    [Fact]
    public void Json_round_trip_predicts_the_same()
    {
        var m = ExpectedPvModel.Train(ClearSkyPv(TD.Local(2026, 3, 1), 7, 0.78), Kwp, Lat, Lon, timeZone: TD.Ict);
        var json = m.ToJson();
        var back = ExpectedPvModel.FromJson(json);
        back.CapacityKwp.Should().Be(Kwp);
        back.UtcOffsetMinutes.Should().Be(420);
        foreach (var h in new[] { 8, 10, 12, 15 })
        {
            var t = TD.Local(2026, 3, 9, h);
            back.PredictW(t).Should().BeApproximately(m.PredictW(t), 1e-9);
        }
        back.ToJson().Should().Be(json);
    }

    [Fact]
    public void Missing_month_uses_nearest_month_then_fallback()
    {
        var m = ExpectedPvModel.Train(ClearSkyPv(TD.Local(2026, 3, 1), 7, 0.7), Kwp, Lat, Lon, timeZone: TD.Ict);
        m.RatioFor(5, 12).Should().BeApproximately(0.7, 1e-6);   // May → March
        var empty = ExpectedPvModel.Train(TimeSeries.Empty, Kwp, Lat, Lon, timeZone: TD.Ict);
        empty.RatioFor(5, 12).Should().Be(ExpectedPvModel.FallbackRatio);
        empty.PredictW(TD.Local(2026, 5, 1, 12)).Should().BeGreaterThan(0);
    }

    [Fact]
    public void Performance_daily_expected_vs_actual_and_pr()
    {
        var from = TD.Local(2026, 3, 1);
        var model = ExpectedPvModel.Train(ClearSkyPv(from, 7, 0.8), Kwp, Lat, Lon, timeZone: TD.Ict);
        var actual = ClearSkyPv(TD.Local(2026, 3, 10), 2, 0.72);   // 90 % of expected
        var days = PvPerformance.Daily(actual, model, TD.Local(2026, 3, 10), TD.Local(2026, 3, 12));
        days.Should().HaveCount(2);
        var d = days[0];
        d.Date.Should().Be(new DateOnly(2026, 3, 10));
        d.PerformanceIndexPct!.Value.Should().BeApproximately(90, 0.5);
        d.KwhPerKwp.Should().BeApproximately(d.ActualKwh / Kwp, 1e-9);
        d.PrBasis.Should().Be("clearsky");
        d.PrPct!.Value.Should().BeApproximately(72, 1.0);
        var sum = PvPerformance.Summarize(days, Kwp);
        sum.Days.Should().Be(2);
        sum.PerformanceIndexPct!.Value.Should().BeApproximately(90, 0.5);
    }

    [Fact]
    public void Performance_uses_measured_irradiance_when_given()
    {
        var day = TD.Local(2026, 3, 10);
        var irr = TD.Constant(day.AddHours(6), TimeSpan.FromHours(10), Five, 500);   // 5 kWh/m²
        var actual = TD.Constant(day.AddHours(6), TimeSpan.FromHours(10), Five, 4000);   // 40 kWh
        var d = PvPerformance.Daily(actual, actual, Kwp, Lat, Lon, day, day.AddDays(1), TD.Ict, irr).Single();
        d.PrBasis.Should().Be("irradiance");
        d.InsolationKwhM2.Should().BeApproximately(5, 1e-6);
        d.PrPct!.Value.Should().BeApproximately(80, 1e-6);
        PvPerformance.Daily(TimeSeries.Empty, TimeSeries.Empty, Kwp, Lat, Lon, day, day, TD.Ict).Should().BeEmpty();
    }
}
