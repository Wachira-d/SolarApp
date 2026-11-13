using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Models.Notifications;
using SolarApp.Services;
using SolarApp.Services.LineApi;
using SolarApp.Services.Notifications;
using SolarApp.Tests.TestSupport;
using Xunit;

namespace SolarApp.Tests.Services.Notifications;

public class CustomerNotificationServiceTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 5, 0, 0, DateTimeKind.Utc); // 12:00 in Bangkok

    private readonly Mock<ILineMessagingService> _line = new();
    private readonly Mock<ICustomerEmailSender> _email = new();

    public CustomerNotificationServiceTests()
    {
        _email.Setup(e => e.IsConfiguredAsync()).ReturnsAsync(true);
    }

    private CustomerNotificationService Create(ApplicationDbContext db) =>
        new(db, _line.Object, _email.Object, NullLogger<CustomerNotificationService>.Instance, null, new FixedTime(Now));

    private static SystemAlert OfflineAlert(Installation site, AlertSeverity sev = AlertSeverity.High) => new()
    {
        Id = 42, InstallationId = site.Id, AlertType = AlertType.Offline, Severity = sev,
        Title = "Inverter offline", Message = "no data", OccurredAt = Now.AddMinutes(-20),
    };

    private static CustomerNotificationRecipient AddRecipient(ApplicationDbContext db, Customer c,
        NotificationChannel ch, string address, int? siteId = null, bool enabled = true)
    {
        var r = new CustomerNotificationRecipient
        {
            CustomerId = c.Id, Channel = ch, Address = address, InstallationId = siteId, IsEnabled = enabled,
        };
        db.CustomerNotificationRecipients.Add(r);
        db.SaveChanges();
        return r;
    }

    [Fact]
    public void Services_ResolveFromContainer_WithOptionalClockAndConfig()
    {
        // Mirrors the Startup registrations: the optional TimeProvider is not
        // registered and must fall back to its default rather than fail activation.
        var services = new ServiceCollection();
        services.AddScoped(_ => TestDb.Create());
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(_line.Object);
        services.AddSingleton(new Mock<SolarApp.Services.Security.ICredentialProtector>().Object);
        services.AddScoped<ICustomerEmailSender, SmtpCustomerEmailSender>();
        services.AddScoped<ILineBindingService, LineBindingService>();
        services.AddScoped<ICustomerNotificationService, CustomerNotificationService>();

        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICustomerNotificationService>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<ILineBindingService>().Should().NotBeNull();
    }

    // ── Preference resolution ──

    [Fact]
    public void Resolve_WithoutRow_FollowsSiteFlags_OnBothChannels()
    {
        var site = new Installation { NotifyOnGridLoss = true, NotifyOnLowProduction = false, NotifyOnRecovery = true };

        CustomerNotificationEvents.Resolve(NotificationEventType.GridLossOffline, null, site).Should().Be((true, true));
        CustomerNotificationEvents.Resolve(NotificationEventType.LowProduction, null, site).Should().Be((false, false));
        CustomerNotificationEvents.Resolve(NotificationEventType.Recovery, null, site).Should().Be((true, true));
        CustomerNotificationEvents.Resolve(NotificationEventType.PredictiveDiagnostic, null, site).Should().Be((false, false));
    }

    [Fact]
    public void Resolve_WithRow_OverridesSiteFlags()
    {
        var site = new Installation { NotifyOnGridLoss = true };
        var pref = new CustomerNotificationPreference { LineEnabled = false, EmailEnabled = true };
        CustomerNotificationEvents.Resolve(NotificationEventType.GridLossOffline, pref, site).Should().Be((false, true));
    }

    [Fact]
    public void CustomerDefault_IsOnWhenAnySiteHasItOn()
    {
        var sites = new[] { new Installation { NotifyOnCleaningDue = false }, new Installation { NotifyOnCleaningDue = true } };
        CustomerNotificationEvents.CustomerDefault(NotificationEventType.CleaningDue, sites).Should().BeTrue();
        CustomerNotificationEvents.CustomerDefault(NotificationEventType.WarrantyExpiry, sites).Should().BeFalse();
    }

    [Fact]
    public void EventFor_MapsApiFailureSeparately_AndNeverSendsContracts()
    {
        CustomerNotificationEvents.EventFor(new SystemAlert { AlertType = AlertType.Error, Title = AlertTitles.ApiFailure + "x" })
            .Should().Be(NotificationEventType.ApiFailure);
        CustomerNotificationEvents.EventFor(new SystemAlert { AlertType = AlertType.WarrantyExpiring, Title = AlertTitles.ContractExpiring + "x" })
            .Should().BeNull();
        CustomerNotificationEvents.EventFor(new SystemAlert { AlertType = AlertType.Offline, Title = "Station Faulty: x" })
            .Should().Be(NotificationEventType.GridLossOffline);
        CustomerNotificationEvents.EventFor(new SystemAlert { AlertType = AlertType.Warning, Title = "x" })
            .Should().BeNull();
    }

    // ── Delivery ──

    [Fact]
    public async Task Deliver_SendsToEnabledRecipients_OnAllowedChannels_AndLogs()
    {
        using var db = TestDb.Create();
        var (c, site) = TestDb.SeedSite(db);
        var line = AddRecipient(db, c, NotificationChannel.Line, "Uspouse");
        var mail = AddRecipient(db, c, NotificationChannel.Email, "owner@example.com");
        AddRecipient(db, c, NotificationChannel.Line, "Udisabled", enabled: false);

        var sent = await Create(db).DeliverAlertAsync(OfflineAlert(site), site,
            NotificationEventType.GridLossOffline, "ℹ️ ระบบขาดการเชื่อมต่อ\n\nรายละเอียด", new HashSet<string>());

        sent.Should().Be(2);
        _line.Verify(l => l.SendMessageAsync("Uspouse", It.IsAny<string>()), Times.Once);
        _line.Verify(l => l.SendMessageAsync("Udisabled", It.IsAny<string>()), Times.Never);
        _email.Verify(e => e.SendAsync("owner@example.com", It.Is<string>(s => s.Contains("Test Site")),
            It.Is<string>(h => h.Contains("ระบบขาดการเชื่อมต่อ")), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        var logs = await db.NotificationLogs.ToListAsync();
        logs.Should().HaveCount(2);
        logs.Should().OnlyContain(l => l.Status == NotificationStatus.Sent
                                       && l.RelatedEntityType == CustomerNotificationService.LogEntityType
                                       && l.RelatedEntityId == "42");
        (await db.CustomerNotificationRecipients.FindAsync(line.Id))!.LastSentAt.Should().Be(Now);
        (await db.CustomerNotificationRecipients.FindAsync(mail.Id))!.LastSentAt.Should().Be(Now);
    }

    [Fact]
    public async Task Deliver_RespectsPerChannelPreference()
    {
        using var db = TestDb.Create();
        var (c, site) = TestDb.SeedSite(db);
        AddRecipient(db, c, NotificationChannel.Line, "U1");
        AddRecipient(db, c, NotificationChannel.Email, "a@example.com");
        db.CustomerNotificationPreferences.Add(new CustomerNotificationPreference
        {
            CustomerId = c.Id, EventType = NotificationEventType.GridLossOffline, LineEnabled = false, EmailEnabled = true,
        });
        db.SaveChanges();

        var sent = await Create(db).DeliverAlertAsync(OfflineAlert(site), site,
            NotificationEventType.GridLossOffline, "msg", Array.Empty<string>());

        sent.Should().Be(1);
        _line.Verify(l => l.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _email.Verify(e => e.SendAsync("a@example.com", It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Deliver_DefaultsOff_WhenSiteFlagOff()
    {
        using var db = TestDb.Create();
        var (c, site) = TestDb.SeedSite(db);
        site.NotifyOnLowProduction = false;
        db.SaveChanges();
        AddRecipient(db, c, NotificationChannel.Line, "U1");

        var alert = OfflineAlert(site);
        alert.AlertType = AlertType.LowPerformance;
        var sent = await Create(db).DeliverAlertAsync(alert, site, NotificationEventType.LowProduction, "msg", Array.Empty<string>());

        sent.Should().Be(0);
        _line.Verify(l => l.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Deliver_HonoursSiteScope()
    {
        using var db = TestDb.Create();
        var (c, site) = TestDb.SeedSite(db);
        var other = new Installation { SiteName = "Other", SiteCode = "OT", Address = "x", CustomerId = c.Id };
        db.Installations.Add(other);
        db.SaveChanges();
        AddRecipient(db, c, NotificationChannel.Line, "Uthis", siteId: site.Id);
        AddRecipient(db, c, NotificationChannel.Line, "Uother", siteId: other.Id);
        AddRecipient(db, c, NotificationChannel.Line, "Uall");

        await Create(db).DeliverAlertAsync(OfflineAlert(site), site, NotificationEventType.GridLossOffline, "m",
            Array.Empty<string>());

        _line.Verify(l => l.SendMessageAsync("Uthis", It.IsAny<string>()), Times.Once);
        _line.Verify(l => l.SendMessageAsync("Uall", It.IsAny<string>()), Times.Once);
        _line.Verify(l => l.SendMessageAsync("Uother", It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Deliver_SkipsAlreadyReachedLineIds_AndReportsWhomItReached()
    {
        using var db = TestDb.Create();
        var (c, site) = TestDb.SeedSite(db);
        AddRecipient(db, c, NotificationChannel.Line, "Uowner");
        AddRecipient(db, c, NotificationChannel.Line, "Uspouse");
        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "UOWNER" };

        var sent = await Create(db).DeliverAlertAsync(OfflineAlert(site), site,
            NotificationEventType.GridLossOffline, "m", reached);

        sent.Should().Be(1);
        _line.Verify(l => l.SendMessageAsync("Uowner", It.IsAny<string>()), Times.Never);
        reached.Should().Contain("Uspouse", "later fan-out steps must skip it");
    }

    [Fact]
    public async Task Deliver_IsIdempotentPerAlert()
    {
        using var db = TestDb.Create();
        var (c, site) = TestDb.SeedSite(db);
        AddRecipient(db, c, NotificationChannel.Line, "U1");
        AddRecipient(db, c, NotificationChannel.Line, "U1", siteId: site.Id); // same person twice
        var svc = Create(db);

        (await svc.DeliverAlertAsync(OfflineAlert(site), site, NotificationEventType.GridLossOffline, "m", new HashSet<string>()))
            .Should().Be(1);
        (await svc.DeliverAlertAsync(OfflineAlert(site), site, NotificationEventType.GridLossOffline, "m", new HashSet<string>()))
            .Should().Be(0);
        _line.Verify(l => l.SendMessageAsync("U1", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Deliver_InQuietHours_HoldsNonCritical_ButSendsCritical()
    {
        using var db = TestDb.Create();
        var (c, site) = TestDb.SeedSite(db);
        var local = TimeOnly.FromDateTime(SiteTime.ToSiteTime(Now));
        site.QuietHoursStart = local.AddHours(-1);
        site.QuietHoursEnd = local.AddHours(1);
        db.SaveChanges();
        AddRecipient(db, c, NotificationChannel.Line, "U1");
        var svc = Create(db);

        (await svc.DeliverAlertAsync(OfflineAlert(site, AlertSeverity.High), site,
            NotificationEventType.GridLossOffline, "m", new HashSet<string>())).Should().Be(0);

        var critical = OfflineAlert(site, AlertSeverity.Critical);
        critical.Id = 43;
        (await svc.DeliverAlertAsync(critical, site, NotificationEventType.GridLossOffline, "m", new HashSet<string>()))
            .Should().Be(1);
    }

    [Fact]
    public async Task Deliver_Recovery_IgnoresQuietHours_ButNeedsDownEventAllowed()
    {
        using var db = TestDb.Create();
        var (c, site) = TestDb.SeedSite(db);
        var local = TimeOnly.FromDateTime(SiteTime.ToSiteTime(Now));
        site.QuietHoursStart = local.AddHours(-1);
        site.QuietHoursEnd = local.AddHours(1);
        db.SaveChanges();
        AddRecipient(db, c, NotificationChannel.Line, "U1");
        var alert = OfflineAlert(site);
        alert.ResolvedAt = Now;
        var svc = Create(db);

        (await svc.DeliverAlertAsync(alert, site, NotificationEventType.Recovery, "✅ กลับมาแล้ว", new HashSet<string>()))
            .Should().Be(1, "the all-clear is never held back by quiet hours");

        // Same customer, down-alerts switched off: no recovery for a problem they never heard about.
        db.CustomerNotificationPreferences.Add(new CustomerNotificationPreference
        {
            CustomerId = c.Id, EventType = NotificationEventType.GridLossOffline, LineEnabled = false, EmailEnabled = false,
        });
        db.SaveChanges();
        alert.Id = 77;
        (await svc.DeliverAlertAsync(alert, site, NotificationEventType.Recovery, "✅", new HashSet<string>()))
            .Should().Be(0);
    }

    [Fact]
    public async Task Deliver_SkipsEmail_WhenSmtpNotConfigured()
    {
        using var db = TestDb.Create();
        var (c, site) = TestDb.SeedSite(db);
        AddRecipient(db, c, NotificationChannel.Email, "a@example.com");
        _email.Setup(e => e.IsConfiguredAsync()).ReturnsAsync(false);

        var sent = await Create(db).DeliverAlertAsync(OfflineAlert(site), site,
            NotificationEventType.GridLossOffline, "m", new HashSet<string>());

        sent.Should().Be(0);
        _email.Verify(e => e.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        (await db.NotificationLogs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Deliver_LogsFailedEmail()
    {
        using var db = TestDb.Create();
        var (c, site) = TestDb.SeedSite(db);
        AddRecipient(db, c, NotificationChannel.Email, "a@example.com");
        _email.Setup(e => e.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("535 auth failed"));

        var sent = await Create(db).DeliverAlertAsync(OfflineAlert(site), site,
            NotificationEventType.GridLossOffline, "m", new HashSet<string>());

        sent.Should().Be(0);
        var log = await db.NotificationLogs.SingleAsync();
        log.Status.Should().Be(NotificationStatus.Failed);
        log.Channel.Should().Be(NotificationChannel.Email);
        log.ErrorMessage.Should().Contain("535");
    }
}
