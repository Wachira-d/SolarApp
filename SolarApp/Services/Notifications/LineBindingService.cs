using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models.Enums;
using SolarApp.Models.Notifications;

namespace SolarApp.Services.Notifications;

public enum LineCodeStatus { Valid, Expired, Used, NotFound }

public enum LineBindOutcome { Bound, AlreadyBound, Expired, Used, NotFound }

/// <summary>What the public landing page may show about a code — first name and site only.</summary>
public record LineCodeLookup(LineCodeStatus Status, string? Code, string? CustomerFirstName, string? SiteName,
    DateTime? ExpiresAt);

public record LineBindResult(LineBindOutcome Outcome, string? CustomerName = null, string? SiteName = null,
    int? CustomerId = null, bool CanQueryStatus = false);

/// <summary>
/// "ผูก LINE" by link instead of by copying a user id out of the chat.
///
/// The old flow was: customer adds the bot, bot replies with a 33-character
/// id, customer retypes or forwards it to the office, operator pastes it into
/// the customer record. Every step lost people, and a mistyped id failed
/// silently. Now the operator hands out a link; the customer taps two buttons
/// and the webhook does the pasting.
/// </summary>
public interface ILineBindingService
{
    Task<LineBindingCode> CreateCodeAsync(int customerId, int? installationId, string? createdByUserId);
    Task<LineCodeLookup> LookupAsync(string? code);

    /// <param name="displayNameLookup">
    /// Called only when a recipient is about to be created/updated, so a bad
    /// or replayed code never costs a LINE profile API call.
    /// </param>
    Task<LineBindResult> BindAsync(string code, string lineUserId, Func<Task<string?>>? displayNameLookup = null);
}

public class LineBindingService : ILineBindingService
{
    /// <summary>
    /// No 0/O, 1/I/L: the code is read off a phone screen and sometimes
    /// typed by hand, and those pairs are where people get it wrong.
    /// </summary>
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const int CodeLength = 8;
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    // Two groups of four, optionally split by a space or hyphen, not glued to
    // other letters/digits. Applied to upper-cased text so "abcd2345" works.
    private static readonly Regex CandidateRx = new(
        @"(?<![A-Z0-9])([A-Z0-9]{4})[\s\-]?([A-Z0-9]{4})(?![A-Z0-9])", RegexOptions.Compiled);

    private readonly ApplicationDbContext _context;
    private readonly ILogger<LineBindingService> _logger;
    private readonly TimeProvider _clock;

    public LineBindingService(ApplicationDbContext context, ILogger<LineBindingService> logger,
        TimeProvider? clock = null)
    {
        _context = context;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    // ── Codes ──────────────────────────────────────────────────────────

    /// <summary>
    /// A random code that always mixes letters and digits. All-letter codes
    /// would collide with ordinary English words a customer types to the bot
    /// ("MESSAGES" is valid in this alphabet), and the webhook must not treat
    /// those as failed binding attempts.
    /// </summary>
    public static string GenerateCode()
    {
        Span<char> c = stackalloc char[CodeLength];
        while (true)
        {
            for (var i = 0; i < CodeLength; i++)
                c[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            var code = new string(c);
            if (IsWellFormed(code)) return code;
        }
    }

    public static bool IsWellFormed(string? code) =>
        code is { Length: CodeLength }
        && code.All(ch => Alphabet.Contains(ch))
        && code.Any(char.IsDigit)
        && code.Any(char.IsLetter);

    /// <summary>Upper-case and drop separators, for codes typed into the URL or chat.</summary>
    public static string Normalize(string? code) =>
        new string((code ?? "").ToUpperInvariant().Where(ch => ch is not (' ' or '-')).ToArray());

    /// <summary>
    /// Finds a binding code in a chat message: "ผูกบัญชี ABCD2345", the bare
    /// code, lower case, or split "ABCD-2345". Null when there is none.
    /// </summary>
    public static string? ExtractCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (Match m in CandidateRx.Matches(text.ToUpperInvariant()))
        {
            var candidate = m.Groups[1].Value + m.Groups[2].Value;
            if (IsWellFormed(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>The customer said they are binding, so a miss deserves an answer.</summary>
    public static bool HasBindIntent(string? text) =>
        !string.IsNullOrEmpty(text) && text.Contains("ผูก");

    public async Task<LineBindingCode> CreateCodeAsync(int customerId, int? installationId, string? createdByUserId)
    {
        // 31^8 ≈ 8.5e11 — a collision is essentially impossible, but the
        // column is unique and a retry is cheaper than a 500.
        string code;
        var attempts = 0;
        do
        {
            code = GenerateCode();
            if (++attempts > 10) throw new InvalidOperationException("Could not allocate a unique LINE binding code");
        } while (await _context.LineBindingCodes.AnyAsync(c => c.Code == code));

        var now = UtcNow;
        var row = new LineBindingCode
        {
            Code = code,
            CustomerId = customerId,
            InstallationId = installationId,
            CreatedByUserId = createdByUserId,
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
        };
        _context.LineBindingCodes.Add(row);
        await _context.SaveChangesAsync();
        return row;
    }

    public async Task<LineCodeLookup> LookupAsync(string? code)
    {
        var normalized = Normalize(code);
        if (!IsWellFormed(normalized)) return new LineCodeLookup(LineCodeStatus.NotFound, null, null, null, null);

        // Include through the Customer query filter: a deleted customer's
        // codes read as unknown rather than binding to a hidden record.
        var row = await _context.LineBindingCodes.AsNoTracking()
            .Include(c => c.Customer)
            .FirstOrDefaultAsync(c => c.Code == normalized);
        if (row == null) return new LineCodeLookup(LineCodeStatus.NotFound, null, null, null, null);

        var site = await SiteNameAsync(row.InstallationId);
        var status = row.UsedAt != null ? LineCodeStatus.Used
            : UtcNow >= row.ExpiresAt ? LineCodeStatus.Expired
            : LineCodeStatus.Valid;
        return new LineCodeLookup(status, row.Code, row.Customer.FirstName, site, row.ExpiresAt);
    }

    public async Task<LineBindResult> BindAsync(string code, string lineUserId, Func<Task<string?>>? displayNameLookup = null)
    {
        var normalized = Normalize(code);
        if (!IsWellFormed(normalized) || string.IsNullOrWhiteSpace(lineUserId))
            return new LineBindResult(LineBindOutcome.NotFound);
        lineUserId = lineUserId.Trim();

        var row = await _context.LineBindingCodes
            .Include(c => c.Customer)
            .FirstOrDefaultAsync(c => c.Code == normalized);
        if (row == null) return new LineBindResult(LineBindOutcome.NotFound);

        var customer = row.Customer;
        var site = await SiteNameAsync(row.InstallationId);

        if (row.UsedAt != null)
        {
            // LINE redelivers webhooks and customers press "send" twice; the
            // same person repeating the same code gets a friendly "already
            // done", not an error.
            return string.Equals(row.UsedByLineUserId, lineUserId, StringComparison.Ordinal)
                ? new LineBindResult(LineBindOutcome.AlreadyBound, customer.FullName, site, customer.Id,
                    customer.LineUserId == lineUserId)
                : new LineBindResult(LineBindOutcome.Used);
        }
        if (UtcNow >= row.ExpiresAt) return new LineBindResult(LineBindOutcome.Expired);

        string? displayName = null;
        if (displayNameLookup != null)
        {
            try { displayName = await displayNameLookup(); }
            catch (Exception ex) { _logger.LogDebug(ex, "LINE profile lookup failed for {UserId}", lineUserId); }
        }
        if (displayName is { Length: > 100 }) displayName = displayName[..100];

        var now = UtcNow;
        var recipient = await _context.CustomerNotificationRecipients
            .FirstOrDefaultAsync(r => r.CustomerId == customer.Id
                                      && r.Channel == NotificationChannel.Line
                                      && r.Address == lineUserId);
        if (recipient == null)
        {
            _context.CustomerNotificationRecipients.Add(new CustomerNotificationRecipient
            {
                CustomerId = customer.Id,
                InstallationId = row.InstallationId,
                Channel = NotificationChannel.Line,
                Address = lineUserId,
                DisplayName = displayName,
                IsEnabled = true,
                VerifiedAt = now,
                CreatedAt = now,
            });
        }
        else
        {
            // Re-binding switches a recipient the operator disabled back on —
            // the customer just asked for it. Two different site-scoped links
            // for the same person mean "both", which is every site.
            recipient.IsEnabled = true;
            recipient.VerifiedAt = now;
            if (!string.IsNullOrWhiteSpace(displayName)) recipient.DisplayName = displayName;
            if (recipient.InstallationId != row.InstallationId) recipient.InstallationId = null;
        }

        // The status/เดือนนี้ chat commands and the existing customer alert
        // step key off Customer.LineUserId. Fill it when empty; never replace
        // an id the operator set — that person would silently stop getting alerts.
        if (string.IsNullOrWhiteSpace(customer.LineUserId))
        {
            customer.LineUserId = lineUserId;
            customer.UpdatedAt = now;
        }

        row.UsedAt = now;
        row.UsedByLineUserId = lineUserId.Length > 64 ? lineUserId[..64] : lineUserId;
        await _context.SaveChangesAsync();

        _logger.LogInformation("LINE {UserId} bound to customer {CustomerId} via code", lineUserId, customer.Id);
        return new LineBindResult(LineBindOutcome.Bound, customer.FullName, site, customer.Id,
            customer.LineUserId == lineUserId);
    }

    /// <summary>The chat reply for a binding attempt.</summary>
    public static string BuildReply(LineBindResult r)
    {
        var siteLine = string.IsNullOrWhiteSpace(r.SiteName) ? "" : $"\n📍 ไซต์: {r.SiteName}";
        var statusHint = r.CanQueryStatus ? "\n\nพิมพ์ \"สถานะ\" เพื่อดูการทำงานของระบบได้ทุกเมื่อ" : "";
        return r.Outcome switch
        {
            LineBindOutcome.Bound =>
                $"✅ ผูกบัญชีสำเร็จ\n\nLINE นี้เชื่อมกับบัญชีลูกค้า คุณ{r.CustomerName} แล้ว{siteLine}\n\n"
                + "ต่อไปนี้ท่านจะได้รับแจ้งเตือนระบบโซลาร์เซลล์ทาง LINE นี้" + statusHint,
            LineBindOutcome.AlreadyBound =>
                $"✅ LINE นี้ผูกกับบัญชีลูกค้า คุณ{r.CustomerName} เรียบร้อยแล้ว{siteLine}\nไม่ต้องดำเนินการเพิ่มครับ" + statusHint,
            LineBindOutcome.Expired =>
                "⌛ รหัสนี้หมดอายุแล้ว\n\nกรุณาขอลิงก์ผูกบัญชีใหม่จากผู้ดูแลระบบ แล้วลองอีกครั้งครับ",
            LineBindOutcome.Used =>
                "รหัสนี้ถูกใช้ผูกบัญชีไปแล้ว\n\nหากต้องการให้ LINE นี้รับแจ้งเตือนด้วย "
                + "กรุณาขอลิงก์ใหม่จากผู้ดูแลระบบครับ",
            _ =>
                "ไม่พบรหัสผูกบัญชีนี้\n\nกรุณาตรวจสอบรหัส 8 หลักอีกครั้ง หรือขอลิงก์ใหม่จากผู้ดูแลระบบครับ",
        };
    }

    private async Task<string?> SiteNameAsync(int? installationId) =>
        installationId == null
            ? null
            : await _context.Installations.AsNoTracking()
                .Where(i => i.Id == installationId)
                .Select(i => i.SiteName)
                .FirstOrDefaultAsync();
}
