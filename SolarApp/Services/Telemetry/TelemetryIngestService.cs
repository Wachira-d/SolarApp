using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Models.Telemetry;

namespace SolarApp.Services.Telemetry;

/// <summary>Result of one ingest request (DESIGN.md §4.2).</summary>
public sealed class IngestResult
{
    public int Accepted { get; set; }
    public int Duplicates { get; set; }
    public int Rejected { get; set; }
    public Dictionary<string, int> RejectReasons { get; } = new();
    public SortedSet<string> UnmappedEntities { get; } = new(StringComparer.Ordinal);
    public int NewChannels { get; set; }
    public int Events { get; set; }

    public void Reject(string reason, int n = 1)
    {
        Rejected += n;
        RejectReasons[reason] = RejectReasons.GetValueOrDefault(reason) + n;
    }
}

/// <summary>Thrown for a request that cannot be processed at all (malformed body, too large).</summary>
public sealed class IngestRequestException : Exception
{
    public int StatusCode { get; }
    public IngestRequestException(int statusCode, string message) : base(message) => StatusCode = statusCode;
}

/// <summary>
/// Accepts telemetry pushed by a site's gateway (normally Home Assistant).
/// Validation, mapping, de-duplication and storage follow DESIGN.md §4;
/// side effects (channel catalog, LastOnlineAt, an EnergyData snapshot so
/// every existing page works for gateway sites) follow §4.4.
/// </summary>
public class TelemetryIngestService
{
    public const int MaxSamplesPerRequest = 5000;
    public const int MaxChannelsPerSite = 2000;
    public static readonly TimeSpan MaxPast = TimeSpan.FromDays(35);
    public static readonly TimeSpan MaxFuture = TimeSpan.FromMinutes(5);
    /// <summary>EnergyData realtime rows from a gateway at most this often — they feed the existing pages, not analytics.</summary>
    public static readonly TimeSpan SnapshotInterval = TimeSpan.FromMinutes(5);

    private readonly ApplicationDbContext _context;
    private readonly ITelemetryStore _store;
    private readonly ILogger<TelemetryIngestService> _logger;

    public TelemetryIngestService(ApplicationDbContext context, ITelemetryStore store, ILogger<TelemetryIngestService> logger)
    {
        _context = context;
        _store = store;
        _logger = logger;
    }

    private readonly record struct Incoming(string? Key, string? EntityId, DateTime TsUtc, double Value);

    /// <summary>Parse either body shape into flat samples; malformed entries are rejected individually.</summary>
    public static List<(string? Key, string? EntityId, JsonElement Ts, JsonElement Value)> Flatten(JsonElement root, IngestResult result)
    {
        var list = new List<(string?, string?, JsonElement, JsonElement)>();
        if (root.ValueKind != JsonValueKind.Object)
            throw new IngestRequestException(400, "body must be a JSON object");

        if (root.TryGetProperty("samples", out var samples))
        {
            if (samples.ValueKind != JsonValueKind.Array) throw new IngestRequestException(400, "samples must be an array");
            foreach (var s in samples.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object) { result.Reject("bad_sample"); continue; }
                var key = s.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
                var entity = s.TryGetProperty("entity_id", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                s.TryGetProperty("ts", out var ts);
                s.TryGetProperty("value", out var v);
                list.Add((key, entity, ts, v));
            }
        }

        if (root.TryGetProperty("values", out var values))
        {
            if (values.ValueKind != JsonValueKind.Object) throw new IngestRequestException(400, "values must be an object");
            root.TryGetProperty("ts", out var ts);
            foreach (var p in values.EnumerateObject())
            {
                var name = p.Name.Trim();
                // A name that isn't a canonical key but looks like an HA
                // entity id is an entity; anything else is taken as a key.
                if (TelemetryKey.LooksLikeEntityId(name)) list.Add((null, name, ts, p.Value));
                else list.Add((name.ToLowerInvariant(), null, ts, p.Value));
            }
        }

        if (list.Count > MaxSamplesPerRequest)
            throw new IngestRequestException(413, $"at most {MaxSamplesPerRequest} samples per request");
        return list;
    }

    public static bool TryParseTimestamp(JsonElement ts, out DateTime utc, out string? reason)
    {
        utc = default;
        reason = null;
        if (ts.ValueKind == JsonValueKind.Number && ts.TryGetDouble(out var n) && double.IsFinite(n) && n > 0)
        {
            // Unix seconds or milliseconds (ms values are > 1e11 for any date after 1973).
            var ms = n > 1e11 ? n : n * 1000d;
            try { utc = DateTime.UnixEpoch.AddMilliseconds(Math.Floor(ms)); }
            catch (ArgumentOutOfRangeException) { reason = "ts_unparseable"; return false; }
        }
        else if (ts.ValueKind == JsonValueKind.String)
        {
            var s = ts.GetString()?.Trim();
            if (string.IsNullOrEmpty(s)) { reason = "ts_missing"; return false; }
            if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            { reason = "ts_unparseable"; return false; }
            // An ISO string without an offset is ambiguous (site time? UTC?).
            // HA's now().isoformat() always carries one; refuse to guess.
            if (!HasExplicitOffset(s)) { reason = "ts_no_offset"; return false; }
            utc = dto.UtcDateTime;
        }
        else { reason = "ts_missing"; return false; }

        // datetime2(0): whole seconds, so the same instant always dedupes.
        utc = new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        return true;
    }

    private static bool HasExplicitOffset(string s)
    {
        if (s.EndsWith("Z", StringComparison.OrdinalIgnoreCase)) return true;
        var t = s.IndexOf('T');
        if (t < 0) t = s.IndexOf(' ');
        if (t < 0) return false;
        var time = s[(t + 1)..];
        return time.Contains('+') || time.Contains('-');
    }

    public static bool TryParseValue(JsonElement v, out double value)
    {
        value = 0;
        switch (v.ValueKind)
        {
            case JsonValueKind.Number:
                return v.TryGetDouble(out value) && double.IsFinite(value);
            case JsonValueKind.True: value = 1; return true;
            case JsonValueKind.False: value = 0; return true;
            case JsonValueKind.String:
                var s = v.GetString()?.Trim();
                // HA states "on"/"off" for binary sensors (grid present, etc.)
                if (string.Equals(s, "on", StringComparison.OrdinalIgnoreCase)) { value = 1; return true; }
                if (string.Equals(s, "off", StringComparison.OrdinalIgnoreCase)) { value = 0; return true; }
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
            default:
                return false;
        }
    }

    public async Task<IngestResult> IngestTelemetryAsync(TelemetryGateway gateway, JsonElement body, DateTime nowUtc,
        string? remoteIp, CancellationToken ct = default)
    {
        var result = new IngestResult();
        var raw = Flatten(body, result);
        var installationId = gateway.InstallationId;

        var maps = await _context.TelemetryEntityMaps.AsNoTracking()
            .Where(m => m.InstallationId == installationId)
            .ToDictionaryAsync(m => m.EntityId, StringComparer.OrdinalIgnoreCase, ct);

        // ── 1. Validate and map ──
        var incoming = new List<Incoming>(raw.Count);
        var entityForKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key0, entity, tsEl, valEl) in raw)
        {
            if (!TryParseTimestamp(tsEl, out var ts, out var tsReason)) { result.Reject(tsReason!); continue; }
            if (ts < nowUtc - MaxPast || ts > nowUtc + MaxFuture) { result.Reject("ts_out_of_window"); continue; }
            if (!TryParseValue(valEl, out var value)) { result.Reject("bad_value"); continue; }

            string key;
            if (!string.IsNullOrEmpty(key0))
            {
                key = key0.Trim().ToLowerInvariant();
                if (!TelemetryKey.IsValid(key)) { result.Reject("bad_key"); continue; }
            }
            else if (!string.IsNullOrEmpty(entity))
            {
                if (maps.TryGetValue(entity.Trim(), out var map))
                {
                    key = map.Key;
                    value = (value * map.Scale + map.Offset) * (map.Invert ? -1 : 1);
                }
                else
                {
                    key = TelemetryKey.ForUnmappedEntity(entity);
                    result.UnmappedEntities.Add(entity.Trim());
                }
                entityForKey.TryAdd(key, entity.Trim());
            }
            else { result.Reject("no_key"); continue; }

            if (!double.IsFinite(value)) { result.Reject("bad_value"); continue; }
            incoming.Add(new Incoming(key, entity, ts, value));
        }

        // ── 2. Channels (create unknown keys up to the cap) ──
        var channels = await _context.TelemetryChannels
            .Where(c => c.InstallationId == installationId)
            .ToDictionaryAsync(c => c.Key, StringComparer.Ordinal, ct);

        var newKeys = incoming.Select(i => i.Key!).Distinct(StringComparer.Ordinal)
            .Where(k => !channels.ContainsKey(k)).ToList();
        if (newKeys.Count > 0)
        {
            var room = MaxChannelsPerSite - channels.Count;
            foreach (var key in newKeys)
            {
                if (room <= 0) break;
                var p = TelemetryKey.Parse(key);
                var first = incoming.Where(i => i.Key == key).Min(i => i.TsUtc);
                var ch = new TelemetryChannel
                {
                    InstallationId = installationId,
                    Key = key,
                    Kind = p.Kind,
                    DeviceIndex = p.DeviceIndex,
                    SubIndex = p.SubIndex,
                    Quantity = p.Quantity,
                    Unit = p.Unit,
                    IsCumulative = p.IsCumulative,
                    SourceEntityId = entityForKey.GetValueOrDefault(key),
                    FirstTs = first,
                    LastTs = DateTime.MinValue,
                };
                _context.TelemetryChannels.Add(ch);
                channels[key] = ch;
                room--;
                result.NewChannels++;
            }
            await _context.SaveChangesAsync(ct);   // channel ids are needed for the raw rows
        }

        // ── 3. Dedupe within the request (last wins), split fresh vs replay ──
        var byChannelTs = new Dictionary<(int, DateTime), double>();
        foreach (var i in incoming)
        {
            if (!channels.TryGetValue(i.Key!, out var ch)) { result.Reject("channel_limit"); continue; }
            if (byChannelTs.ContainsKey((ch.Id, i.TsUtc))) result.Duplicates++;
            byChannelTs[(ch.Id, i.TsUtc)] = i.Value;
        }

        var channelById = channels.Values.ToDictionary(c => c.Id);
        var fresh = new List<RawSample>();
        var replayKeys = new List<(int, DateTime)>();
        foreach (var ((chId, ts), value) in byChannelTs)
        {
            if (ts > channelById[chId].LastTs) fresh.Add(new RawSample(chId, ts, value));
            else replayKeys.Add((chId, ts));
        }

        var toAppend = new List<RawSample>(fresh);
        if (replayKeys.Count > 0)
        {
            var existing = await _store.ExistingAsync(replayKeys, ct);
            foreach (var k in replayKeys)
            {
                if (existing.Contains(k)) { result.Duplicates++; continue; }
                toAppend.Add(new RawSample(k.Item1, k.Item2, byChannelTs[k]));
            }
        }

        // ── 4. Store ──
        if (toAppend.Count > 0)
            await _store.AppendRawAsync(toAppend, ct);
        result.Accepted = toAppend.Count;

        // ── 5. Catalog, events, late-data marks ──
        foreach (var g in toAppend.GroupBy(s => s.ChannelId))
        {
            var ch = channelById[g.Key];
            var latest = g.MaxBy(s => s.TsUtc);
            var earliest = g.Min(s => s.TsUtc);
            if (earliest < ch.FirstTs || ch.FirstTs == default) ch.FirstTs = earliest;

            if (ch.Quantity is TelemetryQuantity.Status or TelemetryQuantity.AlarmCode)
                result.Events += AddStatusEvents(gateway, ch, g.OrderBy(s => s.TsUtc));

            if (latest.TsUtc > ch.LastTs)
            {
                ch.LastTs = latest.TsUtc;
                ch.LastValue = latest.Value;
            }
        }

        var state = await _context.TelemetryRollupStates.FindAsync(new object[] { installationId }, ct);
        if (state != null)
        {
            var late = toAppend.Where(s => s.TsUtc < state.WatermarkUtc).ToList();
            if (late.Count > 0)
                _context.TelemetryDirtyRanges.Add(new TelemetryDirtyRange
                {
                    InstallationId = installationId,
                    FromUtc = late.Min(s => s.TsUtc),
                    ToUtc = late.Max(s => s.TsUtc),
                });
        }

        // ── 6. Gateway + site liveness ──
        var gw = await _context.TelemetryGateways.FirstAsync(g => g.Id == gateway.Id, ct);
        gw.LastSeenAt = nowUtc;
        gw.LastSeenIp = remoteIp?.Length > 64 ? remoteIp[..64] : remoteIp;
        gw.SamplesAccepted += result.Accepted;

        var inst = await _context.Installations.FirstOrDefaultAsync(i => i.Id == installationId, ct);
        if (inst != null && result.Accepted > 0)
        {
            // The gateway answering is "contact": the health monitor treats a
            // pushing site exactly like a reachable cloud.
            inst.LastOnlineAt = nowUtc;
            if (inst.Status == InstallationStatus.Offline) inst.Status = InstallationStatus.Active;
            await WriteSnapshotAsync(inst, channels.Values, nowUtc, ct);
        }

        await _context.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>A DeviceEvent for each change of a status / alarm channel.</summary>
    private int AddStatusEvents(TelemetryGateway gateway, TelemetryChannel ch, IEnumerable<RawSample> samples)
    {
        var brand = gateway.Installation?.InverterBrand ?? InverterBrand.Other;
        var kind = ch.Quantity == TelemetryQuantity.AlarmCode ? "alarm" : "status";
        var source = ch.Kind == TelemetryDeviceKind.Inverter ? $"inverter{Math.Max(1, ch.DeviceIndex)}" : ch.Key;
        var previous = ch.LastTs == DateTime.MinValue ? (double?)null : ch.LastValue;
        var added = 0;
        foreach (var s in samples)
        {
            if (s.TsUtc <= ch.LastTs) continue;                 // replays don't re-announce
            if (previous.HasValue && Math.Abs(previous.Value - s.Value) < 0.5) { previous = s.Value; continue; }
            var code = (long)Math.Round(s.Value);
            var d = StatusCodeCatalog.Decode(brand, kind, code);
            _context.DeviceEvents.Add(new DeviceEvent
            {
                InstallationId = gateway.InstallationId,
                TsUtc = s.TsUtc,
                Source = source,
                EventKind = kind,
                Code = code,
                Decoded = d.Text.Length > 300 ? d.Text[..300] : d.Text,
                Severity = d.Severity,
            });
            previous = s.Value;
            added++;
        }
        return added;
    }

    /// <summary>
    /// A realtime EnergyData row from the latest canonical values, at most
    /// every SnapshotInterval, plus today's generation on the Daily row — the
    /// bridge that lets dashboards, reports and alerts work unchanged for
    /// gateway sites (DESIGN.md §4.4).
    /// </summary>
    private async Task WriteSnapshotAsync(Installation inst, IEnumerable<TelemetryChannel> channels, DateTime nowUtc,
        CancellationToken ct)
    {
        var byKey = channels.Where(c => c.LastTs > nowUtc.AddMinutes(-15)).ToDictionary(c => c.Key, c => c.LastValue);
        if (byKey.Count == 0) return;

        var since = nowUtc - SnapshotInterval;
        var recent = await _context.EnergyData.AnyAsync(e => e.InstallationId == inst.Id
            && e.Granularity == DataGranularity.Realtime && e.Timestamp > since, ct);
        if (recent) return;

        decimal? D(string k) => byKey.TryGetValue(k, out var v) ? (decimal)v : null;
        var pvW = D("pv.power_w") ?? SumStrings(byKey);
        var gridW = D("grid.power_w");
        var today = D("pv.energy_today_kwh");

        _context.EnergyData.Add(new EnergyData
        {
            InstallationId = inst.Id,
            Timestamp = nowUtc,
            Granularity = DataGranularity.Realtime,
            PowerGenerationKw = (pvW ?? 0) / 1000m,
            EnergyGenerationKwh = today ?? 0,
            GridImportKwh = gridW is > 0 ? gridW.Value / 1000m : 0,     // instantaneous kW, as cloud adapters store it
            GridExportKwh = gridW is < 0 ? -gridW.Value / 1000m : 0,
            BatterySocPercent = inst.HasBattery ? D("battery.soc_pct") : null,
        });

        if (today is > 0)
        {
            var siteToday = SiteTime.Today;
            var daily = await _context.EnergyData.FirstOrDefaultAsync(e => e.InstallationId == inst.Id
                && e.Granularity == DataGranularity.Daily && e.Timestamp == siteToday, ct);
            if (daily == null)
                _context.EnergyData.Add(new EnergyData
                {
                    InstallationId = inst.Id, Timestamp = siteToday, Granularity = DataGranularity.Daily,
                    EnergyGenerationKwh = today.Value,
                });
            else if (today.Value > daily.EnergyGenerationKwh)   // the day's counter only rises
                daily.EnergyGenerationKwh = today.Value;
        }
    }

    private static decimal? SumStrings(Dictionary<string, double> byKey)
    {
        var strings = byKey.Where(kv => kv.Key.StartsWith("pv.string", StringComparison.Ordinal)
                                        && kv.Key.EndsWith(".power_w", StringComparison.Ordinal)).ToList();
        return strings.Count == 0 ? null : (decimal)strings.Sum(kv => kv.Value);
    }

    /// <summary>Events body (DESIGN.md §4.2): explicit status / alarm events from the gateway.</summary>
    public async Task<IngestResult> IngestEventsAsync(TelemetryGateway gateway, JsonElement body, DateTime nowUtc,
        CancellationToken ct = default)
    {
        var result = new IngestResult();
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("events", out var events)
            || events.ValueKind != JsonValueKind.Array)
            throw new IngestRequestException(400, "body must be {\"events\": [...]}");
        if (events.GetArrayLength() > 1000) throw new IngestRequestException(413, "at most 1000 events per request");

        var brand = gateway.Installation?.InverterBrand ?? InverterBrand.Other;
        foreach (var e in events.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) { result.Reject("bad_event"); continue; }
            e.TryGetProperty("ts", out var tsEl);
            if (!TryParseTimestamp(tsEl, out var ts, out var reason)) { result.Reject(reason!); continue; }
            if (ts < nowUtc - MaxPast || ts > nowUtc + MaxFuture) { result.Reject("ts_out_of_window"); continue; }
            if (!e.TryGetProperty("code", out var codeEl) || !TryParseValue(codeEl, out var codeD))
            { result.Reject("bad_code"); continue; }

            var kind = e.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
                       && k.GetString() == "alarm" ? "alarm" : "status";
            var source = e.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String
                ? (src.GetString() ?? "device").Trim() : "device";
            var msg = e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            var code = (long)Math.Round(codeD);
            var d = StatusCodeCatalog.Decode(brand, kind, code);

            _context.DeviceEvents.Add(new DeviceEvent
            {
                InstallationId = gateway.InstallationId,
                TsUtc = ts,
                Source = source.Length > 64 ? source[..64] : source,
                EventKind = kind,
                Code = code,
                Decoded = d.Text.Length > 300 ? d.Text[..300] : d.Text,
                Severity = d.Severity,
                Message = msg?.Length > 500 ? msg[..500] : msg,
            });
            result.Accepted++;
            result.Events++;
        }

        var gw = await _context.TelemetryGateways.FirstAsync(g => g.Id == gateway.Id, ct);
        gw.LastSeenAt = nowUtc;
        await _context.SaveChangesAsync(ct);
        return result;
    }
}
