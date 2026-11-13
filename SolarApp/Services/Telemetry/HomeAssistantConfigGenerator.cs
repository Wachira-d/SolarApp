using System.Text;
using System.Text.RegularExpressions;

namespace SolarApp.Services.Telemetry;

/// <summary>An HA entity to push and the channel it lands on (for the comment next to it).</summary>
public sealed record HaEntity(string EntityId, string Key);

/// <summary>The snippets the gateway page shows, ready to paste.</summary>
public sealed record HomeAssistantConfig(
    string SecretsYaml, string ConfigurationYaml, string PingCurl, string PushCurl,
    IReadOnlyList<string> Included, IReadOnlyList<string> Skipped);

/// <summary>
/// Builds the Home Assistant side of a gateway (DESIGN.md §4): a
/// <c>rest_command</c> that posts the snapshot form
/// <c>{"ts": …, "values": {entity: value}}</c> and an automation that fires
/// it every minute.
///
/// The payload sends <b>entity ids</b>, not canonical keys: the server applies
/// the site's mapping (key, scale, offset, invert), so fixing a mapping on
/// the page never needs the YAML to be pasted again. Values go through
/// <c>float(none)</c> and are left out when not numeric, so an "unavailable"
/// sensor drops out of that minute's post instead of failing it.
///
/// Pure string building, unit-tested: what is pasted into someone's HA must
/// be valid YAML and valid Jinja whatever an operator typed into the mapping.
/// </summary>
public static partial class HomeAssistantConfigGenerator
{
    public const string IngestPath = "/api/ingest/v1/telemetry";
    public const string PingPath = "/api/ingest/v1/ping";
    public const string CommandName = "solarapp_push";
    public const string SecretName = "solarapp_token";
    public const string TokenPlaceholder = "sag_ใส่โทเค็นของเกตเวย์ที่นี่";

    /// <summary>HA's own entity id rule — anything else could break out of the Jinja string, so it is skipped.</summary>
    [GeneratedRegex(@"^[a-z0-9_]+\.[a-z0-9_]+$")]
    private static partial Regex HaEntityId();

    public static HomeAssistantConfig Generate(string baseUrl, IEnumerable<HaEntity> entities, string? token = null)
    {
        var root = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        var url = root + IngestPath;
        var pingUrl = root + PingPath;
        var tok = string.IsNullOrWhiteSpace(token) ? TokenPlaceholder : token.Trim();

        var included = new List<HaEntity>();
        var skipped = new List<string>();
        foreach (var e in entities.DistinctBy(e => e.EntityId.Trim().ToLowerInvariant()))
        {
            var id = e.EntityId.Trim().ToLowerInvariant();
            // The server only reads a values name as an entity when its HA
            // domain is known (TelemetryKey.LooksLikeEntityId); anything else
            // would be rejected as a bad key, so don't send it.
            if (HaEntityId().IsMatch(id) && TelemetryKey.LooksLikeEntityId(id)) included.Add(e with { EntityId = id });
            else skipped.Add(e.EntityId);
        }

        var secrets = new StringBuilder()
            .AppendLine("# secrets.yaml — ทั้งบรรทัดรวมคำว่า Bearer")
            .AppendLine($"{SecretName}: {YamlQuote("Bearer " + tok)}")
            .ToString();

        var y = new StringBuilder();
        y.AppendLine("# ── SolarApp: ส่งข้อมูลจาก Home Assistant ทุก 1 นาที ──");
        y.AppendLine($"# โทเค็นอยู่ใน secrets.yaml ({SecretName}); เปลี่ยนโทเค็นได้โดยไม่ต้องแก้ไฟล์นี้");
        y.AppendLine("# ถ้า configuration.yaml มี rest_command: หรือ automation: อยู่แล้ว ให้ย้ายรายการด้านล่างไปรวมไว้ใต้หัวข้อเดิม");
        if (included.Count > 0)
        {
            // The mapping as YAML comments: a Jinja comment would be a syntax
            // error inside the {% set %} tag that holds the list.
            y.AppendLine("# entity ที่ส่ง → ช่องข้อมูลใน SolarApp (แปลงหน่วย/กลับเครื่องหมายที่เซิร์ฟเวอร์):");
            foreach (var e in included) y.AppendLine($"#   {e.EntityId} → {SafeKey(e.Key)}");
        }
        y.AppendLine("rest_command:");
        y.AppendLine($"  {CommandName}:");
        y.AppendLine($"    url: {YamlQuote(url)}");
        y.AppendLine("    method: POST");
        y.AppendLine("    headers:");
        y.AppendLine($"      Authorization: !secret {SecretName}");
        y.AppendLine("    content_type: \"application/json\"");
        y.AppendLine("    timeout: 20");
        // Block scalar: nothing inside is YAML-interpreted, so quotes, colons
        // and '#' in the template are safe; '-' trims Jinja whitespace.
        y.AppendLine("    payload: >-");
        y.AppendLine("      {%- set entities = [");
        for (var i = 0; i < included.Count; i++)
        {
            var e = included[i];
            var comma = i < included.Count - 1 ? "," : "";
            y.AppendLine($"        \"{e.EntityId}\"{comma}");
        }
        y.AppendLine("      ] -%}");
        y.AppendLine("      {%- set ns = namespace(items=[]) -%}");
        y.AppendLine("      {%- for e in entities -%}");
        y.AppendLine("        {%- set s = states(e) -%}");
        y.AppendLine("        {%- if s in ['on', 'off'] -%}");
        y.AppendLine("          {%- set v = 1 if s == 'on' else 0 -%}");
        y.AppendLine("        {%- else -%}");
        y.AppendLine("          {%- set v = s | float(none) -%}");
        y.AppendLine("        {%- endif -%}");
        y.AppendLine("        {%- if v is not none and s not in ['unavailable', 'unknown'] -%}");
        y.AppendLine("          {%- set ns.items = ns.items + [(e | to_json) ~ ': ' ~ (v | to_json)] -%}");
        y.AppendLine("        {%- endif -%}");
        y.AppendLine("      {%- endfor -%}");
        y.AppendLine("      {\"ts\": {{ now().isoformat() | to_json }}, \"values\": { {{ ns.items | join(', ') }} }}");
        y.AppendLine();
        y.AppendLine("automation:");
        y.AppendLine("  - id: solarapp_push_telemetry");
        y.AppendLine("    alias: \"SolarApp - ส่งข้อมูลทุกนาที\"");
        y.AppendLine("    mode: single");
        y.AppendLine("    trigger:");
        y.AppendLine("      - platform: time_pattern");
        y.AppendLine("        seconds: 0");
        y.AppendLine("    action:");
        y.AppendLine($"      - service: rest_command.{CommandName}");

        if (included.Count == 0)
            y.Insert(0, "# ยังไม่มี entity ที่จับคู่ไว้ — เพิ่มการจับคู่ในหน้านี้ก่อน แล้วสร้าง YAML ใหม่\n");
        if (skipped.Count > 0)
            y.Insert(0, "# ข้ามรายการที่ไม่ใช่ entity id ของ HA: " + string.Join(", ", skipped.Select(YamlComment)) + "\n");

        var ping = $"curl -sS -H {ShellQuote("Authorization: Bearer " + tok)} {ShellQuote(pingUrl)}";
        var sampleBody = "{\"ts\": \"" + DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + "\", \"values\": {\"pv.power_w\": 0}}";
        var push = $"curl -sS -X POST -H {ShellQuote("Authorization: Bearer " + tok)} " +
                   $"-H 'Content-Type: application/json' -d {ShellQuote(sampleBody)} {ShellQuote(url)}";

        // Unix newlines whatever the server OS (AppendLine is \r\n on IIS).
        return new HomeAssistantConfig(secrets.Replace("\r\n", "\n"), y.ToString().Replace("\r\n", "\n"), ping, push,
            included.Select(e => e.EntityId).ToList(), skipped);
    }

    /// <summary>YAML double-quoted scalar.</summary>
    public static string YamlQuote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(c)) sb.Append($"\\x{(int)c:x2}");
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>POSIX single-quoted shell word.</summary>
    public static string ShellQuote(string s) => "'" + s.Replace("'", "'\"'\"'") + "'";

    // A canonical key is [a-z0-9_.] by validation, but the mapping table is
    // operator input: keep the comment on one harmless line whatever it holds.
    private static string SafeKey(string s) => Regex.Replace(s, @"[^a-zA-Z0-9_.\-]", "");

    private static string YamlComment(string s) => Regex.Replace(s, @"[\r\n]+", " ");
}
