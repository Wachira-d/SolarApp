namespace SolarApp.Services.Analytics;

public readonly record struct SolarPosition(double ZenithDeg, double ElevationDeg, double AzimuthDeg, double DeclinationDeg, double EquationOfTimeMin);

/// <summary>
/// Sun position (NOAA solar calculator equations) and Haurwitz clear-sky
/// global horizontal irradiance. Good to a fraction of a degree, which is
/// far better than needed for ratios of measured to expected power.
/// </summary>
public static class ClearSky
{
    private const double Deg = Math.PI / 180.0;

    public static SolarPosition Position(DateTime utc, double latitude, double longitude)
    {
        utc = TimeSeries.AsUtc(utc);
        var jd = utc.Ticks / (double)TimeSpan.TicksPerDay + 1721425.5;   // Julian day: 0001-01-01 is JD 1721425.5
        var t = (jd - 2451545.0) / 36525.0;

        var l0 = Mod(280.46646 + t * (36000.76983 + t * 0.0003032), 360);
        var m = 357.52911 + t * (35999.05029 - 0.0001537 * t);
        var e = 0.016708634 - t * (0.000042037 + 0.0000001267 * t);
        var c = Math.Sin(m * Deg) * (1.914602 - t * (0.004817 + 0.000014 * t))
              + Math.Sin(2 * m * Deg) * (0.019993 - 0.000101 * t)
              + Math.Sin(3 * m * Deg) * 0.000289;
        var trueLong = l0 + c;
        var omega = 125.04 - 1934.136 * t;
        var lambda = trueLong - 0.00569 - 0.00478 * Math.Sin(omega * Deg);
        var eps0 = 23 + (26 + (21.448 - t * (46.815 + t * (0.00059 - t * 0.001813))) / 60) / 60;
        var eps = eps0 + 0.00256 * Math.Cos(omega * Deg);
        var decl = Math.Asin(Math.Sin(eps * Deg) * Math.Sin(lambda * Deg)) / Deg;

        var y = Math.Pow(Math.Tan(eps * Deg / 2), 2);
        var eot = 4 / Deg * (y * Math.Sin(2 * l0 * Deg) - 2 * e * Math.Sin(m * Deg)
                  + 4 * e * y * Math.Sin(m * Deg) * Math.Cos(2 * l0 * Deg)
                  - 0.5 * y * y * Math.Sin(4 * l0 * Deg) - 1.25 * e * e * Math.Sin(2 * m * Deg));

        var minutes = utc.TimeOfDay.TotalMinutes;
        var tst = Mod(minutes + eot + 4 * longitude, 1440);
        var ha = tst / 4 < 0 ? tst / 4 + 180 : tst / 4 - 180;

        var cosZ = Math.Sin(latitude * Deg) * Math.Sin(decl * Deg)
                 + Math.Cos(latitude * Deg) * Math.Cos(decl * Deg) * Math.Cos(ha * Deg);
        cosZ = Math.Clamp(cosZ, -1, 1);
        var zen = Math.Acos(cosZ) / Deg;

        var denom = Math.Cos(latitude * Deg) * Math.Sin(zen * Deg);
        double az;
        if (Math.Abs(denom) < 1e-9) az = 180;
        else
        {
            var a = Math.Acos(Math.Clamp((Math.Sin(latitude * Deg) * Math.Cos(zen * Deg) - Math.Sin(decl * Deg)) / denom, -1, 1)) / Deg;
            az = ha > 0 ? Mod(a + 180, 360) : Mod(540 - a, 360);
        }
        return new SolarPosition(zen, 90 - zen, az, decl, eot);
    }

    /// <summary>Haurwitz clear-sky GHI, W/m²: 1098·cos z·exp(−0.059 / cos z); 0 when the sun is down.</summary>
    public static double Ghi(DateTime utc, double latitude, double longitude)
    {
        var z = Position(utc, latitude, longitude).ZenithDeg;
        return HaurwitzGhi(z);
    }

    public static double HaurwitzGhi(double zenithDeg)
    {
        var cz = Math.Cos(zenithDeg * Deg);
        if (cz <= 0.01) return 0;   // below ~89.4°: the formula blows up, and there is no PV anyway
        return 1098.0 * cz * Math.Exp(-0.059 / cz);
    }

    /// <summary>Clear-sky insolation over [from, to), kWh/m², sampled every <paramref name="step"/> (default 5 min).</summary>
    public static double InsolationKwhM2(DateTime fromUtc, DateTime toUtc, double latitude, double longitude, TimeSpan? step = null)
    {
        var s = step ?? TimeSpan.FromMinutes(5);
        double wh = 0;
        for (var t = fromUtc; t < toUtc; t += s)
        {
            var e = t + s > toUtc ? toUtc : t + s;
            wh += (Ghi(t, latitude, longitude) + Ghi(e, latitude, longitude)) / 2 * (e - t).TotalHours;
        }
        return wh / 1000;
    }

    private static double Mod(double x, double m) => ((x % m) + m) % m;
}
