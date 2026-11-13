using FluentAssertions;
using SolarApp.Services.Telemetry;
using Xunit;

namespace SolarApp.Tests.Services.Telemetry;

public class HomeAssistantConfigGeneratorTests
{
    private static HomeAssistantConfig Gen(params HaEntity[] entities) =>
        HomeAssistantConfigGenerator.Generate("https://solar.example.com/", entities, "sag_abc123");

    private static int Count(string s, string token)
    {
        var n = 0;
        for (var i = s.IndexOf(token, StringComparison.Ordinal); i >= 0; i = s.IndexOf(token, i + token.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    [Fact]
    public void Rest_command_and_automation_have_the_expected_structure()
    {
        var c = Gen(new HaEntity("sensor.pv_power", "pv.power_w"), new HaEntity("binary_sensor.grid_ok", "grid.status"));
        var lines = c.ConfigurationYaml.Split('\n');

        lines.Should().Contain("rest_command:");
        lines.Should().Contain("  solarapp_push:");
        lines.Should().Contain("    url: \"https://solar.example.com/api/ingest/v1/telemetry\"");   // trailing slash trimmed
        lines.Should().Contain("    method: POST");
        lines.Should().Contain("      Authorization: !secret solarapp_token");
        lines.Should().Contain("    content_type: \"application/json\"");
        lines.Should().Contain("    payload: >-");
        lines.Should().Contain("automation:");
        lines.Should().Contain("      - platform: time_pattern");
        lines.Should().Contain("        seconds: 0");
        lines.Should().Contain("      - service: rest_command.solarapp_push");

        // YAML sanity: no tabs, no CR, every content line indented by an even number of spaces.
        c.ConfigurationYaml.Should().NotContain("\t").And.NotContain("\r");
        foreach (var l in lines.Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#')))
            ((l.Length - l.TrimStart().Length) % 2).Should().Be(0, l);

        // Everything after "payload: >-" up to the blank line is the block scalar, indented deeper.
        var p = Array.IndexOf(lines, "    payload: >-");
        var body = lines.Skip(p + 1).TakeWhile(l => l.Length > 0).ToList();
        body.Should().OnlyContain(l => l.StartsWith("      "));
        var template = string.Join("\n", body);
        Count(template, "{%").Should().Be(Count(template, "%}"));
        template.Should().Contain("now().isoformat()").And.Contain("float(none)").And.Contain("'unavailable'");
        body[^1].Trim().Should().Be("{\"ts\": {{ now().isoformat() | to_json }}, \"values\": { {{ ns.items | join(', ') }} }}");
    }

    [Fact]
    public void Mapped_entities_are_in_the_payload_once_and_listed_with_their_keys()
    {
        var c = Gen(new HaEntity("sensor.pv_power", "pv.power_w"), new HaEntity("sensor.batt_soc", "battery.soc_pct"),
            new HaEntity("SENSOR.PV_POWER", "pv.power_w"));

        c.Included.Should().Equal("sensor.pv_power", "sensor.batt_soc");
        Count(c.ConfigurationYaml, "\"sensor.pv_power\"").Should().Be(1);
        c.ConfigurationYaml.Should().Contain("        \"sensor.pv_power\",\n        \"sensor.batt_soc\"\n      ] -%}");
        c.ConfigurationYaml.Should().Contain("#   sensor.batt_soc → battery.soc_pct");
    }

    [Fact]
    public void Unsafe_entity_ids_are_skipped_not_injected()
    {
        var evil = "sensor.x\"] %}{{ 7*7 }}";
        var c = Gen(new HaEntity(evil, "pv.power_w"), new HaEntity("light.kitchen", "pv.power_w"),
            new HaEntity("sensor.ok", "pv.power_w"));

        c.Included.Should().Equal("sensor.ok");
        c.Skipped.Should().BeEquivalentTo(evil, "light.kitchen");   // light.* isn't read as an entity by the server
        var payload = c.ConfigurationYaml[c.ConfigurationYaml.IndexOf("payload:", StringComparison.Ordinal)..];
        payload.Should().NotContain("7*7");
        payload.Should().NotContain("light.kitchen");
    }

    [Fact]
    public void Secrets_urls_and_curl_are_quoted()
    {
        var c = HomeAssistantConfigGenerator.Generate("https://a.example/x\"y", Array.Empty<HaEntity>(), "sag_it's");
        c.SecretsYaml.Should().Contain("solarapp_token: \"Bearer sag_it's\"");
        c.ConfigurationYaml.Should().Contain("url: \"https://a.example/x\\\"y/api/ingest/v1/telemetry\"");
        c.PingCurl.Should().Be("curl -sS -H 'Authorization: Bearer sag_it'\"'\"'s' 'https://a.example/x\"y/api/ingest/v1/ping'");
        c.PushCurl.Should().Contain("-X POST").And.Contain("Content-Type: application/json");
        c.ConfigurationYaml.Should().Contain("ยังไม่มี entity").And.Contain("      {%- set entities = [\n      ] -%}");
    }

    [Fact]
    public void Without_a_token_a_placeholder_is_used()
    {
        var c = HomeAssistantConfigGenerator.Generate("https://x", Array.Empty<HaEntity>());
        c.SecretsYaml.Should().Contain(HomeAssistantConfigGenerator.TokenPlaceholder);
        c.PingCurl.Should().Contain(HomeAssistantConfigGenerator.TokenPlaceholder);
    }
}
