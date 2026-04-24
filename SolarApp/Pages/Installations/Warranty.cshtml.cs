using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.Authorization;

namespace SolarApp.Pages.Installations;

[Authorize(Roles = "Admin,Partner")]
public class WarrantyModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly IInstallationAccessHelper _access;

    public WarrantyModel(ApplicationDbContext context, IInstallationAccessHelper access)
    {
        _context = context;
        _access = access;
    }

    public Installation Installation { get; set; } = null!;
    public List<WarrantyRecord> Records { get; set; } = new();
    public List<SelectListItem> EquipmentOptions { get; set; } = new();

    [BindProperty]
    public WarrantyInput Input { get; set; } = new();

    public class WarrantyInput
    {
        public int InstallationId { get; set; }
        public int? EquipmentId { get; set; }
        public WarrantyType WarrantyType { get; set; }
        public string ProviderName { get; set; } = string.Empty;
        public DateTime WarrantyStartDate { get; set; } = DateTime.Today;
        public int WarrantyYears { get; set; } = 10;
        public string? TermsDescription { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (!await _access.CanAccessAsync(id, User)) return Forbid();

        var inst = await _context.Installations
            .Include(i => i.WarrantyRecords.OrderByDescending(w => w.WarrantyEndDate))
                .ThenInclude(w => w.Equipment)
            .Include(i => i.Equipment)
            .Include(i => i.Customer)
            .FirstOrDefaultAsync(i => i.Id == id);

        if (inst == null) return NotFound();

        Installation = inst;
        Records = inst.WarrantyRecords.ToList();
        Input.InstallationId = id;
        LoadEquipmentOptions(inst);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await _access.CanAccessAsync(Input.InstallationId, User)) return Forbid();

        if (string.IsNullOrWhiteSpace(Input.ProviderName))
        {
            ModelState.AddModelError("Input.ProviderName", "Provider name is required");
        }
        if (Input.WarrantyYears < 1 || Input.WarrantyYears > 50)
        {
            ModelState.AddModelError("Input.WarrantyYears", "Warranty years must be 1-50");
        }

        var inst = await _context.Installations.Include(i => i.Equipment)
            .FirstOrDefaultAsync(i => i.Id == Input.InstallationId);
        if (inst == null) return NotFound();

        if (!ModelState.IsValid)
        {
            Installation = inst;
            Records = await _context.WarrantyRecords.Where(w => w.InstallationId == inst.Id)
                .OrderByDescending(w => w.WarrantyEndDate).ToListAsync();
            LoadEquipmentOptions(inst);
            return Page();
        }

        _context.WarrantyRecords.Add(new WarrantyRecord
        {
            InstallationId = Input.InstallationId,
            EquipmentId = Input.EquipmentId,
            WarrantyType = Input.WarrantyType,
            ProviderName = Input.ProviderName,
            WarrantyStartDate = Input.WarrantyStartDate,
            WarrantyEndDate = Input.WarrantyStartDate.AddYears(Input.WarrantyYears),
            TermsDescription = Input.TermsDescription
        });

        await _context.SaveChangesAsync();
        return RedirectToPage("Warranty", new { id = Input.InstallationId });
    }

    private void LoadEquipmentOptions(Installation inst)
    {
        EquipmentOptions = inst.Equipment
            .Select(e => new SelectListItem($"{e.EquipmentType} - {e.Brand} {e.Model}", e.Id.ToString()))
            .ToList();
    }
}
