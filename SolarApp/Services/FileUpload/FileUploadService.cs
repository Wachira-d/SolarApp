namespace SolarApp.Services.FileUpload;

public class FileUploadService : IFileUploadService
{
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<FileUploadService> _logger;

    private static readonly string[] AllowedExtensions =
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp",       // images
        ".pdf", ".doc", ".docx", ".xls", ".xlsx",        // docs
        ".txt", ".csv"
    };

    // Magic-byte signatures for MIME verification (anti-MIME-spoofing)
    private static readonly Dictionary<string, byte[][]> MagicBytes = new()
    {
        [".jpg"]  = new[] { new byte[] { 0xFF, 0xD8, 0xFF } },
        [".jpeg"] = new[] { new byte[] { 0xFF, 0xD8, 0xFF } },
        [".png"]  = new[] { new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A } },
        [".gif"]  = new[] { System.Text.Encoding.ASCII.GetBytes("GIF87a"),
                            System.Text.Encoding.ASCII.GetBytes("GIF89a") },
        [".webp"] = new[] { System.Text.Encoding.ASCII.GetBytes("RIFF") }, // checks RIFF; full: RIFF....WEBP
        [".pdf"]  = new[] { System.Text.Encoding.ASCII.GetBytes("%PDF-") },
        [".doc"]  = new[] { new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 } },   // OLE2
        [".xls"]  = new[] { new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 } },
        [".docx"] = new[] { new byte[] { 0x50, 0x4B, 0x03, 0x04 } },                            // ZIP
        [".xlsx"] = new[] { new byte[] { 0x50, 0x4B, 0x03, 0x04 } },
        // .txt and .csv have no magic bytes — skip signature check
    };

    private static readonly HashSet<string> ExtensionsWithoutMagic = new() { ".txt", ".csv" };

    private const long MaxFileSize = 10 * 1024 * 1024; // 10 MB

    public FileUploadService(IWebHostEnvironment env, ILogger<FileUploadService> logger)
    {
        _env = env;
        _logger = logger;
    }

    public async Task<(bool Success, string UrlOrError)> UploadAsync(IFormFile file, string category, int installationId, bool stripExif = false)
    {
        if (file == null || file.Length == 0)
            return (false, "No file provided");

        if (file.Length > MaxFileSize)
            return (false, $"File too large ({file.Length / 1024 / 1024} MB). Max {MaxFileSize / 1024 / 1024} MB");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
            return (false, $"File type '{ext}' not allowed. Allowed: {string.Join(", ", AllowedExtensions)}");

        // Magic-byte validation: reject files whose contents don't match the extension
        if (!ExtensionsWithoutMagic.Contains(ext))
        {
            try
            {
                await using var probe = file.OpenReadStream();
                var header = new byte[16];
                var read = await probe.ReadAsync(header, 0, header.Length);
                if (!MatchesAnySignature(header, read, ext))
                {
                    _logger.LogWarning("Magic-byte mismatch for {FileName} (claimed {Ext})", file.FileName, ext);
                    return (false, $"File content does not match extension '{ext}'. Possible MIME spoofing.");
                }
            }
            catch (Exception probeEx)
            {
                _logger.LogWarning(probeEx, "Could not probe file {FileName} for magic bytes", file.FileName);
                return (false, "Could not read file header for validation.");
            }
        }

        try
        {
            var webRoot = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
            var safeCategory = string.Join("_", category.Split(Path.GetInvalidFileNameChars()));
            var dir = Path.Combine(webRoot, "uploads", safeCategory, installationId.ToString());
            Directory.CreateDirectory(dir);

            var fileName = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}{ext}";
            var fullPath = Path.Combine(dir, fileName);

            // Strip EXIF for JPEGs when requested. Other formats are written as-is —
            // PNG/WEBP/GIF do not carry GPS in the way phone cameras embed it in JPEG.
            var shouldStripExif = stripExif && (ext == ".jpg" || ext == ".jpeg");
            if (shouldStripExif)
            {
                await using var inStream = file.OpenReadStream();
                using var ms = new MemoryStream();
                await inStream.CopyToAsync(ms);
                var bytes = ms.ToArray();
                var stripped = JpegExifStripper.Strip(bytes);
                await File.WriteAllBytesAsync(fullPath, stripped);
            }
            else
            {
                await using var stream = new FileStream(fullPath, FileMode.Create);
                await file.CopyToAsync(stream);
            }

            // URL path (forward slashes, relative to wwwroot)
            var urlPath = $"/uploads/{safeCategory}/{installationId}/{fileName}";
            _logger.LogInformation("File uploaded: {Path}", urlPath);

            return (true, urlPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload file");
            return (false, $"Upload failed: {ex.Message}");
        }
    }

    private static bool MatchesAnySignature(byte[] header, int bytesRead, string ext)
    {
        if (!MagicBytes.TryGetValue(ext, out var sigs)) return false;
        foreach (var sig in sigs)
        {
            if (bytesRead < sig.Length) continue;
            bool match = true;
            for (int i = 0; i < sig.Length; i++)
            {
                if (header[i] != sig[i]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }

    public bool Delete(string urlPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(urlPath) || !urlPath.StartsWith("/uploads/"))
                return false;

            var webRoot = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
            var fullPath = Path.Combine(webRoot, urlPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete file {Path}", urlPath);
            return false;
        }
    }

    public string AddToCsv(string? existing, string newUrl)
    {
        if (string.IsNullOrWhiteSpace(existing)) return newUrl;
        return existing + "," + newUrl;
    }

    public string RemoveFromCsv(string? existing, string urlToRemove)
    {
        if (string.IsNullOrWhiteSpace(existing)) return "";
        return string.Join(",",
            existing.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(u => u != urlToRemove));
    }
}
