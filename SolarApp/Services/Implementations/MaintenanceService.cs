using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.Interfaces;

namespace SolarApp.Services.Implementations;

public class MaintenanceService : IMaintenanceService
{
    private readonly ApplicationDbContext _context;

    public MaintenanceService(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<MaintenanceRecord>> GetByInstallationAsync(int installationId)
    {
        return await _context.MaintenanceRecords
            .Include(m => m.PerformedByUser)
            .Where(m => m.InstallationId == installationId)
            .OrderByDescending(m => m.ScheduledDate)
            .ToListAsync();
    }

    public async Task<MaintenanceRecord?> GetByIdAsync(int id)
    {
        return await _context.MaintenanceRecords
            .Include(m => m.Installation)
            .Include(m => m.PerformedByUser)
            .FirstOrDefaultAsync(m => m.Id == id);
    }

    public async Task<MaintenanceRecord> CreateAsync(MaintenanceRecord record)
    {
        record.CreatedAt = DateTime.UtcNow;
        _context.MaintenanceRecords.Add(record);
        await _context.SaveChangesAsync();
        return record;
    }

    public async Task CompleteMaintenanceAsync(int id, DateTime completedDate, string? notes)
    {
        var record = await _context.MaintenanceRecords
            .Include(m => m.Installation)
            .FirstOrDefaultAsync(m => m.Id == id);

        if (record == null) return;

        record.Status = MaintenanceStatus.Completed;
        record.CompletedDate = completedDate;
        if (notes != null) record.Notes = notes;

        // If it's a panel cleaning, update the installation's cleaning count
        if (record.MaintenanceType == MaintenanceType.PanelCleaning)
        {
            var installation = record.Installation;
            installation.RecordCompletedCleaning(completedDate);
        }

        await _context.SaveChangesAsync();
    }

    public async Task<List<ServiceTicket>> GetTicketsByInstallationAsync(int installationId)
    {
        return await _context.ServiceTickets
            .AsNoTracking()
            .Include(t => t.ReportedByUser)
            .Include(t => t.AssignedToUser)
            .Where(t => t.InstallationId == installationId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
    }

    public async Task<List<ServiceTicket>> GetTicketsByUserAsync(string userId)
    {
        return await _context.ServiceTickets
            .Include(t => t.Installation)
            .Include(t => t.ReportedByUser)
            .Where(t => t.ReportedByUserId == userId || t.AssignedToUserId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
    }

    public async Task<ServiceTicket> CreateTicketAsync(ServiceTicket ticket)
    {
        // Generate ticket number
        var count = await _context.ServiceTickets.CountAsync();
        ticket.TicketNumber = $"TK-{DateTime.UtcNow:yyyyMM}-{(count + 1):D4}";
        ticket.CreatedAt = DateTime.UtcNow;
        ticket.UpdatedAt = DateTime.UtcNow;

        _context.ServiceTickets.Add(ticket);
        await _context.SaveChangesAsync();
        return ticket;
    }

    public async Task UpdateTicketStatusAsync(int ticketId, TicketStatus status, string? resolution)
    {
        var ticket = await _context.ServiceTickets.FindAsync(ticketId);
        if (ticket == null) return;

        ticket.Status = status;
        ticket.UpdatedAt = DateTime.UtcNow;

        if (status == TicketStatus.Resolved || status == TicketStatus.Closed)
        {
            ticket.ResolvedAt = DateTime.UtcNow;
            ticket.Resolution = resolution;
        }

        await _context.SaveChangesAsync();
    }
}
