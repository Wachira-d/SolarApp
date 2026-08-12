using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models.Enums;

namespace SolarApp.Controllers;

/// <summary>
/// Lets a customer ask, rather than only be told.
///
/// LINE was one-way: alerts and summaries went out, and anything a customer
/// wanted to know in between meant opening the portal, remembering a password,
/// or ringing the office. The commonest question by far is "is it working
/// right now" — which the app can answer in one line and previously answered
/// with a phone call.
///
/// This also captures the ID needed to configure anything at all. Getting a
/// customer's LINE user ID or a group ID meant reading raw webhook payloads in
/// a debugger; now the bot replies with it, so an operator can copy it out of
/// the chat.
///
/// Anonymous by necessity — LINE calls this, not a signed-in user. Every
/// request is authenticated by HMAC signature instead, and a reply only ever
/// contains data belonging to the LINE ID that sent the message.
/// </summary>
[ApiController]
[Route("api/line/webhook")]
[AllowAnonymous]
public class LineWebhookController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LineWebhookController> _logger;

    private const string ReplyUrl = "https://api.line.me/v2/bot/message/reply";

    public LineWebhookController(
        ApplicationDbContext context,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<LineWebhookController> logger)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Post()
    {
        // Read the raw body: the signature is over exact bytes, so anything
        // that re-serialises the JSON first will never verify.
        string body;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            body = await reader.ReadToEndAsync();

        var channelSecret = await GetSettingAsync("Line.ChannelSecret", "LineMessaging:ChannelSecret");
        if (string.IsNullOrWhiteSpace(channelSecret))
        {
            // Refuse rather than process unverified input. An unconfigured
            // secret would otherwise turn this into an open endpoint that
            // anyone could use to probe for customer data.
            _logger.LogWarning("LINE webhook received but Line.ChannelSecret is not configured — rejecting");
            return Unauthorized();
        }

        if (!VerifySignature(body, channelSecret, Request.Headers["X-Line-Signature"].ToString()))
        {
            _logger.LogWarning("LINE webhook signature verification failed");
            return Unauthorized();
        }

        // Answer LINE immediately and do the work inline; the handlers are a
        // couple of indexed queries. LINE retries on non-2xx, so any throw
        // below would replay the whole batch.
        try
        {
            await HandleEventsAsync(body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LINE webhook handling failed");
        }

        return Ok();
    }

    private async Task HandleEventsAsync(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("events", out var events)
            || events.ValueKind != JsonValueKind.Array) return;

        foreach (var ev in events.EnumerateArray())
        {
            var type = ev.TryGetProperty("type", out var t) ? t.GetString() : null;
            var replyToken = ev.TryGetProperty("replyToken", out var rt) ? rt.GetString() : null;
            if (string.IsNullOrEmpty(replyToken)) continue;

            var source = ev.TryGetProperty("source", out var src) ? src : default;
            var userId = source.ValueKind == JsonValueKind.Object
                         && source.TryGetProperty("userId", out var u) ? u.GetString() : null;
            var groupId = source.ValueKind == JsonValueKind.Object
                          && source.TryGetProperty("groupId", out var g) ? g.GetString() : null;

            if (type == "join" && groupId != null)
            {
                // A bot added to a group needs its group ID pasted into
                // settings. Reading it out of a debugger was the old way.
                await ReplyAsync(replyToken,
                    "สวัสดีครับ 🌞 SolarApp พร้อมส่งแจ้งเตือนเข้ากลุ่มนี้แล้ว\n\n"
                    + $"Group ID:\n{groupId}\n\n"
                    + "นำ ID นี้ไปใส่ในหน้า ตั้งค่า LINE → Alert Group ID");
                continue;
            }

            if (type == "follow" && userId != null)
            {
                await ReplyAsync(replyToken,
                    "สวัสดีครับ 🌞 ขอบคุณที่เพิ่ม SolarApp เป็นเพื่อน\n\n"
                    + $"LINE User ID ของคุณ:\n{userId}\n\n"
                    + "แจ้ง ID นี้กับผู้ดูแลระบบเพื่อผูกกับบัญชีลูกค้า "
                    + "จากนั้นจะได้รับแจ้งเตือนและสรุปผลผลิตอัตโนมัติ\n\n"
                    + "พิมพ์ \"เมนู\" เพื่อดูคำสั่งที่ใช้ได้");
                continue;
            }

            if (type != "message") continue;
            if (!ev.TryGetProperty("message", out var msg)) continue;
            if (!msg.TryGetProperty("type", out var mt) || mt.GetString() != "text") continue;

            var text = (msg.TryGetProperty("text", out var txt) ? txt.GetString() : null)?.Trim() ?? "";
            await ReplyAsync(replyToken, await BuildReplyAsync(text, userId, groupId));
        }
    }

    /// <summary>
    /// Matches on substrings rather than exact commands. A customer types
    /// "ระบบเป็นไงบ้างครับ", not "/status", and a bot that answers only the
    /// exact word is a bot nobody uses twice.
    /// </summary>
    private async Task<string> BuildReplyAsync(string text, string? userId, string? groupId)
    {
        var lower = text.ToLowerInvariant();

        if (lower.Contains("id") || text.Contains("ไอดี"))
        {
            return groupId != null
                ? $"Group ID ของกลุ่มนี้:\n{groupId}"
                : userId != null
                    ? $"LINE User ID ของคุณ:\n{userId}"
                    : "ไม่พบ ID ของผู้ส่ง";
        }

        if (text.Contains("เมนู") || lower.Contains("help") || text.Contains("ช่วย"))
        {
            return "คำสั่งที่ใช้ได้ 🌞\n\n"
                 + "• \"สถานะ\" — ระบบทำงานปกติไหม ผลิตได้เท่าไหร่วันนี้\n"
                 + "• \"เดือนนี้\" — ผลผลิตและค่าไฟที่ประหยัดได้เดือนนี้\n"
                 + "• \"แจ้งเตือน\" — รายการที่ยังไม่ได้จัดการ\n"
                 + "• \"ID\" — ดู LINE ID ของคุณหรือของกลุ่ม";
        }

        if (userId == null)
            return "ไม่สามารถระบุผู้ใช้ได้ — กรุณาทักแชทส่วนตัวกับบอท";

        var customer = await _context.Customers.AsNoTracking()
            .Include(c => c.Installations)
            .FirstOrDefaultAsync(c => c.LineUserId == userId);

        // Only ever answer for the LINE ID that asked. An unlinked ID gets
        // nothing but instructions — this endpoint is public.
        if (customer == null || !customer.Installations.Any())
        {
            return "LINE ID นี้ยังไม่ได้ผูกกับบัญชีลูกค้า\n\n"
                 + $"แจ้ง ID นี้กับผู้ดูแลระบบ:\n{userId}";
        }

        var siteIds = customer.Installations.Select(i => i.Id).ToList();
        var today = Services.SiteTime.Today;

        if (text.Contains("แจ้งเตือน") || lower.Contains("alert"))
        {
            var alerts = await _context.SystemAlerts.AsNoTracking()
                .Where(a => siteIds.Contains(a.InstallationId) && a.ResolvedAt == null)
                .OrderBy(a => a.Severity)
                .ThenByDescending(a => a.OccurredAt)
                .Take(5)
                .Select(a => new { a.Title, a.Message, Site = a.Installation.SiteName, a.OccurredAt })
                .ToListAsync();

            if (!alerts.Any()) return "✅ ไม่มีแจ้งเตือนค้างอยู่ ระบบทำงานปกติทุกไซต์";

            var lines = alerts.Select(a =>
                $"• {Models.SystemAlert.TitleFor(a.Title, a.Message)}\n  ({a.Site} · {a.OccurredAt.AddHours(7):dd/MM HH:mm})");
            return $"⚠ แจ้งเตือนที่ยังไม่จัดการ {alerts.Count} รายการ\n\n" + string.Join("\n", lines);
        }

        if (text.Contains("เดือน") || lower.Contains("month"))
        {
            var monthStart = new DateTime(today.Year, today.Month, 1);
            var monthKwh = await _context.EnergyData.AsNoTracking()
                .Where(e => siteIds.Contains(e.InstallationId)
                            && e.Granularity == DataGranularity.Daily
                            && e.Timestamp >= monthStart && e.Timestamp <= today)
                .SumAsync(e => (decimal?)e.EnergyGenerationKwh) ?? 0;

            var rate = customer.Installations.Average(i =>
                i.ElectricityRatePerKwh > 0 ? i.ElectricityRatePerKwh : 4.0m);

            return $"📊 เดือนนี้ ({monthStart:MM/yyyy})\n\n"
                 + $"ผลิตได้: {monthKwh:N1} kWh\n"
                 + $"ประหยัดค่าไฟ: ฿{monthKwh * rate:N0}\n"
                 + $"ลด CO₂: {monthKwh * 0.5m:N0} kg";
        }

        // Anything else is treated as "how is it going" — the question this
        // exists to answer, and the one a customer phrases twenty ways.
        var todayKwh = await _context.EnergyData.AsNoTracking()
            .Where(e => siteIds.Contains(e.InstallationId)
                        && e.Granularity == DataGranularity.Daily
                        && e.Timestamp.Date == today)
            .SumAsync(e => (decimal?)e.EnergyGenerationKwh) ?? 0;

        var online = customer.Installations.Count(i => i.Status == InstallationStatus.Active);
        var total = customer.Installations.Count;

        var openAlerts = await _context.SystemAlerts.AsNoTracking()
            .CountAsync(a => siteIds.Contains(a.InstallationId) && a.ResolvedAt == null);

        var header = online == total && openAlerts == 0
            ? "✅ ระบบทำงานปกติทุกไซต์"
            : online < total
                ? $"⚠ มี {total - online} ไซต์ที่ขาดการเชื่อมต่อ"
                : $"⚠ มีแจ้งเตือนค้าง {openAlerts} รายการ";

        return $"{header}\n\n"
             + $"ไซต์ออนไลน์: {online}/{total}\n"
             + $"ผลิตวันนี้: {todayKwh:N1} kWh\n"
             + (openAlerts > 0 ? $"แจ้งเตือนค้าง: {openAlerts} รายการ\n" : "")
             + "\nพิมพ์ \"เมนู\" เพื่อดูคำสั่งอื่น";
    }

    /// <summary>
    /// Constant-time comparison — a fast-exit string compare on a signature
    /// leaks, byte by byte, what the correct value is.
    /// </summary>
    private static bool VerifySignature(string body, string channelSecret, string? headerSignature)
    {
        if (string.IsNullOrEmpty(headerSignature)) return false;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(channelSecret));
        var computed = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(body)));

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(headerSignature));
    }

    private async Task ReplyAsync(string replyToken, string message)
    {
        var token = await GetSettingAsync("Line.ChannelAccessToken", "LineMessaging:ChannelAccessToken");
        if (string.IsNullOrWhiteSpace(token)) return;

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

            // Reply (not push) — free, and it's the correct API for answering
            // a message. LINE rejects text over 5000 characters.
            await client.PostAsJsonAsync(ReplyUrl, new
            {
                replyToken,
                messages = new object[]
                {
                    new { type = "text", text = message.Length > 4900 ? message[..4900] : message }
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LINE reply failed");
        }
    }

    /// <summary>Database setting first (set via the admin UI), then appsettings.</summary>
    private async Task<string?> GetSettingAsync(string dbKey, string configKey)
    {
        var setting = await _context.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == dbKey);
        return setting != null && !string.IsNullOrWhiteSpace(setting.Value)
            ? setting.Value
            : _configuration[configKey];
    }
}
