using Microsoft.AspNetCore.DataProtection;

namespace SolarApp.Services.Security;

/// <summary>
/// Encrypts/decrypts sensitive credential strings before storage.
/// Uses ASP.NET Core DataProtection — keys auto-rotate and are stored in
/// %LOCALAPPDATA%\ASP.NET\DataProtection-Keys on Windows by default.
///
/// Backward-compatible: if the stored value is not prefixed with "enc:",
/// it's treated as plaintext legacy data and returned as-is. This lets
/// existing installations keep working while new writes are encrypted.
/// </summary>
public interface ICredentialProtector
{
    /// <summary>Encrypt a plaintext credential for storage. Returns "enc:{protected}".</summary>
    string? Protect(string? plaintext);

    /// <summary>Decrypt a stored credential. Handles both encrypted ("enc:...")
    /// and legacy plaintext values transparently.</summary>
    string? Unprotect(string? stored);

    /// <summary>True if the stored value is already encrypted.</summary>
    bool IsProtected(string? stored);
}

public class CredentialProtector : ICredentialProtector
{
    private const string Prefix = "enc:";
    private const string PurposeName = "SolarApp.Credentials.v1";
    private readonly IDataProtector _protector;
    private readonly ILogger<CredentialProtector> _logger;

    public CredentialProtector(IDataProtectionProvider provider, ILogger<CredentialProtector> logger)
    {
        _protector = provider.CreateProtector(PurposeName);
        _logger = logger;
    }

    public string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;
        if (plaintext.StartsWith(Prefix)) return plaintext; // already encrypted
        try
        {
            return Prefix + _protector.Protect(plaintext);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to encrypt credential (falling back to plaintext storage)");
            return plaintext;
        }
    }

    public string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return stored;
        if (!stored.StartsWith(Prefix)) return stored; // legacy plaintext
        try
        {
            return _protector.Unprotect(stored[Prefix.Length..]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt credential — may have been encrypted with a different key");
            return null;
        }
    }

    public bool IsProtected(string? stored)
        => !string.IsNullOrEmpty(stored) && stored.StartsWith(Prefix);
}
