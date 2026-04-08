namespace SolarApp.Models;

public class InverterApiLog
{
    public long Id { get; set; }
    public int InstallationId { get; set; }
    public Installation Installation { get; set; } = null!;

    public string ApiProvider { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public DateTime RequestTime { get; set; } = DateTime.UtcNow;

    public int ResponseStatusCode { get; set; }
    public string? ResponseMessage { get; set; }
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
}
