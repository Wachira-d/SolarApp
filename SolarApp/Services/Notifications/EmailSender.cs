using System.Net;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using SolarApp.Data;
using SolarApp.Services.Security;

namespace SolarApp.Services.Notifications;

/// <summary>
/// Sends customer alert e-mail. Separate from ASP.NET Identity's IEmailSender
/// on purpose: that one is for account mail and is not configured here, and
/// conflating the two would let a password-reset flow start sending through
/// the alert SMTP account (or vice versa) without anyone deciding it should.
/// </summary>
public interface ICustomerEmailSender
{
    /// <summary>True when host and from-address are set — enough to try a send.</summary>
    Task<bool> IsConfiguredAsync();

    /// <summary>Sends one message. Throws on any failure so callers can log it as Failed.</summary>
    Task SendAsync(string to, string subject, string htmlBody, string? textBody = null, CancellationToken ct = default);
}

/// <summary>SMTP settings as stored in SystemSettings ("Email.*").</summary>
public class EmailSettings
{
    public const string HostKey = "Email.SmtpHost";
    public const string PortKey = "Email.SmtpPort";
    public const string UseSslKey = "Email.UseSsl";
    public const string UsernameKey = "Email.Username";
    public const string PasswordKey = "Email.Password";
    public const string FromAddressKey = "Email.FromAddress";
    public const string FromNameKey = "Email.FromName";

    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string? Username { get; set; }
    /// <summary>Plaintext, after unprotecting. Never rendered back to a page.</summary>
    public string? Password { get; set; }
    public string? FromAddress { get; set; }
    public string? FromName { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);

    public static async Task<EmailSettings> LoadAsync(ApplicationDbContext context, ICredentialProtector protector)
    {
        var keys = new[] { HostKey, PortKey, UseSslKey, UsernameKey, PasswordKey, FromAddressKey, FromNameKey };
        var rows = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            context.SystemSettings.Where(s => keys.Contains(s.Key)));
        string? Get(string k) => rows.FirstOrDefault(r => r.Key == k)?.Value is { } v && !string.IsNullOrWhiteSpace(v)
            ? v.Trim() : null;

        return new EmailSettings
        {
            Host = Get(HostKey),
            Port = int.TryParse(Get(PortKey), out var p) && p > 0 && p < 65536 ? p : 587,
            UseSsl = !bool.TryParse(Get(UseSslKey), out var ssl) || ssl,
            Username = Get(UsernameKey),
            // Stored as "enc:…" by the settings page; legacy plaintext still reads.
            Password = protector.Unprotect(rows.FirstOrDefault(r => r.Key == PasswordKey)?.Value),
            FromAddress = Get(FromAddressKey),
            FromName = Get(FromNameKey),
        };
    }

    /// <summary>
    /// Basic shape check for operator-typed addresses. MailboxAddress.TryParse
    /// accepts display-name forms and bare local parts, which would then fail
    /// at send time with an unhelpful SMTP error.
    /// </summary>
    public static bool IsValidAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length > 254) return false;
        var a = address.Trim();
        if (a.Contains(' ') || a.Contains('<') || a.Contains('>')) return false;
        var at = a.IndexOf('@');
        if (at <= 0 || at != a.LastIndexOf('@') || at == a.Length - 1) return false;
        var domain = a[(at + 1)..];
        return domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.')
               && MailboxAddress.TryParse(a, out _);
    }
}

public class SmtpCustomerEmailSender : ICustomerEmailSender
{
    private readonly ApplicationDbContext _context;
    private readonly ICredentialProtector _protector;
    private readonly ILogger<SmtpCustomerEmailSender> _logger;

    public SmtpCustomerEmailSender(ApplicationDbContext context, ICredentialProtector protector,
        ILogger<SmtpCustomerEmailSender> logger)
    {
        _context = context;
        _protector = protector;
        _logger = logger;
    }

    public async Task<bool> IsConfiguredAsync() =>
        (await EmailSettings.LoadAsync(_context, _protector)).IsConfigured;

    public async Task SendAsync(string to, string subject, string htmlBody, string? textBody = null,
        CancellationToken ct = default)
    {
        var s = await EmailSettings.LoadAsync(_context, _protector);
        if (!s.IsConfigured)
            throw new InvalidOperationException("ยังไม่ได้ตั้งค่า SMTP (Email.SmtpHost / Email.FromAddress)");

        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(s.FromName ?? "SolarApp", s.FromAddress));
        msg.To.Add(MailboxAddress.Parse(to.Trim()));
        msg.Subject = subject;
        var body = new BodyBuilder { HtmlBody = htmlBody, TextBody = textBody ?? CustomerEmailTemplate.ToPlainText(htmlBody) };
        msg.Body = body.ToMessageBody();

        // 465 is implicit TLS; anything else with SSL on means STARTTLS. Picking
        // by port is what every mail client does and saves the operator a
        // setting they would get wrong.
        var security = !s.UseSsl ? SecureSocketOptions.None
            : s.Port == 465 ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTls;

        using var client = new SmtpClient { Timeout = 20_000 };
        await client.ConnectAsync(s.Host, s.Port, security, ct);
        if (!string.IsNullOrEmpty(s.Username))
            await client.AuthenticateAsync(s.Username, s.Password ?? "", ct);
        await client.SendAsync(msg, ct);
        await client.DisconnectAsync(true, ct);
        _logger.LogInformation("Customer e-mail sent to {To}: {Subject}", to, subject);
    }
}

/// <summary>
/// The customer alert e-mail. Same words as the LINE message — a customer who
/// gets both should not wonder whether they describe two different problems —
/// wrapped in a plain, inline-styled layout because mail clients strip
/// &lt;style&gt; blocks and external CSS.
/// </summary>
public static class CustomerEmailTemplate
{
    public static string Subject(string siteName, string title) =>
        $"[SolarApp] {OneLine(title)} — {OneLine(siteName)}";

    /// <param name="occurredUtc">Alert time; rendered in site-local (Thai) time.</param>
    /// <param name="siteUrl">Absolute link to the site page, or null to omit the button.</param>
    public static string BuildAlertHtml(string siteName, string title, string message,
        DateTime occurredUtc, string? siteUrl)
    {
        var when = SiteTime.ToSiteTime(occurredUtc).ToString("dd/MM/yyyy เวลา HH:mm") + " น.";
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"th\"><head><meta charset=\"utf-8\">")
          .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
          .Append("<title>").Append(Enc(title)).Append("</title></head>")
          .Append("<body style=\"margin:0;padding:0;background:#f4f5f7;font-family:Tahoma,'Sarabun',Arial,sans-serif;color:#212529;\">")
          .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#f4f5f7;padding:16px 0;\"><tr><td align=\"center\">")
          .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:560px;background:#ffffff;border-radius:8px;overflow:hidden;\">")
          .Append("<tr><td style=\"background:#212529;color:#ffc107;padding:14px 20px;font-size:18px;font-weight:bold;\">☀ SolarApp</td></tr>")
          .Append("<tr><td style=\"padding:20px;\">")
          .Append("<h2 style=\"margin:0 0 12px 0;font-size:18px;line-height:1.4;\">").Append(Enc(title)).Append("</h2>")
          .Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" style=\"font-size:14px;margin-bottom:14px;\">")
          .Append("<tr><td style=\"color:#6c757d;padding:2px 12px 2px 0;\">ไซต์</td><td style=\"font-weight:bold;\">").Append(Enc(siteName)).Append("</td></tr>")
          .Append("<tr><td style=\"color:#6c757d;padding:2px 12px 2px 0;\">เวลา</td><td>").Append(Enc(when)).Append("</td></tr>")
          .Append("</table>")
          .Append("<div style=\"font-size:14px;line-height:1.6;\">").Append(EncMultiline(message)).Append("</div>");

        if (!string.IsNullOrWhiteSpace(siteUrl))
        {
            sb.Append("<p style=\"margin:20px 0 0 0;\"><a href=\"").Append(Enc(siteUrl))
              .Append("\" style=\"display:inline-block;background:#ffc107;color:#212529;text-decoration:none;padding:10px 18px;border-radius:6px;font-weight:bold;\">ดูข้อมูลไซต์</a></p>");
        }

        sb.Append("</td></tr>")
          .Append("<tr><td style=\"padding:12px 20px;background:#f8f9fa;color:#6c757d;font-size:12px;\">")
          .Append("อีเมลนี้ส่งอัตโนมัติจากระบบติดตามโซลาร์เซลล์ หากต้องการเปลี่ยนการแจ้งเตือน กรุณาติดต่อผู้ดูแลระบบ")
          .Append("</td></tr></table></td></tr></table></body></html>");
        return sb.ToString();
    }

    public static string BuildTestHtml(string? siteUrl) =>
        BuildAlertHtml("ทดสอบระบบ", "ทดสอบการส่งอีเมลแจ้งเตือน",
            "หากท่านได้รับอีเมลนี้ แสดงว่าการตั้งค่าอีเมลแจ้งเตือนของ SolarApp ใช้งานได้แล้ว",
            DateTime.UtcNow, siteUrl);

    /// <summary>
    /// Splits a LINE-style message into headline + body: its first non-empty
    /// line is the headline the customer reads first in both channels.
    /// </summary>
    public static (string Title, string Body) SplitLineText(string text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        var first = Array.FindIndex(lines, l => !string.IsNullOrWhiteSpace(l));
        if (first < 0) return ("แจ้งเตือนระบบโซลาร์", "");
        return (lines[first].Trim(), string.Join("\n", lines.Skip(first + 1)).Trim('\n', ' '));
    }

    public static string ToPlainText(string html)
    {
        // Drop <head> first, or the <title> would open the text part twice.
        var bodyOnly = System.Text.RegularExpressions.Regex.Replace(html, "<head>.*?</head>", "",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        var noTags = System.Text.RegularExpressions.Regex.Replace(
            bodyOnly.Replace("<br>", "\n").Replace("</tr>", "\n").Replace("</p>", "\n").Replace("</h2>", "\n"),
            "<[^>]+>", "");
        return WebUtility.HtmlDecode(noTags).Trim();
    }

    private static string Enc(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string EncMultiline(string? s) =>
        Enc(s).Replace("\r\n", "\n").Replace("\n", "<br>");

    private static string OneLine(string s) => s.Replace("\r", " ").Replace("\n", " ").Trim();
}
