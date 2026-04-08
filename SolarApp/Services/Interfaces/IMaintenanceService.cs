using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Services.Interfaces;

public interface IMaintenanceService
{
    Task<List<MaintenanceRecord>> GetByInstallationAsync(int installationId);
    Task<MaintenanceRecord?> GetByIdAsync(int id);
    Task<MaintenanceRecord> CreateAsync(MaintenanceRecord record);
    Task CompleteMaintenanceAsync(int id, DateTime completedDate, string? notes);
    Task<List<ServiceTicket>> GetTicketsByInstallationAsync(int installationId);
    Task<List<ServiceTicket>> GetTicketsByUserAsync(string userId);
    Task<ServiceTicket> CreateTicketAsync(ServiceTicket ticket);
    Task UpdateTicketStatusAsync(int ticketId, TicketStatus status, string? resolution);
}
