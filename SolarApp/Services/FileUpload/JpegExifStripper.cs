namespace SolarApp.Services.FileUpload;

/// <summary>
/// Removes EXIF (APP1) and FlashPix (APP2) metadata segments from a JPEG byte array,
/// which is what carries GPS coordinates from phone cameras.
/// On any parse error, returns the original bytes unchanged so we never lose the photo.
/// </summary>
internal static class JpegExifStripper
{
    private const byte MarkerPrefix = 0xFF;
    private const byte SOI = 0xD8;          // Start of image
    private const byte SOS = 0xDA;          // Start of scan (after this is compressed data)
    private const byte EOI = 0xD9;          // End of image
    private const byte APP1 = 0xE1;         // EXIF/XMP — strip
    private const byte APP2 = 0xE2;         // FlashPix — strip

    public static byte[] Strip(byte[] data)
    {
        if (data == null || data.Length < 4) return data ?? Array.Empty<byte>();
        if (data[0] != MarkerPrefix || data[1] != SOI) return data; // not a JPEG

        try
        {
            using var ms = new MemoryStream(data.Length);
            ms.WriteByte(MarkerPrefix);
            ms.WriteByte(SOI);

            int i = 2;
            while (i < data.Length - 1)
            {
                if (data[i] != MarkerPrefix)
                {
                    // Malformed — bail safely
                    return data;
                }

                // Skip fill bytes (0xFF padding)
                while (i < data.Length && data[i] == MarkerPrefix) i++;
                if (i >= data.Length) break;

                byte marker = data[i++];

                // Markers without payload
                if (marker == SOI || marker == EOI || (marker >= 0xD0 && marker <= 0xD7))
                {
                    ms.WriteByte(MarkerPrefix);
                    ms.WriteByte(marker);
                    continue;
                }

                if (i + 1 >= data.Length) return data;
                int segLen = (data[i] << 8) | data[i + 1];
                if (segLen < 2 || i + segLen > data.Length) return data;

                if (marker == APP1 || marker == APP2)
                {
                    // Skip this segment entirely
                    i += segLen;
                    continue;
                }

                // Copy marker + segment as-is
                ms.WriteByte(MarkerPrefix);
                ms.WriteByte(marker);
                ms.Write(data, i, segLen);
                i += segLen;

                if (marker == SOS)
                {
                    // After SOS the rest is compressed image data + EOI; copy verbatim
                    int rest = data.Length - i;
                    if (rest > 0) ms.Write(data, i, rest);
                    return ms.ToArray();
                }
            }

            return ms.ToArray();
        }
        catch
        {
            return data;
        }
    }
}
