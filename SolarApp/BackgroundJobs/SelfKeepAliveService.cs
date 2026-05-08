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

        // Resolve URL once. Override via SOLARAPP_KEEPALIVE_URL or KeepAlive:Url
        // for environments behind a reverse proxy where http://localhost:N
        // doesn't resolve from the worker process.
        var explicitUrl = Environment.GetEnvironmentVariable("SOLARAPP_KEEPALIVE_URL")
                       ?? _configuration["KeepAlive:Url"];
        var configuredUrls = _configuration["urls"]
                          ?? _configuration["ASPNETCORE_URLS"]
                          ?? "http://localhost:5000";
        var baseUrl = (explicitUrl ?? configuredUrls)
            .Split(';').FirstOrDefault(u => u.StartsWith("http"))
            ?? "http://localhost:5000";

        _logger.LogInformation("Self-keepalive: pinging {Url}/ping every {Interval}", baseUrl, interval);

        var consecutiveFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);

                var response = await client.GetAsync($"{baseUrl}/ping", stoppingToken);

                if (response.IsSuccessStatusCode)
                {
                    if (consecutiveFailures > 0)
                        _logger.LogInformation("Self-keepalive recovered after {Count} failures", consecutiveFailures);
                    consecutiveFailures = 0;
                    _logger.LogDebug("Self-keepalive ping OK ({Url}/ping)", baseUrl);
                }
                else
                {
                    consecutiveFailures++;
                    var code = (int)response.StatusCode;
                    if (code == 401 || code == 403)
                    {
                        _logger.LogWarning(
                            "Self-keepalive: /ping returned HTTP {Code}. /ping should be anonymous — " +
                            "check that authorization isn't applied to it. Consecutive failures: {Count}",
                            code, consecutiveFailures);
                    }
                    else if (consecutiveFailures == 1 || consecutiveFailures % 5 == 0)
                    {
                        _logger.LogWarning(
                            "Self-keepalive ping failed: HTTP {Code} ({Url}/ping). Consecutive failures: {Count}. " +
                            "If this persists, check IIS bindings or set SOLARAPP_KEEPALIVE_URL.",
                            code, baseUrl, consecutiveFailures);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                consecutiveFailures++;
                if (consecutiveFailures == 1 || consecutiveFailures % 5 == 0)
                {
                    _logger.LogWarning(ex,
                        "Self-keepalive ping error ({Url}/ping). Consecutive failures: {Count}",
                        baseUrl, consecutiveFailures);
                }
            }

            await Task.Delay(interval, stoppingToken);
        }
    }
}
