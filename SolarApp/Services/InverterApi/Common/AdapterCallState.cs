namespace SolarApp.Services.InverterApi.Common;

/// <summary>
/// AsyncLocal scratchpad an adapter's internal data method can use to record
/// "a transport/API error happened" so the surrounding *ResultAsync wrapper
/// can turn an empty list into a proper <c>Fail</c> instead of a misleading
/// <c>Ok(0 records)</c>.
///
/// Why this exists: every adapter's Get*DataAsync historically caught HTTP
/// failures and returned an empty list, so "0 records fetched" meant BOTH
/// "the site genuinely has no data" AND "the vendor cloud was down" — the
/// backfill UI and logs couldn't tell them apart.  The internal method now
/// sets <see cref="Fail"/> on error; the wrapper reads (and clears) it.
///
/// AsyncLocal keeps it per-logical-call so concurrent syncs don't clobber
/// each other's flag.
/// </summary>
public static class AdapterCallState
{
    private static readonly AsyncLocal<string?> _fail = new();

    /// <summary>Record that a transport/API error occurred in the current call.</summary>
    public static void Fail(string reason) => _fail.Value = reason;

    /// <summary>Read and clear the recorded failure (null if none).</summary>
    public static string? Take()
    {
        var v = _fail.Value;
        _fail.Value = null;
        return v;
    }

    /// <summary>Clear any recorded failure at the start of a fresh call.</summary>
    public static void Reset() => _fail.Value = null;
}
