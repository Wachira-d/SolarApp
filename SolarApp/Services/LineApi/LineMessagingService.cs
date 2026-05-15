using System.Net.Http.Json;
using System.Text;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Services.LineApi;

public class LineMessagingService : ILineMessagingService
{
    // Process-wide throttle for outbound LINE pushes.
    // LINE Messaging API limits: 1000 push/min and 500 push/sec per channel.
    // We aim well below: ~20 msg/sec via 50ms min spacing. The semaphore
    // serializes access so concurrent jobs (health monitor + WO reminder)
    // can't burst together and trip the 500/sec ceiling.
    private static readonly SemaphoreSlim _sendGate = new(1, 1);
    private static DateTime _lastSendUtc = DateTime.MinValue;
    private static readonly TimeSpan _minSpacing = TimeSpan.FromMilliseconds(50);

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

    /// <summary>
    /// Acquires the process-wide LINE rate gate. Call once immediately before
    /// each PostAsJsonAsync to PushUrl. Returns an IDisposable that releases
    /// the gate; callers should `await using` it.
    /// </summary>
    private static async Task<IDisposable> AcquireSendSlotAsync(CancellationToken ct = default)
    {
        await _sendGate.WaitAsync(ct);
        try
        {
            var elapsed = DateTime.UtcNow - _lastSendUtc;
            if (elapsed < _minSpacing)
                await Task.Delay(_minSpacing - elapsed, ct);
            _lastSendUtc = DateTime.UtcNow;
        }
        catch
        {
            _sendGate.Release();
            throw;
        }
        return new GateReleaser();
    }

    private sealed class GateReleaser : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _released, 1) == 0)
                _sendGate.Release();
        }
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

            using var slot = await AcquireSendSlotAsync();
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

        const int maxAttempts = 3;
        NotificationStatus finalStatus = NotificationStatus.Failed;
        string? lastError = null;
        int lastStatusCode = 0;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var client = _httpClientFactory.CreateClient("default");
                client.DefaultRequestHeaders.Remove("Authorization");
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

                var payload = new
                {
                    to = lineUserId,
                    messages = new[] { new { type = "text", text = message } }
                };

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var slot = await AcquireSendSlotAsync(cts.Token);
                var response = await client.PostAsJsonAsync(PushUrl, payload, cts.Token);
                lastStatusCode = (int)response.StatusCode;

                if (response.IsSuccessStatusCode)
                {
                    finalStatus = NotificationStatus.Sent;
                    lastError = null;
                    break;
                }

                lastError = await response.Content.ReadAsStringAsync();

                // 4xx (except 429) = don't retry — problem with request/auth/user
                if (lastStatusCode == 401 || lastStatusCode == 403)
                {
                    _logger.LogError("LINE auth failed ({Code}) for {UserId}: {Error}",
                        lastStatusCode, lineUserId, lastError);
                    break;
                }
                if (lastStatusCode >= 400 && lastStatusCode < 500 && lastStatusCode != 429)
                {
                    _logger.LogWarning("LINE {Code} for {UserId}: {Error} — not retrying",
                        lastStatusCode, lineUserId, lastError);
                    break;
                }

                if (attempt < maxAttempts)
                {
                    var delayMs = (int)(500 * Math.Pow(2, attempt - 1) * (0.85 + Random.Shared.NextDouble() * 0.3));
                    _logger.LogWarning("LINE attempt {Att}/{Max} failed ({Code}), retrying in {Ms}ms",
                        attempt, maxAttempts, lastStatusCode, delayMs);
                    await Task.Delay(delayMs);
                }
            }
            catch (TaskCanceledException)
            {
                lastError = "Timeout";
                if (attempt < maxAttempts)
                    await Task.Delay(500 * attempt);
            }
            catch (HttpRequestException hre)
            {
                lastError = hre.Message;
                if (attempt < maxAttempts)
                    await Task.Delay(500 * attempt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error sending LINE to {UserId}", lineUserId);
                lastError = ex.Message;
                break; // Unknown error — don't retry
            }
        }

        // Log final outcome
        _context.NotificationLogs.Add(new NotificationLog
        {
            RecipientLineId = lineUserId,
            Channel = NotificationChannel.Line,
            Title = "Text Message",
            Message = message,
            Status = finalStatus,
            ErrorMessage = finalStatus == NotificationStatus.Failed
                ? $"HTTP {lastStatusCode}: {lastError ?? "Unknown"}"
                : null,
            SentAt = finalStatus == NotificationStatus.Sent ? DateTime.UtcNow : null
        });
        await _context.SaveChangesAsync();

        if (finalStatus != NotificationStatus.Sent)
            _logger.LogError("LINE send FAILED to {UserId} after {Attempts} attempt(s): HTTP {Code} {Error}",
                lineUserId, maxAttempts, lastStatusCode, lastError);
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

            using var slot = await AcquireSendSlotAsync();
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

    public async Task SendRecoveryNotificationAsync(string lineUserId, string siteName,
        string alertTitle, string recoveryMessage, TimeSpan downtime)
    {
        if (string.IsNullOrEmpty(lineUserId)) return;
        var token = await GetChannelAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return;

        var downtimeStr = downtime.TotalHours >= 1
            ? $"{downtime.TotalHours:0.0} ชม."
            : $"{downtime.TotalMinutes:0} นาที";

        var flexContent = new
        {
            type = "bubble",
            header = new
            {
                type = "box", layout = "vertical", backgroundColor = "#00C853",
                contents = new object[]
                {
                    new { type = "text", text = "✓ System Recovered", color = "#FFFFFF", weight = "bold", size = "lg" },
                    new { type = "text", text = "กลับมาปกติแล้ว", color = "#FFFFFF", size = "sm" }
                }
            },
            body = new
            {
                type = "box", layout = "vertical", spacing = "md",
                contents = new object[]
                {
                    new { type = "text", text = alertTitle.Replace("Device Offline:", "กลับออนไลน์:").Replace("API Connection Lost:", "API กลับมา:"), weight = "bold", size = "md", wrap = true },
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
                                    new { type = "text", text = "Downtime", size = "sm", color = "#AAAAAA", flex = 2 },
                                    new { type = "text", text = downtimeStr, size = "sm", flex = 4, wrap = true }
                                }
                            },
                            new
                            {
                                type = "box", layout = "horizontal",
                                contents = new object[]
                                {
                                    new { type = "text", text = "Detail", size = "sm", color = "#AAAAAA", flex = 2 },
                                    new { type = "text", text = recoveryMessage, size = "sm", flex = 4, wrap = true }
                                }
                            }
                        }
                    }
                }
            },
            footer = new
            {
                type = "box", layout = "vertical",
                contents = new object[]
                {
                    new { type = "text", text = DateTime.Now.ToString("dd/MM/yyyy HH:mm"), size = "xs", color = "#AAAAAA", align = "center" }
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
                messages = new object[] { new { type = "flex", altText = $"[RECOVERED] {alertTitle}", contents = flexContent } }
            };

            using var slot = await AcquireSendSlotAsync();
            var response = await client.PostAsJsonAsync(PushUrl, payload);

            _context.NotificationLogs.Add(new NotificationLog
            {
                RecipientLineId = lineUserId, Channel = NotificationChannel.Line,
                Title = $"[RECOVERED] {alertTitle}", Message = recoveryMessage,
                RelatedEntityType = "SystemAlert",
                Status = response.IsSuccessStatusCode ? NotificationStatus.Sent : NotificationStatus.Failed,
                ErrorMessage = !response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : null,
                SentAt = response.IsSuccessStatusCode ? DateTime.UtcNow : null
            });
            await _context.SaveChangesAsync();
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to send recovery notification to {UserId}", lineUserId); }
    }

    public async Task SendEscalationAsync(string lineUserId, string siteName, string alertTitle,
        string alertMessage, string severity, TimeSpan openDuration, int escalationLevel)
    {
        if (string.IsNullOrEmpty(lineUserId)) return;
        var token = await GetChannelAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return;

        var durationStr = openDuration.TotalHours >= 24
            ? $"{openDuration.TotalDays:0.0} วัน"
            : $"{openDuration.TotalHours:0.0} ชม.";

        var flexContent = new
        {
            type = "bubble",
            header = new
            {
                type = "box", layout = "vertical", backgroundColor = "#B71C1C",
                contents = new object[]
                {
                    new { type = "text", text = $"⚠ ESCALATION (Level {escalationLevel})", color = "#FFFFFF", weight = "bold", size = "lg" },
                    new { type = "text", text = $"ยังไม่ได้แก้ไข {durationStr}", color = "#FFCDD2", size = "sm" }
                }
            },
            body = new
            {
                type = "box", layout = "vertical", spacing = "md",
                contents = new object[]
                {
                    new { type = "text", text = alertTitle, weight = "bold", size = "md", wrap = true },
                    new
                    {
                        type = "box", layout = "vertical", spacing = "sm",
                        contents = new object[]
                        {
                            new
                            {
                                type = "box", layout = "horizontal",
                                contents = new object[] {
                                    new { type = "text", text = "Site", size = "sm", color = "#AAAAAA", flex = 2 },
                                    new { type = "text", text = siteName, size = "sm", flex = 4, wrap = true }
                                }
                            },
                            new
                            {
                                type = "box", layout = "horizontal",
                                contents = new object[] {
                                    new { type = "text", text = "Severity", size = "sm", color = "#AAAAAA", flex = 2 },
                                    new { type = "text", text = severity, size = "sm", color = "#FF0000", flex = 4 }
                                }
                            },
                            new
                            {
                                type = "box", layout = "horizontal",
                                contents = new object[] {
                                    new { type = "text", text = "Open for", size = "sm", color = "#AAAAAA", flex = 2 },
                                    new { type = "text", text = durationStr, size = "sm", color = "#FF0000", flex = 4 }
                                }
                            },
                            new
                            {
                                type = "box", layout = "horizontal",
                                contents = new object[] {
                                    new { type = "text", text = "Detail", size = "sm", color = "#AAAAAA", flex = 2 },
                                    new { type = "text", text = alertMessage, size = "sm", flex = 4, wrap = true }
                                }
                            }
                        }
                    },
                    new { type = "separator" },
                    new { type = "text", text = "กรุณาดำเนินการแก้ไขโดยด่วน", size = "sm", color = "#B71C1C", weight = "bold" }
                }
            },
            footer = new
            {
                type = "box", layout = "vertical",
                contents = new object[]
                {
                    new { type = "text", text = DateTime.Now.ToString("dd/MM/yyyy HH:mm"), size = "xs", color = "#AAAAAA", align = "center" }
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
                messages = new object[] { new { type = "flex", altText = $"[ESCALATION L{escalationLevel}] {alertTitle} — open {durationStr}", contents = flexContent } }
            };

            using var slot = await AcquireSendSlotAsync();
            var response = await client.PostAsJsonAsync(PushUrl, payload);

            _context.NotificationLogs.Add(new NotificationLog
            {
                RecipientLineId = lineUserId, Channel = NotificationChannel.Line,
                Title = $"[ESCALATION L{escalationLevel}] {alertTitle}", Message = alertMessage,
                RelatedEntityType = "SystemAlert",
                Status = response.IsSuccessStatusCode ? NotificationStatus.Sent : NotificationStatus.Failed,
                ErrorMessage = !response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : null,
                SentAt = response.IsSuccessStatusCode ? DateTime.UtcNow : null
            });
            await _context.SaveChangesAsync();
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to send escalation to {UserId}", lineUserId); }
    }

    public async Task SendDailySummaryAsync(string lineUserId, DailySummaryData s)
    {
        if (string.IsNullOrEmpty(lineUserId)) return;
        var token = await GetChannelAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return;

        var statusColor = s.OfflineSites == 0 && s.ActiveAlerts == 0 ? "#00C853"
            : s.ActiveAlerts > 0 && s.OfflineSites > 0 ? "#FF0000" : "#FF8C00";
        var statusText = s.OfflineSites == 0 && s.ActiveAlerts == 0
            ? "✓ All Systems Normal" : $"⚠ {s.ActiveAlerts} Active Alert(s)";

        var bodyContents = new List<object>
        {
            new { type = "text", text = statusText, weight = "bold", size = "md", color = statusColor, wrap = true },
            new { type = "separator" },
            new
            {
                type = "box", layout = "vertical", spacing = "sm",
                contents = new object[]
                {
                    MakeSummaryRow("Online / Total", $"{s.OnlineSites}/{s.TotalSites} sites"),
                    MakeSummaryRow("Offline", $"{s.OfflineSites} site(s)", s.OfflineSites > 0 ? "#FF0000" : null),
                    MakeSummaryRow("Energy Today", $"{s.TotalEnergyTodayKwh:N1} kWh"),
                    MakeSummaryRow("New Alerts", $"{s.NewAlertsToday}"),
                    MakeSummaryRow("Resolved Today", $"{s.ResolvedTodayAlerts}"),
                    MakeSummaryRow("Active Alerts", $"{s.ActiveAlerts}", s.ActiveAlerts > 0 ? "#FF8C00" : null),
                    MakeSummaryRow("Work Orders", $"{s.PendingWorkOrders} pending, {s.CompletedWorkOrdersToday} done today"),
                }
            }
        };

        if (s.CriticalSites.Count > 0)
        {
            bodyContents.Add(new { type = "separator" });
            bodyContents.Add(new { type = "text", text = "⚠ Critical Sites:", size = "sm", color = "#FF0000", weight = "bold" });
            foreach (var site in s.CriticalSites.Take(5))
                bodyContents.Add(new { type = "text", text = $"  • {site}", size = "xs", color = "#FF0000", wrap = true });
        }

        if (s.RecoveredSites.Count > 0)
        {
            bodyContents.Add(new { type = "separator" });
            bodyContents.Add(new { type = "text", text = "✓ Recovered Today:", size = "sm", color = "#00C853", weight = "bold" });
            foreach (var site in s.RecoveredSites.Take(5))
                bodyContents.Add(new { type = "text", text = $"  • {site}", size = "xs", color = "#00C853", wrap = true });
        }

        var flexContent = new
        {
            type = "bubble",
            header = new
            {
                type = "box", layout = "vertical", backgroundColor = "#1565C0",
                contents = new object[]
                {
                    new { type = "text", text = "Daily Summary Report", color = "#FFFFFF", weight = "bold", size = "lg" },
                    new { type = "text", text = DateTime.Now.ToString("dd/MM/yyyy"), color = "#BBDEFB", size = "sm" }
                }
            },
            body = new { type = "box", layout = "vertical", spacing = "md", contents = bodyContents.ToArray() },
            footer = new
            {
                type = "box", layout = "vertical",
                contents = new object[]
                {
                    new { type = "text", text = $"Report generated {DateTime.Now:HH:mm}", size = "xs", color = "#AAAAAA", align = "center" }
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
                messages = new object[] { new { type = "flex", altText = $"[Daily Summary] {s.OnlineSites}/{s.TotalSites} online, {s.ActiveAlerts} alerts", contents = flexContent } }
            };

            using var slot = await AcquireSendSlotAsync();
            var response = await client.PostAsJsonAsync(PushUrl, payload);

            _context.NotificationLogs.Add(new NotificationLog
            {
                RecipientLineId = lineUserId, Channel = NotificationChannel.Line,
                Title = "Daily Summary Report", Message = $"Online: {s.OnlineSites}/{s.TotalSites}, Alerts: {s.ActiveAlerts}",
                RelatedEntityType = "DailySummary",
                Status = response.IsSuccessStatusCode ? NotificationStatus.Sent : NotificationStatus.Failed,
                ErrorMessage = !response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : null,
                SentAt = response.IsSuccessStatusCode ? DateTime.UtcNow : null
            });
            await _context.SaveChangesAsync();
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to send daily summary to {UserId}", lineUserId); }
    }

    private static object MakeSummaryRow(string label, string value, string? valueColor = null) => new
    {
        type = "box", layout = "horizontal",
        contents = new object[]
        {
            new { type = "text", text = label, size = "sm", color = "#AAAAAA", flex = 3 },
            new { type = "text", text = value, size = "sm", color = valueColor ?? "#333333", flex = 4, wrap = true }
        }
    };

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
