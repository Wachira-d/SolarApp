using System.Buffers.Binary;
using System.Text;

namespace SolarApp.Services.InverterApi.Common;

/// <summary>
/// Decoder for the LineChart protobuf payload that Hoymiles' S-Miles
/// Cloud returns from chart endpoints (Content-Type: application/octet-stream).
///
/// Inferred schema (from the production JS bundle that sets
///   request(url, { buffer: "LineChart", data: {…} })):
///
///   message LineChart {
///     repeated string x_axis = 1;   // dates as yyyy-MM-dd strings
///     repeated Series series = 2;
///   }
///   message Series {
///     string name      = 1;          // e.g. "pv_eq", "consumption_eq"
///     repeated float values = 2;     // little-endian fixed32, packed
///   }
///
/// The repeated-string label list and the per-series value list are
/// index-aligned (labels[i] is the X coordinate for series.values[i]).
/// "pv_eq" carries kWh per day for a daily-aggregate endpoint.  We pick
/// it as the production-energy series; "consumption_eq" / "self_eq"
/// are ancillary breakdowns we ignore.
/// </summary>
public static class HoymilesProtobufDecoder
{
    public class ChartData
    {
        public List<string> Labels { get; } = new();
        public Dictionary<string, List<double>> Series { get; } = new();
    }

    public static ChartData? Decode(byte[] data)
    {
        if (data.Length == 0) return null;
        try
        {
            var result = new ChartData();
            int pos = 0;
            while (pos < data.Length)
            {
                if (!TryReadVarint(data, ref pos, out var tag)) break;
                var fieldNumber = (int)(tag >> 3);
                var wireType    = (int)(tag & 0x07);

                if (wireType == 2) // length-delimited
                {
                    if (!TryReadVarint(data, ref pos, out var len)) break;
                    if (pos + (int)len > data.Length) break;
                    var chunk = data.AsSpan(pos, (int)len);
                    pos += (int)len;

                    if (fieldNumber == 1)
                    {
                        // x_axis label (UTF-8 string)
                        result.Labels.Add(Encoding.UTF8.GetString(chunk));
                    }
                    else if (fieldNumber == 2)
                    {
                        // Nested Series message
                        var series = DecodeSeries(chunk);
                        if (series is { Name: not null })
                            result.Series[series.Value.Name] = series.Value.Values;
                    }
                    // any other field number: ignore
                }
                else if (wireType == 0) // varint, skip
                {
                    TryReadVarint(data, ref pos, out _);
                }
                else if (wireType == 5) // fixed32, skip 4 bytes
                {
                    pos += 4;
                }
                else if (wireType == 1) // fixed64, skip 8 bytes
                {
                    pos += 8;
                }
                else
                {
                    break; // unknown wire type → bail
                }
            }
            return result.Labels.Count == 0 && result.Series.Count == 0 ? null : result;
        }
        catch
        {
            return null;
        }
    }

    private static (string Name, List<double> Values)? DecodeSeries(ReadOnlySpan<byte> data)
    {
        string? name = null;
        var values = new List<double>();
        int pos = 0;
        while (pos < data.Length)
        {
            if (!TryReadVarintSpan(data, ref pos, out var tag)) break;
            var fieldNumber = (int)(tag >> 3);
            var wireType    = (int)(tag & 0x07);

            if (wireType == 2)
            {
                if (!TryReadVarintSpan(data, ref pos, out var len)) break;
                if (pos + (int)len > data.Length) break;
                var chunk = data.Slice(pos, (int)len);
                pos += (int)len;

                if (fieldNumber == 1)
                {
                    name = Encoding.UTF8.GetString(chunk);
                }
                else if (fieldNumber == 2)
                {
                    // Packed-repeated fixed32 floats
                    int p = 0;
                    while (p + 4 <= chunk.Length)
                    {
                        var f = BinaryPrimitives.ReadSingleLittleEndian(chunk.Slice(p, 4));
                        values.Add(f);
                        p += 4;
                    }
                }
            }
            else if (wireType == 5)
            {
                // Single fixed32 — repeated NOT packed.  Append as one value.
                if (pos + 4 > data.Length) break;
                if (fieldNumber == 2)
                {
                    values.Add(BinaryPrimitives.ReadSingleLittleEndian(data.Slice(pos, 4)));
                }
                pos += 4;
            }
            else if (wireType == 0)
            {
                TryReadVarintSpan(data, ref pos, out _);
            }
            else if (wireType == 1)
            {
                pos += 8;
            }
            else
            {
                break;
            }
        }
        if (name == null) return null;
        return (name, values);
    }

    private static bool TryReadVarint(byte[] buf, ref int pos, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (pos < buf.Length)
        {
            byte b = buf[pos++];
            value |= ((ulong)(b & 0x7f)) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
            if (shift > 64) return false;
        }
        return false;
    }

    private static bool TryReadVarintSpan(ReadOnlySpan<byte> buf, ref int pos, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (pos < buf.Length)
        {
            byte b = buf[pos++];
            value |= ((ulong)(b & 0x7f)) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
            if (shift > 64) return false;
        }
        return false;
    }
}
