using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Services.Interfaces;

public interface IInstallationService
{
    Task<List<Installation>> GetAllAsync();
    Task<List<Installation>> GetByCustomerIdAsync(string customerId);
    Task<List<Installation>> GetByPartnerIdAsync(string partnerId);
    Task<Installation?> GetByIdAsync(int id);
    Task<Installation> CreateAsync(Installation installation);
    Task UpdateAsync(Installation installation);
    Task DeleteAsync(int id);
    Task UpdateCleaningAsync(int installationId, DateTime cleaningDate);
    Task<List<Installation>> GetInstallationsNeedingCleaningAsync();
    Task<List<Installation>> GetWarrantyExpiringAsync(int withinDays = 90);
}
