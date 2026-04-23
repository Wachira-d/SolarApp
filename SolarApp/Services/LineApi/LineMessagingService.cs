using System.Net.Http.Json;
using System.Text;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Services.LineApi;

public class LineMessagingService : ILineMessagingService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LineMessagingService> _logger;
    private readonly ApplicationDbContext _context;
    private const string PushUrl = "https://api.line.me/v2/bot/message/push";

    public LineMessagingService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<LineMessagingService> logger,
        ApplicationDbContext context)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
        _context = context;
    }

    public async Task<(bool Success, string Message)> SendTestMessageAsync(string lineUserId, string? overrideToken = null)
    {
        if (string.IsNullOrEmpty(lineUserId))
            return (false, "LINE User ID is required");

        var token = overrideToken ?? await GetChannelAccessTokenAsync();
        if (string.IsNullOrEmpty(token))
            return (false, "Channel Access Token is not configured");

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

            var testMsg = $"[SolarApp] Test notification\nTime: {DateTime.Now:dd/MM/yyyy HH:mm:ss}\nIf you see this, LINE integration is working!";

            var payload = new
            {
                to = lineUserId,
                messages = new[] { new { type = "text", text = testMsg } }
            };

            var response = await client.PostAsJsonAsync(PushUrl, payload);
            var body = await response.Content.ReadAsStringAsync();

            // Log it
            _context.NotificationLogs.Add(new NotificationLog
            {
                RecipientLineId = lineUserId,
                Channel = NotificationChannel.Line,
                Title = "Test message",
                Message = testMsg,
                RelatedEntityType = "Test",
                Status = response.IsSuccessStatusCode ? NotificationStatus.Sent : NotificationStatus.Failed,
                ErrorMessage = !response.IsSuccessStatusCode ? body : null,
                SentAt = response.IsSuccessStatusCode ? DateTime.UtcNow : null
            });
            await _context.SaveChangesAsync();

            if (response.IsSuccessStatusCode)
                return (true, $"Test message sent successfully to {lineUserId}");

            return (false, $"LINE API error {(int)response.StatusCode}: {body}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send test LINE message");
            return (false, $"Exception: {ex.Message}");
        }
    }

    private async Task<string?> GetChannelAccessTokenAsync()
    {
        // Prefer SystemSettings (configurable via UI); fall back to appsettings
        var dbSetting = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(_context.SystemSettings, s => s.Key == "Line.ChannelAccessToken");
        if (dbSetting != null && !string.IsNullOrWhiteSpace(dbSetting.Value))
            return dbSetting.Value;

        return _configuration["LineMessaging:ChannelAccessToken"];
    }

    public async Task SendMessageAsync(string lineUserId, string message)
    {
        if (string.IsNullOrEmpty(lineUserId)) return;

        var token = await GetChannelAccessTokenAsync();
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning("LINE Channel Access Token is not configured (check Admin > LINE Settings)");
            return;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

            var payload = new
            {
                to = lineUserId,
                messages = new[] { new { type = "text", text = message } }
            };

            var response = await client.PostAsJsonAsync(PushUrl, payload);
            var status = response.IsSuccessStatusCode
                ? NotificationStatus.Sent : NotificationStatus.Failed;
            var errorMsg = !response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync() : null;

            // Log notification
            _context.NotificationLogs.Add(new NotificationLog
            {
                RecipientLineId = lineUserId,
                Channel = NotificationChannel.Line,
                Title = "Text Message",
                Message = message,
                Status = status,
                ErrorMessage = errorMsg,
                SentAt = status == NotificationStatus.Sent ? DateTime.UtcNow : null
            });
            await _context.SaveChangesAsync();

            if (!response.IsSuccessStatusCode)
                _logger.LogError("LINE API error: {StatusCode} - {Error}", response.StatusCode, errorMsg);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send LINE message to {UserId}", lineUserId);
        }
    }

    public async Task SendFlexAlertAsync(string lineUserId, string siteName, string alertTitle,
        string alertMessage, string severity, string? rootCause)
    {
        if (string.IsNullOrEmpty(lineUserId)) return;

        var token = await GetChannelAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return;

        var severityColor = severity switch
        {
            "Critical" => "#FF0000",
            "High" => "#FF8C00",
            "Medium" => "#FFD700",
            _ => "#808080"
        };

        // LINE Flex Message - rich card notification
        var flexContent = new
        {
            type = "bubble",
            header = new
            {
                type = "box",
                layout = "vertical",
                backgroundColor = severityColor,
                contents = new object[]
                {
                    new { type = "text", text = "Solar Alert", color = "#FFFFFF", weight = "bold", size = "lg" },
                    new { type = "text", text = severity, color = "#FFFFFF", size = "sm" }
                }
            },
            body = new
            {
                type = "box",
                layout = "vertical",
                spacing = "md",
                contents = BuildFlexBody(siteName, alertTitle, alertMessage, rootCause)
            },
            footer = new
            {
                type = "box",
                layout = "vertical",
                contents = new object[]
                {
                    new
                    {
                        type = "text",
                        text = DateTime.Now.ToString("dd/MM/yyyy HH:mm"),
                        size = "xs",
                        color = "#AAAAAA",
                        align = "center"
                    }
                }
            }
        };

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

            var payload = new
            {
                to = lineUserId,
                messages = new object[]
                {
                    new
                    {
                        type = "flex",
                        altText = $"[{severity}] {alertTitle}",
                        contents = flexContent
                    }
                }
            };

            var response = await client.PostAsJsonAsync(PushUrl, payload);
            var status = response.IsSuccessStatusCode
                ? NotificationStatus.Sent : NotificationStatus.Failed;

            _context.NotificationLogs.Add(new NotificationLog
            {
                RecipientLineId = lineUserId,
                Channel = NotificationChannel.Line,
                Title = alertTitle,
                Message = alertMessage,
                RelatedEntityType = "SystemAlert",
                Status = status,
                ErrorMessage = !response.IsSuccessStatusCode
                    ? await response.Content.ReadAsStringAsync() : null,
                SentAt = status == NotificationStatus.Sent ? DateTime.UtcNow : null
            });
            await _context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send LINE flex message to {UserId}", lineUserId);
        }
    }

    public async Task SendAlertAsync(string lineUserId, string siteName, string alertTitle,
        string alertMessage, string? rootCause)
    {
        await SendFlexAlertAsync(lineUserId, siteName, alertTitle, alertMessage, "High", rootCause);
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

    public async Task SendWarrantyExpiryAsync(string lineUserId, string siteName, int daysRemaining)
    {
        var message = $"--- Warranty Expiry Warning ---\n" +
                      $"Site: {siteName}\n" +
                      $"Warranty expires in {daysRemaining} days\n" +
                      $"\nPlease review warranty terms.";

        await SendMessageAsync(lineUserId, message);
    }

    private static object[] BuildFlexBody(string siteName, string title, string message, string? rootCause)
    {
        var contents = new List<object>
        {
            new { type = "text", text = title, weight = "bold", size = "md", wrap = true },
            new
            {
                type = "box", layout = "vertical", spacing = "sm",
                contents = new object[]
                {
                    new
                    {
                        type = "box", layout = "horizontal",
                        contents = new object[]
                        {
                            new { type = "text", text = "Site", size = "sm", color = "#AAAAAA", flex = 2 },
                            new { type = "text", text = siteName, size = "sm", flex = 4, wrap = true }
                        }
                    },
                    new
                    {
                        type = "box", layout = "horizontal",
                        contents = new object[]
                        {
                            new { type = "text", text = "Detail", size = "sm", color = "#AAAAAA", flex = 2 },
                            new { type = "text", text = message, size = "sm", flex = 4, wrap = true }
                        }
                    }
                }
            }
        };

        if (!string.IsNullOrEmpty(rootCause))
        {
            contents.Add(new { type = "separator" });
            contents.Add(new
            {
                type = "box", layout = "vertical",
                contents = new object[]
                {
                    new { type = "text", text = "Root Cause Analysis", size = "sm", color = "#FF8C00", weight = "bold" },
                    new { type = "text", text = rootCause, size = "xs", color = "#666666", wrap = true }
                }
            });
        }

        return contents.ToArray();
    }
}
