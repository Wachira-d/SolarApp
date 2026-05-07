namespace SolarApp.Services.FileUpload;

public interface IFileUploadService
{
    /// <summary>
    /// Save an uploaded file to disk under wwwroot/uploads/{category}/{installationId}/.
    /// Returns the URL path (relative to wwwroot) to store in DB.
    /// When stripExif=true and the file is a JPEG, EXIF/GPS metadata is removed before save.
    /// </summary>
    Task<(bool Success, string UrlOrError)> UploadAsync(IFormFile file, string category, int installationId, bool stripExif = false);

    bool Delete(string urlPath);

    string AddToCsv(string? existing, string newUrl);
    string RemoveFromCsv(string? existing, string urlToRemove);
}
