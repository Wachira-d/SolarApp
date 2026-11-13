using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;

namespace SolarApp.Services.Telemetry;

/// <summary>
/// /api/ingest/v1/* (DESIGN.md §4.2). Anonymous to cookie auth; the bearer
/// gateway token is the only credential, and a request can only ever touch
/// the gateway's own site.
/// </summary>
public static class TelemetryEndpoints
{
    public const int MaxBodyBytes = 1_048_576;
    public const int MaxRequestsPerMinute = 120;

    private static readonly ConcurrentDictionary<int, (long Minute, int Count)> Rate = new();

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/ingest/v1").AllowAnonymous();

        group.MapGet("/ping", async (HttpContext http, TelemetryGatewayService gateways, ApplicationDbContext db) =>
        {
            var gw = await gateways.AuthenticateAsync(Bearer(http));
            if (gw == null) return Unauthorized();
            var keys = await db.TelemetryChannels.AsNoTracking()
                .Where(c => c.InstallationId == gw.InstallationId)
                .OrderBy(c => c.Key).Select(c => c.Key).Take(500).ToListAsync();
            return Results.Json(new
            {
                ok = true,
                site = gw.Installation.SiteName,
                gateway = gw.Name,
                serverTimeUtc = DateTime.UtcNow,
                recommendedIntervalSeconds = 60,
                knownKeys = keys,
            });
        });

        group.MapPost("/telemetry", async (HttpContext http, TelemetryGatewayService gateways,
            TelemetryIngestService ingest, ILoggerFactory lf) =>
        {
            var gw = await gateways.AuthenticateAsync(Bearer(http));
            if (gw == null) return Unauthorized();
            if (!Allow(gw.Id)) return TooMany();
            try
            {
                using var doc = await ReadJsonAsync(http);
                var result = await ingest.IngestTelemetryAsync(gw, doc.RootElement, DateTime.UtcNow,
                    http.Connection.RemoteIpAddress?.ToString(), http.RequestAborted);
                return Results.Json(new
                {
                    accepted = result.Accepted,
                    duplicates = result.Duplicates,
                    rejected = result.Rejected,
                    rejectReasons = result.RejectReasons,
                    newChannels = result.NewChannels,
                    events = result.Events,
                    unmappedEntities = result.UnmappedEntities.Take(100),
                    serverTimeUtc = DateTime.UtcNow,
                }, statusCode: 202);
            }
            catch (IngestRequestException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
            }
            catch (Exception ex)
            {
                lf.CreateLogger("TelemetryIngest").LogError(ex, "Telemetry ingest failed for gateway {Id}", gw.Id);
                return Results.Json(new { error = "internal error" }, statusCode: 500);
            }
        });

        group.MapPost("/events", async (HttpContext http, TelemetryGatewayService gateways,
            TelemetryIngestService ingest, ILoggerFactory lf) =>
        {
            var gw = await gateways.AuthenticateAsync(Bearer(http));
            if (gw == null) return Unauthorized();
            if (!Allow(gw.Id)) return TooMany();
            try
            {
                using var doc = await ReadJsonAsync(http);
                var result = await ingest.IngestEventsAsync(gw, doc.RootElement, DateTime.UtcNow, http.RequestAborted);
                return Results.Json(new { accepted = result.Accepted, rejected = result.Rejected, rejectReasons = result.RejectReasons },
                    statusCode: 202);
            }
            catch (IngestRequestException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
            }
            catch (Exception ex)
            {
                lf.CreateLogger("TelemetryIngest").LogError(ex, "Event ingest failed for gateway {Id}", gw.Id);
                return Results.Json(new { error = "internal error" }, statusCode: 500);
            }
        });
    }

    internal static string? Bearer(HttpContext http)
    {
        var h = http.Request.Headers.Authorization.ToString();
        return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : null;
    }

    /// <summary>Fixed one-minute window per gateway. In-process: one IIS worker, so no shared store needed.</summary>
    internal static bool Allow(int gatewayId, long? minuteOverride = null)
    {
        var minute = minuteOverride ?? DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute;
        var entry = Rate.AddOrUpdate(gatewayId, _ => (minute, 1),
            (_, e) => e.Minute == minute ? (minute, e.Count + 1) : (minute, 1));
        return entry.Count <= MaxRequestsPerMinute;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpContext http)
    {
        if (http.Request.ContentLength is > MaxBodyBytes)
            throw new IngestRequestException(413, $"body larger than {MaxBodyBytes} bytes");

        // Read at most MaxBodyBytes + 1 so a chunked body without a length is capped too.
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int n;
        while ((n = await http.Request.Body.ReadAsync(chunk, http.RequestAborted)) > 0)
        {
            buffer.Write(chunk, 0, n);
            if (buffer.Length > MaxBodyBytes)
                throw new IngestRequestException(413, $"body larger than {MaxBodyBytes} bytes");
        }
        buffer.Position = 0;
        try { return await JsonDocument.ParseAsync(buffer, cancellationToken: http.RequestAborted); }
        catch (JsonException ex) { throw new IngestRequestException(400, "invalid JSON: " + ex.Message); }
    }

    private static IResult Unauthorized() =>
        Results.Json(new { error = "invalid or revoked gateway token" }, statusCode: 401);

    private static IResult TooMany() =>
        Results.Json(new { error = $"rate limit: {MaxRequestsPerMinute} requests per minute" }, statusCode: 429);
}
