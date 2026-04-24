using System.Text.Json;

namespace SolarApp.Services.Geocoding;

public interface IGeocodingService
{
    /// <summary>Reverse-geocode latitude/longitude into a human-readable address.</summary>
    Task<string?> ReverseAsync(double lat, double lng, string? preferredLang = null, CancellationToken ct = default);

    /// <summary>Forward-geocode (search): address string → (lat, lng, display_name).</summary>
    Task<(double Lat, double Lng, string DisplayName)?> SearchAsync(string query, CancellationToken ct = default);
}

/// <summary>
/// Nominatim (OpenStreetMap) geocoding — free, no API key required.
/// Rate limit: 1 request/second. We add a small in-memory cache to stay
/// well under that for repeated lookups of the same coordinates.
/// </summary>
public class NominatimGeocodingService : IGeocodingService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<NominatimGeocodingService> _logger;
    private const string BaseUrl = "https://nominatim.openstreetmap.org";
    private const string UserAgent = "SolarApp/1.0 (solarapp@mangkorn.co.th)";

    // Simple in-memory cache — one entry per rounded coordinate for a day
    private static readonly Dictionary<string, (string Address, DateTime ExpiresAt)> _cache = new();
    private static readonly object _cacheLock = new();
    private static DateTime _lastRequest = DateTime.MinValue;

    public NominatimGeocodingService(IHttpClientFactory httpClientFactory, ILogger<NominatimGeocodingService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string?> ReverseAsync(double lat, double lng, string? preferredLang = null, CancellationToken ct = default)
    {
        var cacheKey = $"rev|{lat:0.#####}|{lng:0.#####}|{preferredLang}";
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.ExpiresAt)
                return cached.Address;
        }

        await ThrottleAsync(ct);

        var lang = string.IsNullOrWhiteSpace(preferredLang) ? "th,en" : preferredLang;
        var url = $"{BaseUrl}/reverse?format=json&lat={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                  $"&lon={lng.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                  $"&addressdetails=1&accept-language={lang}&zoom=18";

        var client = _httpClientFactory.CreateClient("default");
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var response = await client.GetAsync(url, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Nominatim reverse geocoding failed: HTTP {Code}", (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("display_name", out var displayName)
                || displayName.ValueKind != JsonValueKind.String)
                return null;

            var address = displayName.GetString() ?? "";
            lock (_cacheLock)
            {
                _cache[cacheKey] = (address, DateTime.UtcNow.AddDays(1));
            }
            return address;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nominatim reverse geocoding error");
            return null;
        }
    }

    public async Task<(double Lat, double Lng, string DisplayName)?> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var cacheKey = "search|" + query.Trim().ToLowerInvariant();
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.ExpiresAt)
            {
                // cache stores only address for search results — we'd need a different shape.
                // Skip cache for search for simplicity.
            }
        }

        await ThrottleAsync(ct);

        var url = $"{BaseUrl}/search?format=json&addressdetails=1&limit=1&accept-language=th,en&q=" +
                  Uri.EscapeDataString(query);

        var client = _httpClientFactory.CreateClient("default");
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var response = await client.GetAsync(url, cts.Token);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(body);
            var arr = doc.RootElement;
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) return null;
            var first = arr[0];

            if (!first.TryGetProperty("lat", out var latEl) || !first.TryGetProperty("lon", out var lonEl))
                return null;
            if (!double.TryParse(latEl.GetString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var lat)) return null;
            if (!double.TryParse(lonEl.GetString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var lon)) return null;

            var dn = first.TryGetProperty("display_name", out var dnEl) ? (dnEl.GetString() ?? "") : "";
            return (lat, lon, dn);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nominatim search error");
            return null;
        }
    }

    /// <summary>Ensures at least 1 second between Nominatim requests (ToS).</summary>
    private static async Task ThrottleAsync(CancellationToken ct)
    {
        TimeSpan wait;
        lock (_cacheLock)
        {
            var since = DateTime.UtcNow - _lastRequest;
            wait = since < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) - since : TimeSpan.Zero;
            _lastRequest = DateTime.UtcNow + wait;
        }
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
    }
}
