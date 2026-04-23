using SolarApp.ViewModels;

namespace SolarApp.Services.Interfaces;

public interface IDashboardService
{
    Task<DashboardViewModel> GetDashboardAsync(string? userId, string role, int? filterPartnerId = null);
    Task<ProductionReportViewModel> GetProductionReportAsync(int installationId, DateTime from, DateTime to);
}
