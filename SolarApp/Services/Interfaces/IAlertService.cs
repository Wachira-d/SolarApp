using SolarApp.Models;

namespace SolarApp.Services.Interfaces;

public interface IAlertService
{
    Task<List<SystemAlert>> GetActiveAlertsAsync(int? installationId = null);
    Task<List<SystemAlert>> GetAlertsByInstallationAsync(int installationId);
    Task CreateAlertAsync(SystemAlert alert);
    Task AcknowledgeAlertAsync(int alertId, string userId);
    Task ResolveAlertAsync(int alertId);
    Task<int> GetUnreadAlertCountAsync(string? userId, string role);
}
