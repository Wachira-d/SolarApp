using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using SolarApp.Models.Enums;
using SolarApp.Models.Telemetry;
using SolarApp.Services.Telemetry;
using SolarApp.Tests.TestSupport;
using Xunit;

namespace SolarApp.Tests.Services.Telemetry;

public class TelemetryKeyTests
{
    [Theory]
    [InlineData("pv.power_w", TelemetryDeviceKind.Pv, 0, 0, TelemetryQuantity.Power, false)]
    [InlineData("pv.string3.power_w", TelemetryDeviceKind.PvString, 3, 0, TelemetryQuantity.Power, false)]
    [InlineData("pv.energy_total_kwh", TelemetryDeviceKind.Pv, 0, 0, TelemetryQuantity.Energy, true)]
    [InlineData("pv.energy_today_kwh", TelemetryDeviceKind.Pv, 0, 0, TelemetryQuantity.Energy, false)]
    [InlineData("grid.power_w", TelemetryDeviceKind.Grid, 0, 0, TelemetryQuantity.Power, false)]
    [InlineData("grid.l2.voltage_v", TelemetryDeviceKind.Grid, 2, 0, TelemetryQuantity.Voltage, false)]
    [InlineData("grid.status", TelemetryDeviceKind.Grid, 0, 0, TelemetryQuantity.GridPresent, false)]
    [InlineData("grid.import_total_kwh", TelemetryDeviceKind.Grid, 0, 0, TelemetryQuantity.Energy, true)]
    [InlineData("battery.soc_pct", TelemetryDeviceKind.Battery, 0, 0, TelemetryQuantity.Soc, false)]
    [InlineData("battery.pack2.cell_temp_max_c", TelemetryDeviceKind.BatteryPack, 2, 0, TelemetryQuantity.CellTempMax, false)]
    [InlineData("battery.pack1.cell_v_min", TelemetryDeviceKind.BatteryPack, 1, 0, TelemetryQuantity.CellVoltageMin, false)]
    [InlineData("battery.pack1.cell7.voltage_v", TelemetryDeviceKind.BatteryCell, 1, 7, TelemetryQuantity.Voltage, false)]
    [InlineData("inverter2.temp_c", TelemetryDeviceKind.Inverter, 2, 0, TelemetryQuantity.Temperature, false)]
    [InlineData("inverter1.status_code", TelemetryDeviceKind.Inverter, 1, 0, TelemetryQuantity.Status, false)]
    [InlineData("env.lux", TelemetryDeviceKind.Environment, 0, 0, TelemetryQuantity.Illuminance, false)]
    [InlineData("ha.sensor_foo", TelemetryDeviceKind.Other, 0, 0, TelemetryQuantity.Other, false)]
    public void Parse_reads_device_index_quantity(string key, TelemetryDeviceKind kind, int idx, int sub,
        TelemetryQuantity q, bool cumulative)
    {
        var p = TelemetryKey.Parse(key);
        p.Kind.Should().Be(kind);
        p.DeviceIndex.Should().Be(idx);
        p.SubIndex.Should().Be(sub);
        p.Quantity.Should().Be(q);
        p.IsCumulative.Should().Be(cumulative);
    }

    [Theory]
    [InlineData("pv.power_w", true)]
    [InlineData("PV.power_w", false)]
    [InlineData("pv..power", false)]
    [InlineData("a.b.c.d.e.f.g", false)]
    [InlineData("", false)]
    [InlineData("grid.power-w", false)]
    public void IsValid(string key, bool valid) => TelemetryKey.IsValid(key).Should().Be(valid);

    [Fact]
    public void Unmapped_entity_becomes_ha_key()
    {
        TelemetryKey.ForUnmappedEntity("sensor.Battery SOC").Should().Be("ha.sensor_battery_soc");
        TelemetryKey.LooksLikeEntityId("sensor.battery_soc").Should().BeTrue();
        TelemetryKey.LooksLikeEntityId("battery.soc_pct").Should().BeFalse();
    }
}

public class TelemetryTimestampTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Iso_with_offset_is_converted_to_utc_whole_seconds()
    {
        TelemetryIngestService.TryParseTimestamp(J("\"2026-11-13T12:10:05.678+07:00\""), out var utc, out _).Should().BeTrue();
        utc.Should().Be(new DateTime(2026, 11, 13, 5, 10, 5, DateTimeKind.Utc));
    }

    [Fact]
    public void Iso_without_offset_is_refused()
    {
        TelemetryIngestService.TryParseTimestamp(J("\"2026-11-13T12:10:05\""), out _, out var reason).Should().BeFalse();
        reason.Should().Be("ts_no_offset");
    }

    [Theory]
    [InlineData("1763010605")]        // seconds
    [InlineData("1763010605000")]     // milliseconds
    public void Unix_seconds_and_millis(string json)
    {
        TelemetryIngestService.TryParseTimestamp(J(json), out var utc, out _).Should().BeTrue();
        utc.Should().Be(DateTime.UnixEpoch.AddSeconds(1763010605));
    }

    [Theory]
    [InlineData("\"on\"", 1)]
    [InlineData("\"off\"", 0)]
    [InlineData("true", 1)]
    [InlineData("\"12.5\"", 12.5)]
    public void Values_accept_ha_states(string json, double expected)
    {
        TelemetryIngestService.TryParseValue(J(json), out var v).Should().BeTrue();
        v.Should().Be(expected);
    }

    [Theory]
    [InlineData("\"unavailable\"")]
    [InlineData("null")]
    [InlineData("\"NaN\"")]
    public void Unusable_values_are_rejected(string json) =>
        TelemetryIngestService.TryParseValue(J(json), out _).Should().BeFalse();
}

public class TelemetryIngestServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 5, 0, 0, DateTimeKind.Utc);

    private static (TelemetryIngestService Svc, InMemoryTelemetryStore Store, SolarApp.Data.ApplicationDbContext Db,
        TelemetryGateway Gw) Setup()
    {
        var db = TestDb.Create();
        var (_, site) = TestDb.SeedSite(db);
        var gw = new TelemetryGateway { InstallationId = site.Id, Name = "HA", TokenHash = "h", TokenPrefix = "sag_x" };
        db.TelemetryGateways.Add(gw);
        db.SaveChanges();
        gw = db.TelemetryGateways.Include(g => g.Installation).First();
        var store = new InMemoryTelemetryStore();
        return (new TelemetryIngestService(db, store, NullLogger<TelemetryIngestService>.Instance), store, db, gw);
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task Snapshot_form_creates_channels_stores_samples_and_marks_site_online()
    {
        var (svc, store, db, gw) = Setup();
        var r = await svc.IngestTelemetryAsync(gw, J("""
            { "ts": "2026-11-13T04:59:00Z",
              "values": { "pv.power_w": 4200, "battery.soc_pct": 87, "grid.power_w": -500, "sensor.unknown_thing": 3 } }
            """), Now, "1.2.3.4");

        r.Accepted.Should().Be(4);
        r.Rejected.Should().Be(0);
        r.NewChannels.Should().Be(4);
        r.UnmappedEntities.Should().Contain("sensor.unknown_thing");
        store.AllRaw.Should().HaveCount(4);

        var channels = db.TelemetryChannels.ToList();
        channels.Should().Contain(c => c.Key == "ha.sensor_unknown_thing" && c.Kind == TelemetryDeviceKind.Other);
        channels.Single(c => c.Key == "battery.soc_pct").LastValue.Should().Be(87);

        var site = db.Installations.First();
        site.LastOnlineAt.Should().Be(Now);
        db.EnergyData.Should().Contain(e => e.Granularity == DataGranularity.Realtime && e.PowerGenerationKw == 4.2m
                                            && e.GridExportKwh == 0.5m && e.BatterySocPercent == 87);
        db.TelemetryGateways.First().SamplesAccepted.Should().Be(4);
    }

    [Fact]
    public async Task Entity_map_applies_scale_and_invert()
    {
        var (svc, store, db, gw) = Setup();
        db.TelemetryEntityMaps.Add(new TelemetryEntityMap
        {
            InstallationId = gw.InstallationId, EntityId = "sensor.batt_power_kw", Key = "battery.power_w",
            Scale = 1000, Invert = true,
        });
        db.SaveChanges();

        await svc.IngestTelemetryAsync(gw, J("""
            { "samples": [ { "entity_id": "sensor.batt_power_kw", "ts": "2026-11-13T04:59:00Z", "value": 1.5 } ] }
            """), Now, null);

        store.AllRaw.Single().Value.Should().Be(-1500);
        db.TelemetryChannels.Single().Key.Should().Be("battery.power_w");
    }

    [Fact]
    public async Task Replays_are_deduplicated_against_stored_rows()
    {
        var (svc, store, _, gw) = Setup();
        var body = """
            { "samples": [
                { "key": "pv.power_w", "ts": "2026-11-13T04:50:00Z", "value": 100 },
                { "key": "pv.power_w", "ts": "2026-11-13T04:51:00Z", "value": 110 } ] }
            """;
        (await svc.IngestTelemetryAsync(gw, J(body), Now, null)).Accepted.Should().Be(2);

        // Same samples again plus an older one that was never sent.
        var replay = await svc.IngestTelemetryAsync(gw, J("""
            { "samples": [
                { "key": "pv.power_w", "ts": "2026-11-13T04:49:00Z", "value": 90 },
                { "key": "pv.power_w", "ts": "2026-11-13T04:50:00Z", "value": 100 },
                { "key": "pv.power_w", "ts": "2026-11-13T04:51:00Z", "value": 110 } ] }
            """), Now, null);
        replay.Accepted.Should().Be(1);
        replay.Duplicates.Should().Be(2);
        store.AllRaw.Should().HaveCount(3);
    }

    [Fact]
    public async Task Bad_samples_are_counted_with_reasons_and_the_rest_accepted()
    {
        var (svc, store, _, gw) = Setup();
        var r = await svc.IngestTelemetryAsync(gw, J("""
            { "samples": [
                { "key": "pv.power_w", "ts": "2026-11-13T04:59:00Z", "value": 1 },
                { "key": "pv.power_w", "ts": "2026-11-13T04:59:30", "value": 1 },
                { "key": "pv.power_w", "ts": "2025-01-01T00:00:00Z", "value": 1 },
                { "key": "pv.power_w", "ts": "2026-11-13T04:58:00Z", "value": "unavailable" },
                { "key": "Bad Key", "ts": "2026-11-13T04:58:00Z", "value": 1 } ] }
            """), Now, null);

        r.Accepted.Should().Be(1);
        r.Rejected.Should().Be(4);
        r.RejectReasons.Should().ContainKeys("ts_no_offset", "ts_out_of_window", "bad_value", "bad_key");
        store.AllRaw.Should().HaveCount(1);
    }

    [Fact]
    public async Task Status_code_changes_become_device_events()
    {
        var (svc, _, db, gw) = Setup();
        await svc.IngestTelemetryAsync(gw, J("""
            { "samples": [
                { "key": "inverter1.status_code", "ts": "2026-11-13T04:50:00Z", "value": 2 },
                { "key": "inverter1.status_code", "ts": "2026-11-13T04:51:00Z", "value": 2 },
                { "key": "inverter1.status_code", "ts": "2026-11-13T04:52:00Z", "value": 3 } ] }
            """), Now, null);

        var events = db.DeviceEvents.OrderBy(e => e.TsUtc).ToList();
        events.Should().HaveCount(2, "the first reading and the change from 2 to 3; the repeat of 2 is not an event");
        events.Select(e => e.Code).Should().Equal(2, 3);
        events.All(e => e.Source == "inverter1").Should().BeTrue();
    }

    [Fact]
    public async Task Late_data_behind_the_rollup_watermark_marks_a_dirty_range()
    {
        var (svc, _, db, gw) = Setup();
        db.TelemetryRollupStates.Add(new TelemetryRollupState
        {
            InstallationId = gw.InstallationId, WatermarkUtc = new DateTime(2026, 11, 13, 4, 0, 0, DateTimeKind.Utc),
        });
        db.SaveChanges();

        await svc.IngestTelemetryAsync(gw, J("""
            { "samples": [ { "key": "pv.power_w", "ts": "2026-11-13T03:10:00Z", "value": 5 } ] }
            """), Now, null);

        var dirty = db.TelemetryDirtyRanges.Single();
        dirty.FromUtc.Should().Be(new DateTime(2026, 11, 13, 3, 10, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Malformed_bodies_are_rejected_whole()
    {
        var result = new IngestResult();
        var act = () => TelemetryIngestService.Flatten(J("[1,2]"), result);
        act.Should().Throw<IngestRequestException>().Which.StatusCode.Should().Be(400);
    }
}

public class TelemetryGatewayServiceTests
{
    [Fact]
    public async Task Token_authenticates_until_revoked()
    {
        var db = TestDb.Create();
        var (_, site) = TestDb.SeedSite(db);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var svc = new TelemetryGatewayService(db, cache);

        var (gw, token) = await svc.CreateAsync(site.Id, "HA", null);
        token.Should().StartWith("sag_");
        gw.TokenHash.Should().NotContain(token);

        (await svc.AuthenticateAsync(token)).Should().NotBeNull();
        (await svc.AuthenticateAsync(token + "x")).Should().BeNull();
        (await svc.AuthenticateAsync("Bearer nonsense")).Should().BeNull();

        (await svc.RevokeAsync(gw.Id, site.Id)).Should().BeTrue();
        (await svc.AuthenticateAsync(token)).Should().BeNull();
    }

    [Fact]
    public void Rate_limit_allows_120_per_minute()
    {
        var allowed = Enumerable.Range(0, 130).Count(_ => TelemetryEndpoints.Allow(987654, minuteOverride: 1));
        allowed.Should().Be(TelemetryEndpoints.MaxRequestsPerMinute);
        TelemetryEndpoints.Allow(987654, minuteOverride: 2).Should().BeTrue("a new minute resets the window");
    }
}

public class SiteCapabilitiesTests
{
    [Fact]
    public void Capabilities_follow_channels_and_battery_flag()
    {
        var db = TestDb.Create();
        var (_, site) = TestDb.SeedSite(db, hasBattery: false);
        TelemetryChannel C(string key)
        {
            var p = TelemetryKey.Parse(key);
            return new TelemetryChannel { Key = key, Kind = p.Kind, DeviceIndex = p.DeviceIndex, Quantity = p.Quantity };
        }
        var channels = new[] { C("pv.string1.power_w"), C("pv.string2.power_w"), C("battery.soc_pct"), C("grid.power_w") };

        var caps = SiteCapabilities.From(site, channels);
        caps.StringCount.Should().Be(2);
        caps.HasBattery.Should().BeFalse("the site says it has no battery; a phantom SOC must not create a battery report");
        caps.HasGridMeter.Should().BeTrue();

        site.HasBattery = true;
        SiteCapabilities.From(site, channels).HasBattery.Should().BeTrue();
    }
}
