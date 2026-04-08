using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Pages.Installations;

[Authorize(Roles = "Admin,Partner")]
public class CleaningModel : PageModel
{
    private readonly ApplicationDbContext _context;

    public CleaningModel(ApplicationDbContext context)
    {
        _context = context;
    }

    public Installation Installation { get; set; } = null!;
    public List<CleaningRecord> Records { get; set; } = new();

    [BindProperty]
    public CleaningInput Input { get; set; } = new();

    public class CleaningInput
    {
        public int InstallationId { get; set; }
        public DateTime ScheduledDate { get; set; } = DateTime.Today;
        public DateTime? ActualDate { get; set; }
        public string? PerformedByExternal { get; set; }
        public string? Notes { get; set; }
        public bool MarkCompleted { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var inst = await _context.Installations
            .Include(i => i.CleaningRecords.OrderByDescending(c => c.ScheduledDate))
            .Include(i => i.Customer)
            .FirstOrDefaultAsync(i => i.Id == id);

        if (inst == null) return NotFound();

        Installation = inst;
        Records = inst.CleaningRecords.ToList();
        Input.InstallationId = id;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var installation = await _context.Installations.FindAsync(Input.InstallationId);
        if (installation == null) return NotFound();

        var record = new CleaningRecord
        {
            InstallationId = Input.InstallationId,
            ScheduledDate = Input.ScheduledDate,
            PerformedByExternal = Input.PerformedByExternal,
            Notes = Input.Notes
        };

        if (Input.MarkCompleted)
        {
            record.ActualDate = Input.ActualDate ?? DateTime.UtcNow;
            record.Status = CleaningStatus.Completed;

            installation.CompletedCleaningCount++;
            installation.LastCleaningDate = record.ActualDate;

            if (installation.CompletedCleaningCount < installation.TotalCleaningCount)
                installation.NextCleaningDueDate = record.ActualDate.Value.AddMonths(installation.CleaningFrequencyMonths);
            else
                installation.NextCleaningDueDate = null;

            installation.UpdatedAt = DateTime.UtcNow;
        }

        _context.CleaningRecords.Add(record);
        await _context.SaveChangesAsync();

        return RedirectToPage("Cleaning", new { id = Input.InstallationId });
    }

    public async Task<IActionResult> OnPostCompleteAsync(int recordId)
    {
        var record = await _context.CleaningRecords
            .Include(c => c.Installation)
            .FirstOrDefaultAsync(c => c.Id == recordId);

        if (record == null) return NotFound();

        record.Status = CleaningStatus.Completed;
        record.ActualDate = DateTime.UtcNow;
        record.UpdatedAt = DateTime.UtcNow;

        var inst = record.Installation;
        inst.CompletedCleaningCount++;
        inst.LastCleaningDate = record.ActualDate;

        if (inst.CompletedCleaningCount < inst.TotalCleaningCount)
            inst.NextCleaningDueDate = record.ActualDate.Value.AddMonths(inst.CleaningFrequencyMonths);
        else
            inst.NextCleaningDueDate = null;

        await _context.SaveChangesAsync();

        return RedirectToPage("Cleaning", new { id = inst.Id });
    }
}
