using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.FileUpload;
using SolarApp.Services.Interfaces;

namespace SolarApp.Pages.Maintenance;

[Authorize(Roles = "Admin,Partner")]
public class EditModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IMaintenanceService _service;
    private readonly IFileUploadService _uploader;

    public EditModel(ApplicationDbContext context, UserManager<ApplicationUser> userManager,
        IMaintenanceService service, IFileUploadService uploader)
    {
        _context = context;
        _userManager = userManager;
        _service = service;
        _uploader = uploader;
    }

    [BindProperty]
    public MaintenanceEditInput Input { get; set; } = new();

    public MaintenanceRecord Record { get; set; } = null!;
    public List<SelectListItem> Performers { get; set; } = new();

    [TempData] public string? StatusMessage { get; set; }

    public class MaintenanceEditInput
    {
        public int Id { get; set; }
        public MaintenanceType MaintenanceType { get; set; }
        public MaintenanceStatus Status { get; set; }
        public string Description { get; set; } = string.Empty;
        public DateTime ScheduledDate { get; set; }
        public DateTime? CompletedDate { get; set; }
        public decimal? Cost { get; set; }
        public string? PerformedByUserId { get; set; }
        public string? Category { get; set; }
        public string? Location { get; set; }
        public string? Notes { get; set; }
    }

    [BindProperty] public List<IFormFile> BeforePhotoFiles { get; set; } = new();
    [BindProperty] public List<IFormFile> AfterPhotoFiles { get; set; } = new();
    [BindProperty] public List<IFormFile> AttachmentFiles { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var record = await _context.MaintenanceRecords
            .Include(m => m.Installation).ThenInclude(i => i.Customer)
            .Include(m => m.PerformedByUser)
            .FirstOrDefaultAsync(m => m.Id == id);

        if (record == null) return NotFound();

        Record = record;
        Input = new MaintenanceEditInput
        {
            Id = record.Id,
            MaintenanceType = record.MaintenanceType,
            Status = record.Status,
            Description = record.Description,
            ScheduledDate = record.ScheduledDate,
            CompletedDate = record.CompletedDate,
            Cost = record.Cost,
            PerformedByUserId = record.PerformedByUserId,
            Category = record.Category,
            Location = record.Location,
            Notes = record.Notes
        };

        await LoadPerformersAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var record = await _context.MaintenanceRecords
            .Include(m => m.Installation)
            .FirstOrDefaultAsync(m => m.Id == Input.Id);
        if (record == null) return NotFound();

        var wasCompleted = record.Status == MaintenanceStatus.Completed;

        record.MaintenanceType = Input.MaintenanceType;
        record.Status = Input.Status;
        record.Description = Input.Description;
        record.ScheduledDate = Input.ScheduledDate;
        record.CompletedDate = Input.CompletedDate;
        record.Cost = Input.Cost;
        record.PerformedByUserId = string.IsNullOrEmpty(Input.PerformedByUserId) ? null : Input.PerformedByUserId;
        record.Category = Input.Category;
        record.Location = Input.Location;
        record.Notes = Input.Notes;

        // Handle uploaded files
        foreach (var file in BeforePhotoFiles.Where(f => f != null && f.Length > 0))
        {
            var (ok, url) = await _uploader.UploadAsync(file, "maintenance-before", record.InstallationId);
            if (ok) record.BeforePhotos = _uploader.AddToCsv(record.BeforePhotos, url);
        }
        foreach (var file in AfterPhotoFiles.Where(f => f != null && f.Length > 0))
        {
            var (ok, url) = await _uploader.UploadAsync(file, "maintenance-after", record.InstallationId);
            if (ok) record.AfterPhotos = _uploader.AddToCsv(record.AfterPhotos, url);
        }
        foreach (var file in AttachmentFiles.Where(f => f != null && f.Length > 0))
        {
            var (ok, url) = await _uploader.UploadAsync(file, "maintenance-docs", record.InstallationId);
            if (ok) record.AttachmentUrls = _uploader.AddToCsv(record.AttachmentUrls, url);
        }

        // Auto-set CompletedDate when status flips to Completed
        if (Input.Status == MaintenanceStatus.Completed && !wasCompleted)
        {
            record.CompletedDate ??= DateTime.UtcNow;

            // Update cleaning count if it's a panel cleaning
            if (record.MaintenanceType == MaintenanceType.PanelCleaning)
            {
                var inst = record.Installation;
                inst.RecordCompletedCleaning(record.CompletedDate.Value);
            }
        }

        await _context.SaveChangesAsync();
        StatusMessage = "Maintenance record updated";
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostCompleteAsync(int id)
    {
        await _service.CompleteMaintenanceAsync(id, DateTime.UtcNow, null);
        StatusMessage = "Maintenance marked as completed";
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (!await _userManager.IsInRoleAsync(user!, UserRoles.Admin)) return Forbid();

        var record = await _context.MaintenanceRecords.FindAsync(id);
        if (record == null) return NotFound();

        _context.MaintenanceRecords.Remove(record);
        await _context.SaveChangesAsync();
        StatusMessage = "Maintenance record deleted";
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostRemovePhotoAsync(int id, string url, string kind)
    {
        var record = await _context.MaintenanceRecords.FindAsync(id);
        if (record == null) return NotFound();

        switch (kind)
        {
            case "before":
                record.BeforePhotos = _uploader.RemoveFromCsv(record.BeforePhotos, url);
                break;
            case "after":
                record.AfterPhotos = _uploader.RemoveFromCsv(record.AfterPhotos, url);
                break;
            case "attach":
                record.AttachmentUrls = _uploader.RemoveFromCsv(record.AttachmentUrls, url);
                break;
        }
        _uploader.Delete(url);
        await _context.SaveChangesAsync();

        StatusMessage = "File removed";
        return RedirectToPage(new { id });
    }

    private async Task LoadPerformersAsync()
    {
        var adminRoleId = await _context.Roles.Where(r => r.Name == UserRoles.Admin)
            .Select(r => r.Id).FirstOrDefaultAsync();
        var partnerRoleId = await _context.Roles.Where(r => r.Name == UserRoles.Partner)
            .Select(r => r.Id).FirstOrDefaultAsync();

        var allowedUserIds = await _context.UserRoles
            .Where(ur => ur.RoleId == adminRoleId || ur.RoleId == partnerRoleId)
            .Select(ur => ur.UserId).ToListAsync();

        var users = await _context.Users.Where(u => allowedUserIds.Contains(u.Id) && u.IsActive)
            .ToListAsync();

        Performers = users.Select(u => new SelectListItem($"{u.FullName} ({u.Email})", u.Id)).ToList();
    }
}
