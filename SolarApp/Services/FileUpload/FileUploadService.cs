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

    private const long MaxFileSize = 10 * 1024 * 1024; // 10 MB

    public FileUploadService(IWebHostEnvironment env, ILogger<FileUploadService> logger)
    {
        _env = env;
        _logger = logger;
    }

    public async Task<(bool Success, string UrlOrError)> UploadAsync(IFormFile file, string category, int installationId)
    {
        if (file == null || file.Length == 0)
            return (false, "No file provided");

        if (file.Length > MaxFileSize)
            return (false, $"File too large ({file.Length / 1024 / 1024} MB). Max {MaxFileSize / 1024 / 1024} MB");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
            return (false, $"File type '{ext}' not allowed. Allowed: {string.Join(", ", AllowedExtensions)}");

        try
        {
            var webRoot = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
            var safeCategory = string.Join("_", category.Split(Path.GetInvalidFileNameChars()));
            var dir = Path.Combine(webRoot, "uploads", safeCategory, installationId.ToString());
            Directory.CreateDirectory(dir);

            var fileName = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}{ext}";
            var fullPath = Path.Combine(dir, fileName);

            await using var stream = new FileStream(fullPath, FileMode.Create);
            await file.CopyToAsync(stream);

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
