namespace SolarApp.Services.Api;

/// <summary>
/// Unified API response envelope used by all JSON endpoints.
/// Stops the proliferation of ad-hoc anonymous objects with slightly
/// different field names (success vs ok, message vs error vs detail, etc.).
///
/// Schema:
///   {
///     "success": bool,        // always present
///     "message": string?,     // human-readable (Thai or English)
///     "code":    string?,     // machine-readable error code (e.g. "ACCESS_DENIED")
///     "data":    object?      // payload on success
///   }
///
/// Frontend can rely on `success` being a bool to drive UI state.
/// </summary>
public class ApiResponse
{
    public bool Success { get; init; }
    public string? Message { get; init; }
    public string? Code { get; init; }
    public object? Data { get; init; }

    public static ApiResponse Ok(string? message = null, object? data = null)
        => new() { Success = true, Message = message, Data = data };

    public static ApiResponse Fail(string message, string? code = null)
        => new() { Success = false, Message = message, Code = code };

    // Common, named error factories for consistency across endpoints
    public static ApiResponse AccessDenied(string message = "Access denied")
        => Fail(message, "ACCESS_DENIED");

    public static ApiResponse NotFound(string message = "Resource not found")
        => Fail(message, "NOT_FOUND");

    public static ApiResponse ValidationError(string message)
        => Fail(message, "VALIDATION_ERROR");

    public static ApiResponse Unauthorized(string message = "Authentication required")
        => Fail(message, "UNAUTHORIZED");
}
