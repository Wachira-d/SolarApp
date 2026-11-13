using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SolarApp.Data;
using SolarApp.Models.Enums;
using SolarApp.Services.Notifications;
using SolarApp.Tests.TestSupport;
using Xunit;

namespace SolarApp.Tests.Services.Notifications;

/// <summary>A clock the tests can move — expiry is the whole point of a one-time code.</summary>
public sealed class FixedTime : TimeProvider
{
    public DateTimeOffset Now { get; set; }
    public FixedTime(DateTime utc) => Now = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    public override DateTimeOffset GetUtcNow() => Now;
}

public class LineBindingServiceTests
{
    private static readonly DateTime T0 = new(2026, 3, 1, 3, 0, 0, DateTimeKind.Utc);

    private static LineBindingService Create(ApplicationDbContext db, FixedTime clock) =>
        new(db, NullLogger<LineBindingService>.Instance, clock);

    // ── Code generation ──

    [Fact]
    public void GenerateCode_UsesUnambiguousAlphabet_AndMixesLettersAndDigits()
    {
        for (var i = 0; i < 500; i++)
        {
            var code = LineBindingService.GenerateCode();
            code.Should().HaveLength(8);
            code.Should().NotContainAny("0", "O", "1", "I", "L");
            code.All(ch => LineBindingService.Alphabet.Contains(ch)).Should().BeTrue();
            code.Any(char.IsDigit).Should().BeTrue();
            code.Any(char.IsLetter).Should().BeTrue();
        }
    }

    [Fact]
    public async Task CreateCode_IsUnique_AndExpiresInSevenDays()
    {
        using var db = TestDb.Create();
        var (customer, _) = TestDb.SeedSite(db);
        var svc = Create(db, new FixedTime(T0));

        var codes = new List<string>();
        for (var i = 0; i < 100; i++)
        {
            var row = await svc.CreateCodeAsync(customer.Id, null, "admin");
            row.ExpiresAt.Should().Be(T0.AddDays(7));
            row.UsedAt.Should().BeNull();
            codes.Add(row.Code);
        }
        codes.Should().OnlyHaveUniqueItems();
        (await db.LineBindingCodes.CountAsync()).Should().Be(100);
    }

    [Fact]
    public async Task Lookup_ReportsValid_ThenExpired_AfterSevenDays()
    {
        using var db = TestDb.Create();
        var (customer, site) = TestDb.SeedSite(db);
        var clock = new FixedTime(T0);
        var svc = Create(db, clock);
        var row = await svc.CreateCodeAsync(customer.Id, site.Id, null);

        var ok = await svc.LookupAsync(row.Code.ToLowerInvariant());
        ok.Status.Should().Be(LineCodeStatus.Valid);
        ok.CustomerFirstName.Should().Be("Test");
        ok.SiteName.Should().Be("Test Site");

        clock.Now = clock.Now.AddDays(7).AddMinutes(1);
        (await svc.LookupAsync(row.Code)).Status.Should().Be(LineCodeStatus.Expired);
        (await svc.BindAsync(row.Code, "Uexpired")).Outcome.Should().Be(LineBindOutcome.Expired);
        (await db.CustomerNotificationRecipients.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Lookup_UnknownOrMalformed_IsNotFound()
    {
        using var db = TestDb.Create();
        var svc = Create(db, new FixedTime(T0));
        (await svc.LookupAsync("ABCD2345")).Status.Should().Be(LineCodeStatus.NotFound);
        (await svc.LookupAsync("nope")).Status.Should().Be(LineCodeStatus.NotFound);
        (await svc.LookupAsync(null)).Status.Should().Be(LineCodeStatus.NotFound);
    }

    // ── Extraction from chat text ──

    [Theory]
    [InlineData("ผูกบัญชี ABCD2345", "ABCD2345")]
    [InlineData("ABCD2345", "ABCD2345")]
    [InlineData("  abcd2345  ", "ABCD2345")]
    [InlineData("ผูกบัญชีABCD2345", "ABCD2345")]
    [InlineData("ผูกบัญชี ABCD-2345", "ABCD2345")]
    [InlineData("ผูกบัญชี ABCD 2345 ครับ", "ABCD2345")]
    [InlineData("รหัส: XY7Z9QRS ขอบคุณครับ", "XY7Z9QRS")]
    public void ExtractCode_FindsCode(string text, string expected) =>
        LineBindingService.ExtractCode(text).Should().Be(expected);

    [Theory]
    [InlineData("สถานะ")]
    [InlineData("เดือนนี้")]
    [InlineData("ID")]
    [InlineData("MESSAGES")]          // all letters — a word, not a code
    [InlineData("12345678")]          // all digits — a phone fragment
    [InlineData("ABCD0123")]          // 0 and 1 are not in the alphabet
    [InlineData("ABCD23456")]         // nine characters
    [InlineData("Uabcdef0123456789abcdef0123456789")] // a LINE user id
    [InlineData("")]
    public void ExtractCode_IgnoresOrdinaryMessages(string text) =>
        LineBindingService.ExtractCode(text).Should().BeNull();

    // ── Binding ──

    [Fact]
    public async Task Bind_CreatesVerifiedRecipient_SetsCustomerLineId_AndMarksCodeUsed()
    {
        using var db = TestDb.Create();
        var (customer, site) = TestDb.SeedSite(db);
        var svc = Create(db, new FixedTime(T0));
        var row = await svc.CreateCodeAsync(customer.Id, site.Id, null);

        var result = await svc.BindAsync(row.Code, "U123",
            () => Task.FromResult<string?>("สมชาย"));

        result.Outcome.Should().Be(LineBindOutcome.Bound);
        result.CustomerName.Should().Be("Test Owner");
        result.CanQueryStatus.Should().BeTrue();

        var r = await db.CustomerNotificationRecipients.SingleAsync();
        r.CustomerId.Should().Be(customer.Id);
        r.InstallationId.Should().Be(site.Id);
        r.Channel.Should().Be(NotificationChannel.Line);
        r.Address.Should().Be("U123");
        r.DisplayName.Should().Be("สมชาย");
        r.VerifiedAt.Should().Be(T0);
        r.IsEnabled.Should().BeTrue();

        (await db.Customers.SingleAsync()).LineUserId.Should().Be("U123");
        var used = await db.LineBindingCodes.SingleAsync();
        used.UsedAt.Should().Be(T0);
        used.UsedByLineUserId.Should().Be("U123");

        LineBindingService.BuildReply(result).Should().Contain("Test Owner").And.Contain("Test Site");
    }

    [Fact]
    public async Task Bind_RepeatedBySameUser_IsIdempotent()
    {
        using var db = TestDb.Create();
        var (customer, _) = TestDb.SeedSite(db);
        var svc = Create(db, new FixedTime(T0));
        var row = await svc.CreateCodeAsync(customer.Id, null, null);

        (await svc.BindAsync(row.Code, "U123")).Outcome.Should().Be(LineBindOutcome.Bound);
        var lookups = 0;
        var again = await svc.BindAsync(row.Code, "U123", () => { lookups++; return Task.FromResult<string?>("x"); });

        again.Outcome.Should().Be(LineBindOutcome.AlreadyBound);
        again.CustomerName.Should().Be("Test Owner");
        lookups.Should().Be(0, "a replay must not call the LINE profile API");
        (await db.CustomerNotificationRecipients.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Bind_UsedCodeByAnotherUser_IsRefused()
    {
        using var db = TestDb.Create();
        var (customer, _) = TestDb.SeedSite(db);
        var svc = Create(db, new FixedTime(T0));
        var row = await svc.CreateCodeAsync(customer.Id, null, null);
        await svc.BindAsync(row.Code, "U123");

        var other = await svc.BindAsync(row.Code, "U999");

        other.Outcome.Should().Be(LineBindOutcome.Used);
        (await db.CustomerNotificationRecipients.CountAsync()).Should().Be(1);
        LineBindingService.BuildReply(other).Should().Contain("ถูกใช้");
    }

    [Fact]
    public async Task Bind_DoesNotOverwriteExistingCustomerLineId_AndReenablesRecipient()
    {
        using var db = TestDb.Create();
        var (customer, _) = TestDb.SeedSite(db);
        customer.LineUserId = "Uowner";
        db.CustomerNotificationRecipients.Add(new SolarApp.Models.Notifications.CustomerNotificationRecipient
        {
            CustomerId = customer.Id, Channel = NotificationChannel.Line, Address = "Uspouse", IsEnabled = false,
        });
        db.SaveChanges();
        var svc = Create(db, new FixedTime(T0));
        var row = await svc.CreateCodeAsync(customer.Id, null, null);

        var result = await svc.BindAsync(row.Code, "Uspouse", () => throw new HttpRequestException("profile down"));

        result.Outcome.Should().Be(LineBindOutcome.Bound, "a failing profile lookup must not block binding");
        result.CanQueryStatus.Should().BeFalse();
        (await db.Customers.SingleAsync()).LineUserId.Should().Be("Uowner");
        var r = await db.CustomerNotificationRecipients.SingleAsync();
        r.IsEnabled.Should().BeTrue();
        r.VerifiedAt.Should().Be(T0);
    }

    [Fact]
    public async Task Bind_UnknownCode_IsNotFound()
    {
        using var db = TestDb.Create();
        TestDb.SeedSite(db);
        var svc = Create(db, new FixedTime(T0));
        var result = await svc.BindAsync("ZZZZ2222", "U1");
        result.Outcome.Should().Be(LineBindOutcome.NotFound);
        LineBindingService.BuildReply(result).Should().Contain("ไม่พบ");
    }

    [Theory]
    [InlineData("ผูกบัญชี ABCD2345", true)]
    [InlineData("ABCD2345", false)]
    public void HasBindIntent_DetectsThaiKeyword(string text, bool expected) =>
        LineBindingService.HasBindIntent(text).Should().Be(expected);
}
