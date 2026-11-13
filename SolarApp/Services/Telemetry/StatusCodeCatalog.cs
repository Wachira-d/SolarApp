using SolarApp.Models.Enums;

namespace SolarApp.Services.Telemetry;

/// <summary>
/// Decodes equipment status and alarm codes read over Modbus into Thai text
/// (DESIGN.md §7.6). Tables are per brand and per kind ("status" running
/// state, "alarm" fault code). Unknown codes come back as the number with
/// "ไม่ทราบความหมาย" — never as an empty string.
/// </summary>
public static partial class StatusCodeCatalog
{
    public readonly record struct Decoded(string Text, AlertSeverity Severity, bool IsKnown);

    private static readonly Dictionary<(InverterBrand Brand, string Kind), Dictionary<long, (string Text, AlertSeverity Severity)>> Tables = new();

    /// <summary>Register a brand table. Called from the brand partial files' static constructors.</summary>
    internal static void Register(InverterBrand brand, string kind, Dictionary<long, (string, AlertSeverity)> table) =>
        Tables[(brand, kind)] = table;

    static StatusCodeCatalog()
    {
        RegisterBrandTables();
    }

    /// <summary>Implemented in StatusCodeCatalog.Brands.cs.</summary>
    static partial void RegisterBrandTables();

    /// <param name="kind">"status" or "alarm".</param>
    public static Decoded Decode(InverterBrand brand, string kind, long code)
    {
        if (Tables.TryGetValue((brand, kind), out var table) && table.TryGetValue(code, out var hit))
            return new Decoded(hit.Text, hit.Severity, true);

        // Many alarm registers are bit fields: report each known bit.
        if (kind == "alarm" && code > 0 && Tables.TryGetValue((brand, "alarm-bits"), out var bits))
        {
            var names = new List<string>();
            var worst = AlertSeverity.Low;
            for (var bit = 0; bit < 63; bit++)
            {
                if ((code & (1L << bit)) == 0) continue;
                if (bits.TryGetValue(bit, out var b))
                {
                    names.Add(b.Text);
                    if (b.Severity < worst) worst = b.Severity;   // Critical = 1 is the worst
                }
                else names.Add($"bit {bit}");
            }
            if (names.Count > 0) return new Decoded(string.Join(", ", names), worst, true);
        }

        if (kind == "alarm" && code == 0)
            return new Decoded("ไม่มี alarm", AlertSeverity.Low, true);

        return new Decoded($"รหัส {code} (ไม่ทราบความหมาย)", kind == "alarm" ? AlertSeverity.Medium : AlertSeverity.Low, false);
    }
}
