using System.Globalization;
using System.Text.Json;

namespace SolarApp.Services.InverterApi.Common;

/// <summary>
/// Safe JSON extraction utilities.
///
/// System.Text.Json methods like TryGetDecimal/TryGetInt32/GetString
/// THROW InvalidOperationException if the element's ValueKind doesn't match.
/// These helpers check ValueKind first and return default/null if mismatched.
///
/// Also handles common API quirks:
/// - Numbers encoded as strings ("3.14" instead of 3.14)
/// - Booleans encoded as 0/1 or "true"/"false"
/// - Null/Undefined properties
/// - Wrong root type (Array when expecting Object, etc.)
/// </summary>
public static class SafeJson
{
    // ─────────────────────────────────────────────
    //  Property extractors (from parent element)
    // ─────────────────────────────────────────────

    public static string? GetString(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(prop, out var v)) return null;
        return ToString(v);
    }

    public static decimal GetDecimal(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return 0;
        if (!el.TryGetProperty(prop, out var v)) return 0;
        return ToDecimal(v) ?? 0;
    }

    public static decimal? GetNullableDecimal(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(prop, out var v)) return null;
        return ToDecimal(v);
    }

    public static int GetInt(JsonElement el, string prop, int defaultVal = 0)
    {
        if (el.ValueKind != JsonValueKind.Object) return defaultVal;
        if (!el.TryGetProperty(prop, out var v)) return defaultVal;
        return ToInt(v) ?? defaultVal;
    }

    public static int? GetNullableInt(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(prop, out var v)) return null;
        return ToInt(v);
    }

    public static long? GetNullableLong(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(prop, out var v)) return null;
        return ToLong(v);
    }

    public static bool GetBool(JsonElement el, string prop, bool defaultVal = false)
    {
        if (el.ValueKind != JsonValueKind.Object) return defaultVal;
        if (!el.TryGetProperty(prop, out var v)) return defaultVal;
        return ToBool(v) ?? defaultVal;
    }

    public static DateTime? GetDateTime(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(prop, out var v)) return null;
        return ToDateTime(v);
    }

    /// <summary>
    /// Navigate nested JSON path safely.
    /// Returns null if any step missing/wrong-type.
    /// Example: TryPath(root, "data", "page", "records") to get root.data.page.records
    /// </summary>
    public static JsonElement? TryPath(JsonElement root, params string[] path)
    {
        var cur = root;
        foreach (var key in path)
        {
            if (cur.ValueKind != JsonValueKind.Object) return null;
            if (!cur.TryGetProperty(key, out var next)) return null;
            cur = next;
        }
        return cur;
    }

    /// <summary>
    /// Get the first item from an array, or null if path invalid / array empty.
    /// </summary>
    public static JsonElement? FirstArrayItem(JsonElement root, params string[] path)
    {
        var target = TryPath(root, path);
        if (target == null || target.Value.ValueKind != JsonValueKind.Array) return null;
        var first = target.Value.EnumerateArray().FirstOrDefault();
        return first.ValueKind == JsonValueKind.Undefined ? null : first;
    }

    /// <summary>
    /// Safely enumerate an array. Returns empty sequence if not an array.
    /// </summary>
    public static IEnumerable<JsonElement> EnumerateArray(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Array) return Enumerable.Empty<JsonElement>();
        return el.EnumerateArray();
    }

    // ─────────────────────────────────────────────
    //  Value converters (from an element directly)
    // ─────────────────────────────────────────────

    public static string? ToString(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.ToString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null
    };

    public static decimal? ToDecimal(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String &&
            decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d2))
            return d2;
        return null;
    }

    public static int? ToInt(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.True) return 1;
        if (v.ValueKind == JsonValueKind.False) return 0;
        if (v.ValueKind == JsonValueKind.String &&
            int.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var n2))
            return n2;
        return null;
    }

    public static long? ToLong(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String &&
            long.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var n2))
            return n2;
        return null;
    }

    public static bool? ToBool(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => v.TryGetInt32(out var n) && n != 0,
        JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b :
                                int.TryParse(v.GetString(), out var n2) ? (bool?)(n2 != 0) : null,
        _ => null
    };

    public static DateTime? ToDateTime(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            if (!string.IsNullOrEmpty(s) && DateTime.TryParse(s, out var dt)) return dt;
        }
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var ms))
        {
            // Heuristic: > 10^12 is likely milliseconds, smaller is seconds
            return ms > 10_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
                : DateTimeOffset.FromUnixTimeSeconds(ms).UtcDateTime;
        }
        return null;
    }

    // ─────────────────────────────────────────────
    //  HTTP response parsing
    // ─────────────────────────────────────────────

    /// <summary>
    /// Parse response body to JsonElement safely. Returns null on any error.
    /// </summary>
    public static async Task<JsonElement?> ReadAsync(HttpResponseMessage response)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(body)) return null;
            return JsonDocument.Parse(body).RootElement;
        }
        catch
        {
            return null;
        }
    }

    public static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";
}
