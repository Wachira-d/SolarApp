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

    /// <summary>
    /// The app's public base URL (e.g. https://solarapp.mangkorn.co.th),
    /// used to build tappable deep-links in LINE messages.  Configurable
    /// via Admin → LINE Settings (SystemSettings key "App.BaseUrl"), falls
    /// back to appsettings "App:BaseUrl".  Returns null (no button) when
    /// unset, or when the value isn't an absolute https URL — LINE rejects
    /// URI actions that aren't https.
    /// </summary>
    private async Task<string?> GetAppBaseUrlAsync()
    {
        var dbSetting = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(_context.SystemSettings, s => s.Key == "App.BaseUrl");
        var raw = (dbSetting != null && !string.IsNullOrWhiteSpace(dbSetting.Value))
            ? dbSetting.Value
            : _configuration["App:BaseUrl"];
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim().TrimEnd('/');
        return raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? raw : null;
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
            _logger.LogError(ex,
                "Failed to send LINE flex message UserId={UserId} Site={SiteName} Title={AlertTitle} Severity={Severity}",
                lineUserId, siteName, alertTitle, severity);
        }
    }

    public async Task SendAlertAsync(string lineUserId, string siteName, string alertTitle,
        string alertMessage, string? rootCause)
    {
        await SendFlexAlertAsync(lineUserId, siteName, alertTitle, alertMessage, "High", rootCause);
    }

    public async Task SendDiagnosticAlertAsync(string lineUserId, string siteName,
        string alertTitle, string alertMessage, string severity,
        string? rootCause, string? predictedCause,
        string[] checkSteps, string[] fixSteps, string? knownCaseRef,
        string? safetyWarning, string[]? contextNotes)
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

        var bodyContents = new List<object>
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
                            new { type = "text", text = "Detail", size = "sm", color = "#AAAAAA", flex = 2 },
                            new { type = "text", text = alertMessage.Length > 200 ? alertMessage[..200] + "..." : alertMessage, size = "sm", flex = 4, wrap = true }
                        }
                    }
                }
            }
        };

        // Safety warning — top priority, red background
        if (!string.IsNullOrEmpty(safetyWarning))
        {
            bodyContents.Add(new { type = "separator" });
            bodyContents.Add(new { type = "text", text = $"⚠ {safetyWarning}", size = "xs", color = "#FF0000", weight = "bold", wrap = true });
        }

        // Predicted cause
        if (!string.IsNullOrEmpty(predictedCause))
        {
            bodyContents.Add(new { type = "separator" });
            bodyContents.Add(new { type = "text", text = "🔍 คาดว่า", size = "sm", color = "#FF6F00", weight = "bold" });
            bodyContents.Add(new { type = "text", text = predictedCause, size = "xs", color = "#333333", wrap = true });
        }

        // Context notes (history, patterns)
        if (contextNotes != null && contextNotes.Length > 0)
        {
            foreach (var note in contextNotes.Take(3))
                bodyContents.Add(new { type = "text", text = $"📊 {note}", size = "xs", color = "#666666", wrap = true });
        }

        // Check steps
        if (checkSteps.Length > 0)
        {
            bodyContents.Add(new { type = "separator" });
            bodyContents.Add(new { type = "text", text = "ตรวจเช็ค", size = "sm", color = "#1565C0", weight = "bold" });
            foreach (var step in checkSteps.Take(4))
                bodyContents.Add(new { type = "text", text = $"☐ {step}", size = "xs", color = "#333333", wrap = true });
        }

        // Fix steps
        if (fixSteps.Length > 0)
        {
            bodyContents.Add(new { type = "separator" });
            bodyContents.Add(new { type = "text", text = "วิธีแก้ไข", size = "sm", color = "#00C853", weight = "bold" });
            foreach (var step in fixSteps.Take(4))
                bodyContents.Add(new { type = "text", text = $"→ {step}", size = "xs", color = "#333333", wrap = true });
        }

        // Known case reference
        if (!string.IsNullOrEmpty(knownCaseRef))
        {
            bodyContents.Add(new { type = "separator" });
            bodyContents.Add(new { type = "text", text = $"อ้างอิง: {(knownCaseRef.Length > 80 ? knownCaseRef[..80] + "..." : knownCaseRef)}", size = "xs", color = "#9E9E9E", wrap = true });
        }

        var flexContent = new
        {
            type = "bubble",
            header = new
            {
                type = "box", layout = "vertical", backgroundColor = severityColor,
                contents = new object[]
                {
                    new { type = "text", text = "Solar Alert + Diagnostics", color = "#FFFFFF", weight = "bold", size = "lg" },
                    new { type = "text", text = severity, color = "#FFFFFF", size = "sm" }
                }
            },
            body = new { type = "box", layout = "vertical", spacing = "sm", contents = bodyContents.ToArray() },
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
                messages = new object[] { new { type = "flex", altText = $"[{severity}] {alertTitle}", contents = flexContent } }
            };
            using var slot = await AcquireSendSlotAsync();
            var response = await client.PostAsJsonAsync(PushUrl, payload);
            _context.NotificationLogs.Add(new NotificationLog
            {
                RecipientLineId = lineUserId, Channel = NotificationChannel.Line,
                Title = alertTitle, Message = alertMessage,
                RelatedEntityType = "SystemAlert",
                Status = response.IsSuccessStatusCode ? NotificationStatus.Sent : NotificationStatus.Failed,
                ErrorMessage = !response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : null,
                SentAt = response.IsSuccessStatusCode ? DateTime.UtcNow : null
            });
            await _context.SaveChangesAsync();
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to send diagnostic alert to {UserId}", lineUserId); }
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

    public async Task SendPredictiveAlertAsync(string lineUserId, string siteName,
        string prediction, string evidence, string[] checkSteps, string[] fixSteps,
        string riskLevel, string? knownCaseRef)
    {
        if (string.IsNullOrEmpty(lineUserId)) return;
        var token = await GetChannelAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return;

        var riskColor = riskLevel switch
        {
            "High" => "#FF6F00",
            "Medium" => "#F9A825",
            _ => "#1565C0"
        };

        var bodyContents = new List<object>
        {
            new { type = "text", text = prediction, weight = "bold", size = "md", wrap = true },
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
                            new { type = "text", text = "Risk", size = "sm", color = "#AAAAAA", flex = 2 },
                            new { type = "text", text = riskLevel, size = "sm", color = riskColor, flex = 4 }
                        }
                    }
                }
            },
            new { type = "separator" },
            new { type = "text", text = "หลักฐาน (Evidence)", size = "sm", color = "#FF6F00", weight = "bold" },
            new { type = "text", text = evidence, size = "xs", color = "#666666", wrap = true }
        };

        if (checkSteps.Length > 0)
        {
            bodyContents.Add(new { type = "separator" });
            bodyContents.Add(new { type = "text", text = "สิ่งที่ต้องตรวจ", size = "sm", color = "#1565C0", weight = "bold" });
            foreach (var step in checkSteps.Take(5))
                bodyContents.Add(new { type = "text", text = $"• {step}", size = "xs", color = "#333333", wrap = true });
        }

        if (fixSteps.Length > 0)
        {
            bodyContents.Add(new { type = "separator" });
            bodyContents.Add(new { type = "text", text = "วิธีแก้ไข", size = "sm", color = "#00C853", weight = "bold" });
            foreach (var step in fixSteps.Take(5))
                bodyContents.Add(new { type = "text", text = $"• {step}", size = "xs", color = "#333333", wrap = true });
        }

        if (!string.IsNullOrEmpty(knownCaseRef))
        {
            bodyContents.Add(new { type = "separator" });
            bodyContents.Add(new { type = "text", text = $"อ้างอิง: {knownCaseRef}", size = "xs", color = "#9E9E9E", wrap = true });
        }

        var flexContent = new
        {
            type = "bubble",
            header = new
            {
                type = "box", layout = "vertical", backgroundColor = riskColor,
                contents = new object[]
                {
                    new { type = "text", text = "🔍 Predictive Alert", color = "#FFFFFF", weight = "bold", size = "lg" },
                    new { type = "text", text = "คาดว่าอาจเกิดปัญหา", color = "#FFFFFF", size = "sm" }
                }
            },
            body = new { type = "box", layout = "vertical", spacing = "md", contents = bodyContents.ToArray() },
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
                messages = new object[] { new { type = "flex", altText = $"[PREDICTIVE] {prediction}", contents = flexContent } }
            };
            using var slot = await AcquireSendSlotAsync();
            var response = await client.PostAsJsonAsync(PushUrl, payload);
            _context.NotificationLogs.Add(new NotificationLog
            {
                RecipientLineId = lineUserId, Channel = NotificationChannel.Line,
                Title = $"[PREDICTIVE] {prediction}", Message = evidence,
                RelatedEntityType = "PredictiveDiagnostic",
                Status = response.IsSuccessStatusCode ? NotificationStatus.Sent : NotificationStatus.Failed,
                ErrorMessage = !response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : null,
                SentAt = response.IsSuccessStatusCode ? DateTime.UtcNow : null
            });
            await _context.SaveChangesAsync();
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to send predictive alert to {UserId}", lineUserId); }
    }

    public async Task SendDailySummaryAsync(string lineUserId, DailySummaryData s)
    {
        if (string.IsNullOrEmpty(lineUserId)) return;
        var token = await GetChannelAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return;

        // Header status reflects SYSTEM PROBLEMS only (s.ActiveAlerts is now
        // scoped to faults, excluding cleaning/warranty reminders).  A day
        // with only a cleaning reminder shows "✓ All Systems Normal" plus a
        // separate maintenance section — no more false "system has alerts".
        var statusColor = s.OfflineSites == 0 && s.ActiveAlerts == 0 ? "#00C853"
            : s.ActiveAlerts > 0 && s.OfflineSites > 0 ? "#FF0000" : "#FF8C00";
        var statusText = s.OfflineSites == 0 && s.ActiveAlerts == 0
            ? "✓ All Systems Normal" : $"⚠ {s.ActiveAlerts} System Alert(s)";

        var bodyContents = new List<object>
        {
            new { type = "text", text = statusText, weight = "bold", size = "md", color = statusColor, wrap = true },
            new { type = "separator" },
            // ── Section 1: plant / production status ──
            new
            {
                type = "box", layout = "vertical", spacing = "sm",
                contents = new object[]
                {
                    // "monitored" here means sites with IncludesMonitoring = true
                    // (i.e. ones we actually poll).  Cleaning-only / partner-
                    // monitored sites are excluded so the count reflects what
                    // SolarApp can vouch for.
                    MakeSummaryRow("Online / Monitored", $"{s.OnlineSites}/{s.TotalSites} sites"),
                    MakeSummaryRow("Offline", $"{s.OfflineSites} site(s)", s.OfflineSites > 0 ? "#FF0000" : null),
                    MakeSummaryRow("Energy Today", $"{s.TotalEnergyTodayKwh:N1} kWh"),
                }
            },
            new { type = "separator" },
            // ── Section 2: system-problem alerts (faults) ──
            new { type = "text", text = "🔴 ปัญหาระบบ (System Alerts)", size = "sm", weight = "bold", color = "#D32F2F" },
            new
            {
                type = "box", layout = "vertical", spacing = "sm",
                contents = new object[]
                {
                    MakeSummaryRow("Active", $"{s.ActiveAlerts}", s.ActiveAlerts > 0 ? "#D32F2F" : "#00C853"),
                    MakeSummaryRow("New today", $"{s.NewAlertsToday}"),
                    MakeSummaryRow("Resolved today", $"{s.ResolvedTodayAlerts}"),
                }
            },
            new { type = "separator" },
            // ── Section 3: scheduled maintenance reminders ──
            new { type = "text", text = "🧽 งานตามกำหนด (Maintenance)", size = "sm", weight = "bold", color = "#F57C00" },
            new
            {
                type = "box", layout = "vertical", spacing = "sm",
                contents = new object[]
                {
                    MakeSummaryRow("ครบ/เลยกำหนดล้างแผง", $"{s.CleaningDueCount} site(s)", s.CleaningDueCount > 0 ? "#F57C00" : null),
                    MakeSummaryRow("ประกันใกล้หมด", $"{s.WarrantyExpiringCount} site(s)", s.WarrantyExpiringCount > 0 ? "#F57C00" : null),
                    MakeSummaryRow("Work Orders", $"{s.PendingWorkOrders} pending, {s.CompletedWorkOrdersToday} done today"),
                }
            }
        };

        // Cleaning-due site names (so the crew sees WHICH sites without
        // opening the app).
        if (s.CleaningDueSites.Count > 0)
        {
            bodyContents.Add(new { type = "text", text = "ไซต์ที่ต้องล้าง:", size = "xs", color = "#F57C00", weight = "bold", margin = "sm" });
            foreach (var site in s.CleaningDueSites.Take(5))
                bodyContents.Add(new { type = "text", text = $"  • {site}", size = "xs", color = "#F57C00", wrap = true });
        }

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

        // Footer: an optional "view pending alerts" button (only when the
        // public base URL is configured) followed by the generated-at line.
        // The button deep-links to the /Admin/Alerts page filtered to the
        // unresolved alerts — tapping it from the LINE group opens the
        // browser straight to the outstanding-alert list.
        var baseUrl = await GetAppBaseUrlAsync();
        var footerContents = new List<object>();
        if (!string.IsNullOrEmpty(baseUrl))
        {
            // Primary button → system-problem alerts.  Red + count when
            // there ARE faults; neutral "open" when the plant is clean.
            var alertsUrl = $"{baseUrl}/Admin/Alerts";
            var btnLabel = s.ActiveAlerts > 0
                ? $"🔔 ดูปัญหาระบบ ({s.ActiveAlerts})"
                : "📋 เปิดดูระบบ";
            footerContents.Add(new
            {
                type = "button",
                style = "primary",
                height = "sm",
                color = s.ActiveAlerts > 0 ? "#D32F2F" : "#1565C0",
                action = new { type = "uri", label = btnLabel, uri = alertsUrl }
            });
            // Separate cleaning button → Maintenance page, only when there
            // are cleaning-due reminders.  Keeps "fault" and "cleaning"
            // one tap apart.
            if (s.CleaningDueCount > 0)
            {
                footerContents.Add(new
                {
                    type = "button",
                    style = "secondary",
                    height = "sm",
                    action = new { type = "uri", label = $"🧽 งานล้างแผง ({s.CleaningDueCount})", uri = $"{baseUrl}/Maintenance" }
                });
            }
            if (s.PendingWorkOrders > 0)
            {
                footerContents.Add(new
                {
                    type = "button",
                    style = "link",
                    height = "sm",
                    action = new { type = "uri", label = $"📋 Work Orders ค้าง ({s.PendingWorkOrders})", uri = $"{baseUrl}/WorkOrders" }
                });
            }
        }
        else
        {
            // No base URL yet → no tappable buttons.  Say so, rather than
            // silently shipping a report with no way to act on it; the
            // operator otherwise has no idea a feature is missing.
            footerContents.Add(new
            {
                type = "text",
                text = "ℹ️ ตั้งค่า App Base URL ที่ Admin → ตั้งค่า LINE เพื่อให้มีปุ่มกดดู Alert",
                size = "xs", color = "#FF8C00", wrap = true, align = "center",
            });
        }
        footerContents.Add(new { type = "text", text = $"Report generated {DateTime.Now:HH:mm}", size = "xs", color = "#AAAAAA", align = "center", margin = "sm" });

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
                type = "box", layout = "vertical", spacing = "sm",
                contents = footerContents.ToArray()
            }
        };

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

            var payload = new
            {
                to = lineUserId,
                messages = new object[] { new { type = "flex", altText = $"[Daily Summary] {s.OnlineSites}/{s.TotalSites} online · {s.ActiveAlerts} system alert(s) · {s.CleaningDueCount} cleaning due", contents = flexContent } }
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

    public async Task SendMonthlyReportAsync(string lineUserId, MonthlyReportData r)
    {
        if (string.IsNullOrEmpty(lineUserId)) return;
        var token = await GetChannelAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return;

        var changeText = r.ChangeVsPreviousPercent.HasValue
            ? (r.ChangeVsPreviousPercent.Value >= 0 ? "▲ " : "▼ ")
              + Math.Abs(r.ChangeVsPreviousPercent.Value).ToString("N0") + "% จากเดือนก่อน"
            : "เดือนแรกของรายงาน";
        var changeColor = !r.ChangeVsPreviousPercent.HasValue ? "#AAAAAA"
            : r.ChangeVsPreviousPercent.Value >= 0 ? "#00C853" : "#FF8C00";

        var bodyContents = new List<object>
        {
            // The kWh number leads because it's the one the customer repeats
            // to other people. Everything below it is supporting evidence.
            new
            {
                type = "box", layout = "vertical", spacing = "xs",
                contents = new object[]
                {
                    new { type = "text", text = r.TotalKwh.ToString("N0") + " kWh",
                          weight = "bold", size = "xxl", color = "#F57C00" },
                    new { type = "text", text = changeText, size = "sm", color = changeColor }
                }
            },
            new { type = "separator", margin = "md" },
            new
            {
                type = "box", layout = "vertical", spacing = "sm", margin = "md",
                contents = new object[]
                {
                    MakeSummaryRow("ประหยัดค่าไฟ", "฿" + r.SavingsThb.ToString("N0"), "#00C853"),
                    MakeSummaryRow("ลด CO₂", r.Co2SavedKg.ToString("N0") + " kg  (≈ ปลูกต้นไม้ " + r.TreesEquivalent + " ต้น)"),
                    MakeSummaryRow("ผลิตสูงสุด", r.BestDayKwh.ToString("N1") + " kWh"
                        + (string.IsNullOrEmpty(r.BestDayDate) ? "" : " (" + r.BestDayDate + ")")),
                    MakeSummaryRow("ระบบทำงานปกติ", r.HealthyDays + "/" + r.DaysInMonth + " วัน",
                        r.HealthyDays == r.DaysInMonth ? "#00C853" : "#FF8C00"),
                }
            }
        };

        // Only mention service visits when there were any — "ล้างแผง 0 ครั้ง"
        // reads as a complaint about us rather than a summary.
        if (r.CleaningsDone > 0 || r.WorkOrdersCompleted > 0)
        {
            var service = new List<object>();
            if (r.CleaningsDone > 0)
                service.Add(MakeSummaryRow("ล้างแผง", r.CleaningsDone + " ครั้ง"));
            if (r.WorkOrdersCompleted > 0)
                service.Add(MakeSummaryRow("งานบริการ", r.WorkOrdersCompleted + " รายการ"));

            bodyContents.Add(new { type = "separator", margin = "md" });
            bodyContents.Add(new
            {
                type = "box", layout = "vertical", spacing = "sm", margin = "md",
                contents = service.ToArray()
            });
        }

        var footerContents = new List<object>();
        var baseUrl = await GetAppBaseUrlAsync();
        if (baseUrl != null)
        {
            footerContents.Add(new
            {
                type = "button", style = "primary", color = "#F57C00", height = "sm",
                action = new { type = "uri", label = "ดูรายละเอียดย้อนหลัง", uri = $"{baseUrl}/My/History" }
            });
        }

        // A dictionary rather than an anonymous type: LINE rejects a bubble
        // with "footer": null, and the footer only exists when App.BaseUrl is
        // configured — so the key has to be absent, not empty.
        var flexContent = new Dictionary<string, object>
        {
            ["type"] = "bubble",
            ["header"] = new
            {
                type = "box", layout = "vertical",
                backgroundColor = "#F57C00", paddingAll = "16px",
                contents = new object[]
                {
                    new { type = "text", text = "☀ สรุปผลผลิตประจำเดือน", color = "#FFFFFF", weight = "bold", size = "md" },
                    new { type = "text", text = r.PeriodLabel + " · " + r.SiteCount + " ไซต์",
                          color = "#FFFFFF", size = "sm" }
                }
            },
            ["body"] = new { type = "box", layout = "vertical", spacing = "md", contents = bodyContents.ToArray() }
        };

        if (footerContents.Count > 0)
        {
            flexContent["footer"] = new
            {
                type = "box", layout = "vertical", spacing = "sm", contents = footerContents.ToArray()
            };
        }

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
                        altText = $"สรุปผลผลิต {r.PeriodLabel}: {r.TotalKwh:N0} kWh · ประหยัด ฿{r.SavingsThb:N0}",
                        contents = flexContent
                    }
                }
            };

            using var slot = await AcquireSendSlotAsync();
            var response = await client.PostAsJsonAsync(PushUrl, payload);

            _context.NotificationLogs.Add(new NotificationLog
            {
                RecipientLineId = lineUserId,
                Channel = NotificationChannel.Line,
                Title = $"Monthly Report {r.PeriodLabel}",
                Message = $"{r.TotalKwh:N0} kWh across {r.SiteCount} site(s)",
                RelatedEntityType = "MonthlyReport",
                Status = response.IsSuccessStatusCode ? NotificationStatus.Sent : NotificationStatus.Failed,
                ErrorMessage = !response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : null,
                SentAt = response.IsSuccessStatusCode ? DateTime.UtcNow : null
            });
            await _context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send monthly report to {UserId}", lineUserId);
        }
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
