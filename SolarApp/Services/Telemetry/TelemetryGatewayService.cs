using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SolarApp.Data;
using SolarApp.Models.Telemetry;

namespace SolarApp.Services.Telemetry;

/// <summary>
/// Gateway tokens (DESIGN.md §4.1): 256-bit random, shown once, stored as
/// SHA-256. Lookups are cached for at most 60 seconds so a revoked token
/// stops working within a minute without a database hit per request.
/// </summary>
public class TelemetryGatewayService
{
    public const string TokenPrefix = "sag_";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly ApplicationDbContext _context;
    private readonly IMemoryCache _cache;

    public TelemetryGatewayService(ApplicationDbContext context, IMemoryCache cache)
    {
        _context = context;
        _cache = cache;
    }

    public static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return TokenPrefix + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Creates a gateway and returns the token — the only time it is ever visible.</summary>
    public async Task<(TelemetryGateway Gateway, string Token)> CreateAsync(int installationId, string name, string? userId)
    {
        var token = NewToken();
        var gw = new TelemetryGateway
        {
            InstallationId = installationId,
            Name = string.IsNullOrWhiteSpace(name) ? "Home Assistant" : name.Trim()[..Math.Min(100, name.Trim().Length)],
            TokenHash = HashToken(token),
            TokenPrefix = token[..Math.Min(12, token.Length)],
            CreatedByUserId = userId,
            CreatedAt = DateTime.UtcNow,
        };
        _context.TelemetryGateways.Add(gw);
        await _context.SaveChangesAsync();
        return (gw, token);
    }

    public async Task<bool> RevokeAsync(int gatewayId, int installationId)
    {
        var gw = await _context.TelemetryGateways
            .FirstOrDefaultAsync(g => g.Id == gatewayId && g.InstallationId == installationId);
        if (gw == null || gw.RevokedAt != null) return false;
        gw.RevokedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        _cache.Remove(CacheKey(gw.TokenHash));
        return true;
    }

    /// <summary>
    /// The active gateway for a bearer token, or null. Constant-time compare
    /// on the hash; the database lookup is by hash, so timing reveals nothing
    /// about valid tokens.
    /// </summary>
    public async Task<TelemetryGateway?> AuthenticateAsync(string? bearer)
    {
        if (string.IsNullOrWhiteSpace(bearer) || !bearer.StartsWith(TokenPrefix, StringComparison.Ordinal)
            || bearer.Length > 128)
            return null;

        var hash = HashToken(bearer);
        if (_cache.TryGetValue(CacheKey(hash), out GatewayIdentity? cached) && cached != null)
            return cached.RevokedAt == null ? await AttachAsync(cached.Id) : null;

        var gw = await _context.TelemetryGateways.AsNoTracking()
            .Where(g => g.TokenHash == hash)
            .Select(g => new GatewayIdentity(g.Id, g.TokenHash, g.RevokedAt))
            .FirstOrDefaultAsync();
        if (gw == null || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(gw.TokenHash), Encoding.ASCII.GetBytes(hash)))
            return null;

        _cache.Set(CacheKey(hash), gw, CacheTtl);
        return gw.RevokedAt == null ? await AttachAsync(gw.Id) : null;
    }

    private Task<TelemetryGateway?> AttachAsync(int id) =>
        _context.TelemetryGateways
            .Include(g => g.Installation)
            .FirstOrDefaultAsync(g => g.Id == id && g.RevokedAt == null);

    private static string CacheKey(string hash) => "tgw:" + hash;

    private sealed record GatewayIdentity(int Id, string TokenHash, DateTime? RevokedAt);
}
