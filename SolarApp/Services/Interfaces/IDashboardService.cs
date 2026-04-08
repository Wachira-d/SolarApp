using SolarApp.ViewModels;

namespace SolarApp.Services.Interfaces;

public interface IDashboardService
{
    Task<DashboardViewModel> GetDashboardAsync(string? userId, string role);
    Task<ProductionReportViewModel> GetProductionReportAsync(int installationId, DateTime from, DateTime to);
}
