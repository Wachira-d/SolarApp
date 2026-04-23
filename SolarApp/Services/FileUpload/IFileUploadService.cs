namespace SolarApp.Services.FileUpload;

public interface IFileUploadService
{
    /// <summary>
    /// Save an uploaded file to disk under wwwroot/uploads/{category}/{installationId}/.
    /// Returns the URL path (relative to wwwroot) to store in DB.
    /// </summary>
    Task<(bool Success, string UrlOrError)> UploadAsync(IFormFile file, string category, int installationId);

    /// <summary>
    /// Delete a file given its URL path.
    /// </summary>
    bool Delete(string urlPath);

    string AddToCsv(string? existing, string newUrl);
    string RemoveFromCsv(string? existing, string urlToRemove);
}
