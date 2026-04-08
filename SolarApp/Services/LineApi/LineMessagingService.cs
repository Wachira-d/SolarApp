using System.Net.Http.Json;
using System.Text;

namespace SolarApp.Services.LineApi;

public class LineMessagingService : ILineMessagingService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LineMessagingService> _logger;
    private const string PushUrl = "https://api.line.me/v2/bot/message/push";

    public LineMessagingService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<LineMessagingService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendMessageAsync(string lineUserId, string message)
    {
        if (string.IsNullOrEmpty(lineUserId)) return;

        var token = _configuration["LineMessaging:ChannelAccessToken"];
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning("LINE Channel Access Token is not configured");
            return;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

            var payload = new
            {
                to = lineUserId,
                messages = new[]
                {
                    new { type = "text", text = message }
                }
            };

            var response = await client.PostAsJsonAsync(PushUrl, payload);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                _logger.LogError("LINE API error: {StatusCode} - {Error}", response.StatusCode, error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send LINE message to {UserId}", lineUserId);
        }
    }

    public async Task SendAlertAsync(string lineUserId, string siteName, string alertTitle,
        string alertMessage, string? rootCause)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- Solar Alert ---");
        sb.AppendLine($"Site: {siteName}");
        sb.AppendLine($"Alert: {alertTitle}");
        sb.AppendLine($"Detail: {alertMessage}");

        if (!string.IsNullOrEmpty(rootCause))
        {
            sb.AppendLine();
            sb.AppendLine($"Possible Cause: {rootCause}");
        }

        sb.AppendLine();
        sb.AppendLine($"Time: {DateTime.Now:dd/MM/yyyy HH:mm}");

        await SendMessageAsync(lineUserId, sb.ToString());
    }

    public async Task SendMaintenanceReminderAsync(string lineUserId, string siteName,
        string maintenanceType, DateTime dueDate)
    {
        var message = $"--- Maintenance Reminder ---\n" +
                      $"Site: {siteName}\n" +
                      $"Type: {maintenanceType}\n" +
                      $"Due Date: {dueDate:dd/MM/yyyy}\n" +
                      $"\nPlease schedule this maintenance soon.";

        await SendMessageAsync(lineUserId, message);
    }
}
