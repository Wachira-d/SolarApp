using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.Authorization;
using SolarApp.Services.FileUpload;

namespace SolarApp.Pages.WorkOrders;

[Authorize]
public class PhotosModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IFileUploadService _uploader;
    private readonly IWorkOrderAccessHelper _access;

    public PhotosModel(
        ApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        IFileUploadService uploader,
        IWorkOrderAccessHelper access)
    {
        _context = context;
        _userManager = userManager;
        _uploader = uploader;
        _access = access;
    }

    public WorkOrder Order { get; set; } = null!;
    public List<IGrouping<PhotoCategory, WorkOrderPhoto>> PhotosByCategory { get; set; } = new();
    [TempData] public string? StatusMessage { get; set; }

    [BindProperty] public PhotoCategory UploadCategory { get; set; }
    [BindProperty] public PhotoTiming UploadTiming { get; set; } = PhotoTiming.Before;
    [BindProperty] public string? UploadDescription { get; set; }
    [BindProperty] public List<IFormFile> PhotoFiles { get; set; } = new();

    // Suggested categories grouped for the UI
    public static readonly (string Group, PhotoCategory[] Items)[] CategoryGroups = new[]
    {
        ("ภาพรวมไซต์", new[] { PhotoCategory.SiteOverview, PhotoCategory.RoofOverview }),
        ("แผงโซลาร์", new[] { PhotoCategory.PanelArrayOverall, PhotoCategory.PanelCloseup, PhotoCategory.PanelDamage, PhotoCategory.PanelStringLabel }),
        ("Inverter", new[] { PhotoCategory.InverterZone, PhotoCategory.InverterDisplay, PhotoCategory.InverterWiring, PhotoCategory.InverterNameplate }),
        ("ไฟฟ้า", new[] { PhotoCategory.ACBreaker, PhotoCategory.DCIsolator, PhotoCategory.ElectricalPanel, PhotoCategory.Meter, PhotoCategory.WiringConduit }),
        ("Thermal Scan", new[] { PhotoCategory.ThermalPanelOverall, PhotoCategory.ThermalHotspot, PhotoCategory.ThermalInverter, PhotoCategory.ThermalConnection }),
        ("Battery", new[] { PhotoCategory.BatteryZone, PhotoCategory.BatteryDisplay }),
        ("โครงสร้าง", new[] { PhotoCategory.MountingStructure, PhotoCategory.RoofPenetration }),
        ("ล้างแผง", new[] { PhotoCategory.PanelBeforeCleaning, PhotoCategory.PanelAfterCleaning, PhotoCategory.DrainageGutter }),
        ("เอกสาร", new[] { PhotoCategory.WorkPermit, PhotoCategory.SafetySign, PhotoCategory.MeterReading, PhotoCategory.CustomerSignoff }),
        ("อื่นๆ", new[] { PhotoCategory.Other }),
    };

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var wo = await _context.WorkOrders
            .Include(w => w.Photos.OrderBy(p => p.Category).ThenBy(p => p.Timing).ThenBy(p => p.SortOrder))
            .Include(w => w.Installation)
            .FirstOrDefaultAsync(w => w.Id == id);
        if (wo == null) return NotFound();
        if (!await _access.CanViewAsync(wo, User)) return Forbid();

        Order = wo;
        PhotosByCategory = wo.Photos.GroupBy(p => p.Category).ToList();
        return Page();
    }

    public async Task<IActionResult> OnPostUploadAsync(int id)
    {
        var wo = await _context.WorkOrders.FindAsync(id);
        if (wo == null) return NotFound();
        if (!await _access.CanEditAsync(wo, User)) return Forbid();

        var user = await _userManager.GetUserAsync(User);

        int uploaded = 0;
        foreach (var file in PhotoFiles.Where(f => f != null && f.Length > 0).Take(20))
        {
            var (ok, urlOrErr) = await _uploader.UploadAsync(file, $"wo-{id}-photos", id, stripExif: true);
            if (!ok)
            {
                StatusMessage = $"Upload failed: {urlOrErr}";
                continue;
            }

            _context.WorkOrderPhotos.Add(new WorkOrderPhoto
            {
                WorkOrderId = id,
                Category = UploadCategory,
                Timing = UploadTiming,
                Description = UploadDescription,
                FilePath = urlOrErr,
                ContentType = file.ContentType,
                FileSizeBytes = file.Length,
                UploadedByUserId = user?.Id,
                SortOrder = uploaded
            });
            uploaded++;
        }

        if (uploaded > 0)
        {
            await _context.SaveChangesAsync();
            StatusMessage = $"Uploaded {uploaded} photo(s) to {UploadCategory}";
        }
        return RedirectToPage("Photos", new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id, int photoId)
    {
        var wo = await _context.WorkOrders.FindAsync(id);
        if (wo == null) return NotFound();
        if (!await _access.CanEditAsync(wo, User)) return Forbid();

        var photo = await _context.WorkOrderPhotos.FindAsync(photoId);
        if (photo == null || photo.WorkOrderId != id) return NotFound();
        _uploader.Delete(photo.FilePath);
        _context.WorkOrderPhotos.Remove(photo);
        await _context.SaveChangesAsync();
        StatusMessage = "Photo deleted";
        return RedirectToPage("Photos", new { id });
    }
}
