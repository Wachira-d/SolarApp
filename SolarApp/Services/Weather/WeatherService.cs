using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using SolarApp.Services.InverterApi.Common;

namespace SolarApp.Services.Weather;

/// <summary>
/// Weather and solar irradiance forecast via Open-Meteo (free, no API key).
/// Provides Shortwave Radiation (W/m²) — used to improve performance expectations.
/// </summary>
public interface IWeatherService
{
    Task<SolarForecast?> GetForecastAsync(double lat, double lng, DateTime from, DateTime to, CancellationToken ct = default);
    Task<decimal?> EstimatePeakSunHoursAsync(double lat, double lng, DateTime date, CancellationToken ct = default);
}

public record DailyIrradiance(DateTime Date, decimal ShortwaveRadiationSumMj, decimal PeakSunHours, decimal MaxTempC, decimal MinTempC);

public record SolarForecast(double Lat, double Lng, List<DailyIrradiance> Days, string Source);

public class WeatherService : IWeatherService
{
    private const string BaseUrl = "https://api.open-meteo.com/v1/forecast";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<WeatherService> _logger;

    public WeatherService(IHttpClientFactory httpClientFactory, IMemoryCache cache, ILogger<WeatherService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task<SolarForecast?> GetForecastAsync(double lat, double lng, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var cacheKey = $"weather:{lat:0.####}:{lng:0.####}:{from:yyyyMMdd}:{to:yyyyMMdd}";
        if (_cache.TryGetValue(cacheKey, out SolarForecast? cached)) return cached;

        var url = $"{BaseUrl}?latitude={lat}&longitude={lng}" +
                  $"&daily=shortwave_radiation_sum,temperature_2m_max,temperature_2m_min" +
                  $"&timezone=auto" +
                  $"&start_date={from:yyyy-MM-dd}&end_date={to:yyyy-MM-dd}";

        var client = _httpClientFactory.CreateClient("default");
        var (response, err, kind) = await AdapterResilience.SafeSendAsync(
            () => client.GetAsync(url, ct), "Open-Meteo", timeoutSeconds: 10, cancellationToken: ct);

        if (response == null || !response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Weather fetch failed: {Kind} — {Err}", kind, err);
            return null;
        }

        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("daily", out var daily)) return null;

            var dates = daily.GetProperty("time").EnumerateArray()
                .Select(e => DateTime.Parse(e.GetString()!)).ToList();
            var radiation = daily.GetProperty("shortwave_radiation_sum").EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.Number ? (decimal)e.GetDouble() : 0m).ToList();
            var tmax = daily.GetProperty("temperature_2m_max").EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.Number ? (decimal)e.GetDouble() : 0m).ToList();
            var tmin = daily.GetProperty("temperature_2m_min").EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.Number ? (decimal)e.GetDouble() : 0m).ToList();

            var days = new List<DailyIrradiance>();
            for (int i = 0; i < dates.Count; i++)
            {
                // Open-Meteo returns MJ/m² — convert to peak-sun-hours (1 PSH = 1 kWh/m² = 3.6 MJ/m²)
                var mj = i < radiation.Count ? radiation[i] : 0;
                var psh = mj / 3.6m;
                days.Add(new DailyIrradiance(
                    dates[i],
                    Math.Round(mj, 2),
                    Math.Round(psh, 3),
                    i < tmax.Count ? tmax[i] : 0,
                    i < tmin.Count ? tmin[i] : 0));
            }

            var forecast = new SolarForecast(lat, lng, days, "open-meteo");
            _cache.Set(cacheKey, forecast, TimeSpan.FromHours(3));
            return forecast;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Weather JSON parse failed");
            return null;
        }
    }

    public async Task<decimal?> EstimatePeakSunHoursAsync(double lat, double lng, DateTime date, CancellationToken ct = default)
    {
        var f = await GetForecastAsync(lat, lng, date.Date, date.Date, ct);
        return f?.Days.FirstOrDefault()?.PeakSunHours;
    }
}
