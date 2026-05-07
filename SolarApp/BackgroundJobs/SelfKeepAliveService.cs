namespace SolarApp.BackgroundJobs;

/// <summary>
/// Sends a self-request to /ping every 4 minutes to prevent IIS from
/// shutting down the app pool due to idle timeout (default 20 min).
///
/// Without this, when no user visits the website for 20 minutes, IIS kills
/// the w3wp.exe process → Hangfire dies → no scheduled sync → no data →
/// no LINE alerts. The operator only notices when they open the site and
/// see days of missing data.
///
/// This service runs as a .NET BackgroundService (IHostedService) which
/// starts with the app and stays alive as long as the process exists.
/// It's a safety net — the recommended primary fix is IIS configuration:
///   Application Pool → Start Mode = AlwaysRunning
///   Application Pool → Idle Time-out = 0
///   Site → Preload Enabled = True
/// </summary>
public class SelfKeepAliveService : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SelfKeepAliveService> _logger;

    public SelfKeepAliveService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<SelfKeepAliveService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait 30 seconds for the app to fully start before first ping
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        var interval = TimeSpan.FromMinutes(4); // Well under IIS 20-min default

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Determine our own URL from server configuration
                var urls = _configuration["urls"]
                        ?? _configuration["ASPNETCORE_URLS"]
                        ?? "http://localhost:5000";
                var baseUrl = urls.Split(';').FirstOrDefault(u => u.StartsWith("http"))
                           ?? "http://localhost:5000";

                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);

                var response = await client.GetAsync($"{baseUrl}/ping", stoppingToken);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Self-keepalive ping OK ({Url}/ping)", baseUrl);
                }
                else
                {
                    _logger.LogWarning("Self-keepalive ping failed: HTTP {Code}", (int)response.StatusCode);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Don't log every single failure — network issues during startup are normal
                _logger.LogDebug(ex, "Self-keepalive ping error (will retry in {Interval})", interval);
            }

            await Task.Delay(interval, stoppingToken);
        }
    }
}
