using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.Security;
using SolarApp.Tests.TestSupport;
using Xunit;

namespace SolarApp.Tests.Pages;

/// <summary>
/// The site edit form, posted the way a browser posts it. Regression for
/// "cleared the Station ID and username, saved, and they came back".
/// </summary>
public class InstallationEditCloudConnectionTests
{
    /// <summary>The validation messages on a re-rendered form, decoded — the useful part of a failed save.</summary>
    internal static string Errors(string html) =>
        string.Join(" | ", System.Text.RegularExpressions.Regex.Matches(html,
                "class=\"(?:field-validation-error|validation-summary-errors)[^\"]*\"[^>]*>(.*?)</(?:span|div)>",
                System.Text.RegularExpressions.RegexOptions.Singleline)
            .Select(m => System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(m.Groups[1].Value, "<[^>]+>", " ")).Trim()));

    /// <summary>Set EDIT_RENDER_DIR to keep the rendered pages for a visual check in a browser.</summary>
    private static void Dump(string name, string html)
    {
        if (Environment.GetEnvironmentVariable("EDIT_RENDER_DIR") is { Length: > 0 } dir)
            File.WriteAllText(Path.Combine(dir, name + ".html"), html);
    }

    private static ICredentialProtector Protector(StaffPageHost host) => host.Services.GetRequiredService<ICredentialProtector>();

    private static async Task<int> SeedConnectedSiteAsync(StaffPageHost host)
    {
        await using var db = host.NewDb();
        var (_, site) = TestDb.SeedSite(db, InverterBrand.GoodWe, hasBattery: false);
        var p = Protector(host);
        site.TelemetrySource = TelemetrySource.Cloud;
        site.ConnectionMethod = ConnectionMethod.UserPassword;
        site.PlantId = "a8c26f19-6839-428d-bba9-b098b549666c";
        site.CloudUsername = p.Protect("owner@example.com");
        site.CloudPassword = p.Protect("secret-pass");
        site.CloudApiKey = p.Protect("key-123");
        site.CloudApiSecret = p.Protect("sec-456");
        site.CloudApiBaseUrl = "https://eu.semsportal.com";
        site.NumberOfPanels = 20;
        site.PanelWattage = 550;
        site.WarrantyYears = 10;
        site.InverterSizeKw = 10;
        await db.SaveChangesAsync();
        return site.Id;
    }

    [Fact]
    public async Task Clearing_station_id_username_and_endpoint_and_saving_clears_them()
    {
        await using var host = await StaffPageHost.StartAsync(Guid.NewGuid().ToString());
        var id = await SeedConnectedSiteAsync(host);

        var form = await host.GetFormAsync($"/Installations/Edit/{id}");
        form.Get("Input.PlantId").Should().Be("a8c26f19-6839-428d-bba9-b098b549666c");
        form.Set("Input.PlantId", "").Set("Input.CloudUsername", "").Set("Input.CloudApiBaseUrl", "");

        var res = await host.PostAsync(form);

        res.StatusCode.Should().Be(HttpStatusCode.Redirect, Errors(await res.Content.ReadAsStringAsync()));
        await using var db = host.NewDb();
        var site = await db.Installations.SingleAsync(i => i.Id == id);
        site.PlantId.Should().BeNull();
        site.CloudUsername.Should().BeNull();
        site.CloudApiBaseUrl.Should().BeNull();
    }

    [Fact]
    public async Task Saving_without_touching_the_connection_keeps_every_credential()
    {
        await using var host = await StaffPageHost.StartAsync(Guid.NewGuid().ToString());
        var id = await SeedConnectedSiteAsync(host);

        var form = await host.GetFormAsync($"/Installations/Edit/{id}");
        var res = await host.PostAsync(form);

        res.StatusCode.Should().Be(HttpStatusCode.Redirect, Errors(await res.Content.ReadAsStringAsync()));
        await using var db = host.NewDb();
        var site = await db.Installations.SingleAsync(i => i.Id == id);
        var p = Protector(host);
        site.PlantId.Should().Be("a8c26f19-6839-428d-bba9-b098b549666c");
        p.Unprotect(site.CloudUsername).Should().Be("owner@example.com");
        p.Unprotect(site.CloudPassword).Should().Be("secret-pass");
        p.Unprotect(site.CloudApiKey).Should().Be("key-123");
        p.Unprotect(site.CloudApiSecret).Should().Be("sec-456");
        site.CloudApiBaseUrl.Should().Be("https://eu.semsportal.com");
    }

    [Fact]
    public async Task Ticking_clear_password_removes_only_the_password()
    {
        await using var host = await StaffPageHost.StartAsync(Guid.NewGuid().ToString());
        var id = await SeedConnectedSiteAsync(host);

        var form = await host.GetFormAsync($"/Installations/Edit/{id}");
        form.Fields.Should().ContainKey("Input.ClearCloudPassword");   // the tick is on the form
        form.Set("Input.ClearCloudPassword", "true");
        var res = await host.PostAsync(form);

        res.StatusCode.Should().Be(HttpStatusCode.Redirect, Errors(await res.Content.ReadAsStringAsync()));
        await using var db = host.NewDb();
        var site = await db.Installations.SingleAsync(i => i.Id == id);
        site.CloudPassword.Should().BeNull();
        Protector(host).Unprotect(site.CloudUsername).Should().Be("owner@example.com");
        site.CloudApiKey.Should().NotBeNull();
        site.PlantId.Should().NotBeNull();
    }

    [Fact]
    public async Task Disconnect_wipes_the_connection_stops_polling_and_closes_alerts_quietly()
    {
        await using var host = await StaffPageHost.StartAsync(Guid.NewGuid().ToString());
        var id = await SeedConnectedSiteAsync(host);
        await using (var db0 = host.NewDb())
        {
            var s0 = await db0.Installations.SingleAsync(i => i.Id == id);
            s0.Status = InstallationStatus.Offline;
            db0.SystemAlerts.Add(new SystemAlert { InstallationId = id, AlertType = AlertType.Offline, Severity = AlertSeverity.High, Title = "ออฟไลน์", Message = "x" });
            db0.SystemAlerts.Add(new SystemAlert { InstallationId = id, AlertType = AlertType.MaintenanceDue, Severity = AlertSeverity.Low, Title = "ล้างแผง", Message = "x" });
            await db0.SaveChangesAsync();
        }

        var form = await host.GetFormAsync($"/Installations/Edit/{id}");
        form.Html.Should().Contain($"handler=CloudDisconnect").And.Contain("handler=CloudPause");
        var res = await host.PostAsync(form, "CloudDisconnect");

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Contain("#cloud");
        await using var db = host.NewDb();
        var site = await db.Installations.SingleAsync(i => i.Id == id);
        site.CloudApiEnabled.Should().BeFalse();
        new[] { site.PlantId, site.CloudUsername, site.CloudPassword, site.CloudApiKey, site.CloudApiSecret, site.CloudApiBaseUrl }
            .Should().OnlyContain(v => v == null);
        site.Status.Should().Be(InstallationStatus.Active);
        var alerts = await db.SystemAlerts.Where(a => a.InstallationId == id).ToListAsync();
        alerts.Single(a => a.AlertType == AlertType.Offline).Should().Match<SystemAlert>(a => a.ResolvedAt != null && a.NotificationSuppressed);
        alerts.Single(a => a.AlertType == AlertType.MaintenanceDue).ResolvedAt.Should().BeNull();   // not a connectivity alert

        // The page now offers to reconnect and no longer offers to pause or disconnect.
        var after = await host.GetFormAsync($"/Installations/Edit/{id}");
        Dump("edit-connected", form.Html);
        Dump("edit-disconnected", after.Html);
        after.Html.Should().Contain("handler=CloudResume").And.NotContain("handler=CloudDisconnect").And.NotContain("handler=CloudPause");
    }

    [Fact]
    public async Task Pause_keeps_settings_and_resume_switches_back_on()
    {
        await using var host = await StaffPageHost.StartAsync(Guid.NewGuid().ToString());
        var id = await SeedConnectedSiteAsync(host);

        (await host.PostAsync(await host.GetFormAsync($"/Installations/Edit/{id}"), "CloudPause")).StatusCode.Should().Be(HttpStatusCode.Redirect);
        await using (var db = host.NewDb())
        {
            var site = await db.Installations.SingleAsync(i => i.Id == id);
            site.CloudApiEnabled.Should().BeFalse();
            site.PlantId.Should().NotBeNull();
            Protector(host).Unprotect(site.CloudPassword).Should().Be("secret-pass");
        }

        // A normal save while paused keeps it paused.
        (await host.PostAsync(await host.GetFormAsync($"/Installations/Edit/{id}"))).StatusCode.Should().Be(HttpStatusCode.Redirect);
        await using (var db = host.NewDb())
            (await db.Installations.SingleAsync(i => i.Id == id)).CloudApiEnabled.Should().BeFalse();

        (await host.PostAsync(await host.GetFormAsync($"/Installations/Edit/{id}"), "CloudResume")).StatusCode.Should().Be(HttpStatusCode.Redirect);
        await using (var db = host.NewDb())
            (await db.Installations.SingleAsync(i => i.Id == id)).CloudApiEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task A_refused_save_shows_why_at_the_top_in_thai()
    {
        await using var host = await StaffPageHost.StartAsync(Guid.NewGuid().ToString());
        var id = await SeedConnectedSiteAsync(host);

        var form = (await host.GetFormAsync($"/Installations/Edit/{id}")).Set("Input.NumberOfPanels", "0");
        var res = await host.PostAsync(form);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await res.Content.ReadAsStringAsync();
        Dump("edit-refused", raw);
        var html = System.Net.WebUtility.HtmlDecode(raw);
        html.Should().Contain("id=\"saveErrors\"").And.Contain("จำนวนแผง ต้องอยู่ระหว่าง 1–10000");
    }
}

public class InstallationCloudConnectionTests
{
    private static Installation Site(TelemetrySource src, bool enabled, string? plant) =>
        new() { TelemetrySource = src, CloudApiEnabled = enabled, PlantId = plant, CloudUsername = "enc:u" };

    [Theory]
    [InlineData(TelemetrySource.Cloud, true, "P1", true)]
    [InlineData(TelemetrySource.Cloud, false, "P1", false)]   // paused
    [InlineData(TelemetrySource.Cloud, true, null, false)]    // disconnected / never set
    [InlineData(TelemetrySource.Cloud, true, "", false)]
    [InlineData(TelemetrySource.LocalGateway, false, null, true)]   // the gateway still feeds it
    [InlineData(TelemetrySource.Hybrid, false, "P1", true)]
    public void Live_data_source_rule(TelemetrySource src, bool enabled, string? plant, bool live)
    {
        var sites = new[] { Site(src, enabled, plant) }.AsQueryable();
        sites.Any(SolarApp.Services.InstallationCloudConnection.HasLiveDataSource).Should().Be(live);
        sites.Any(SolarApp.Services.InstallationCloudConnection.HasNoLiveDataSource).Should().Be(!live);
    }

    [Fact]
    public void States_follow_switch_and_settings()
    {
        var s = Site(TelemetrySource.Cloud, true, "P1");
        SolarApp.Services.InstallationCloudConnection.State(s).Should().Be(SolarApp.Services.CloudConnectionState.Connected);
        SolarApp.Services.InstallationCloudConnection.Pause(s);
        SolarApp.Services.InstallationCloudConnection.State(s).Should().Be(SolarApp.Services.CloudConnectionState.Paused);
        SolarApp.Services.InstallationCloudConnection.Disconnect(s);
        SolarApp.Services.InstallationCloudConnection.State(s).Should().Be(SolarApp.Services.CloudConnectionState.Off);
        SolarApp.Services.InstallationCloudConnection.Resume(s);
        SolarApp.Services.InstallationCloudConnection.State(s).Should().Be(SolarApp.Services.CloudConnectionState.Incomplete);
    }
}
