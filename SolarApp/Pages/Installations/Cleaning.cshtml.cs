using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.Authorization;
using SolarApp.Services.FileUpload;

namespace SolarApp.Pages.Installations;

[Authorize]
public class CleaningModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly IFileUploadService _uploader;
    private readonly IInstallationAccessHelper _access;

    public CleaningModel(ApplicationDbContext context, IFileUploadService uploader, IInstallationAccessHelper access)
    {
        _context = context;
        _uploader = uploader;
        _access = access;
    }

    public Installation Installation { get; set; } = null!;
    public List<CleaningRecord> Records { get; set; } = new();

    [BindProperty]
    public CleaningInput Input { get; set; } = new();

    [BindProperty] public List<IFormFile> BeforePhotoFiles { get; set; } = new();
    [BindProperty] public List<IFormFile> AfterPhotoFiles { get; set; } = new();

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
        if (!await _access.CanAccessAsync(id, User)) return Forbid();

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
        if (!await _access.CanAccessAsync(Input.InstallationId, User)) return Forbid();

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

        // Upload photos
        foreach (var file in BeforePhotoFiles.Where(f => f != null && f.Length > 0))
        {
            var (ok, url) = await _uploader.UploadAsync(file, "cleaning-before", Input.InstallationId);
            if (ok) record.BeforePhotos = _uploader.AddToCsv(record.BeforePhotos, url);
        }
        foreach (var file in AfterPhotoFiles.Where(f => f != null && f.Length > 0))
        {
            var (ok, url) = await _uploader.UploadAsync(file, "cleaning-after", Input.InstallationId);
            if (ok) record.AfterPhotos = _uploader.AddToCsv(record.AfterPhotos, url);
        }

        _context.CleaningRecords.Add(record);
        await _context.SaveChangesAsync();

        return RedirectToPage("Cleaning", new { id = Input.InstallationId });
    }

    public async Task<IActionResult> OnPostAddPhotoAsync(int recordId, string kind)
    {
        var record = await _context.CleaningRecords.FindAsync(recordId);
        if (record == null) return NotFound();
        if (!await _access.CanAccessAsync(record.InstallationId, User)) return Forbid();

        var files = kind == "before" ? BeforePhotoFiles : AfterPhotoFiles;
        foreach (var file in files.Where(f => f != null && f.Length > 0))
        {
            var (ok, url) = await _uploader.UploadAsync(file, $"cleaning-{kind}", record.InstallationId);
            if (ok)
            {
                if (kind == "before")
                    record.BeforePhotos = _uploader.AddToCsv(record.BeforePhotos, url);
                else
                    record.AfterPhotos = _uploader.AddToCsv(record.AfterPhotos, url);
            }
        }
        await _context.SaveChangesAsync();
        return RedirectToPage("Cleaning", new { id = record.InstallationId });
    }

    public async Task<IActionResult> OnPostRemovePhotoAsync(int recordId, string url, string kind)
    {
        var record = await _context.CleaningRecords.FindAsync(recordId);
        if (record == null) return NotFound();
        if (!await _access.CanAccessAsync(record.InstallationId, User)) return Forbid();

        if (kind == "before")
            record.BeforePhotos = _uploader.RemoveFromCsv(record.BeforePhotos, url);
        else
            record.AfterPhotos = _uploader.RemoveFromCsv(record.AfterPhotos, url);

        _uploader.Delete(url);
        await _context.SaveChangesAsync();
        return RedirectToPage("Cleaning", new { id = record.InstallationId });
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
