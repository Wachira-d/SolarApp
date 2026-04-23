using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Pages.Installations;

[Authorize(Roles = "Admin,Partner")]
public class AddEquipmentModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;

    public AddEquipmentModel(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    [BindProperty]
    public EquipmentInput Input { get; set; } = new();

    public Installation Installation { get; set; } = null!;

    public class EquipmentInput
    {
        [Required]
        public EquipmentType EquipmentType { get; set; }

        [Required]
        public string Brand { get; set; } = string.Empty;

        [Required]
        public string Model { get; set; } = string.Empty;

        public string? SerialNumber { get; set; }

        [Required]
        [Range(1, 10000)]
        public int Quantity { get; set; } = 1;

        // Power/capacity per unit
        public decimal? RatingKw { get; set; }    // Inverter (kW), Battery (kWh as kW equivalent)
        public int? RatingWatts { get; set; }     // Panel (W)

        [Required]
        [Range(1, 30)]
        public int WarrantyYears { get; set; } = 10;

        [Required]
        public DateTime InstalledDate { get; set; } = DateTime.Today;

        public string? Notes { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(int installationId)
    {
        var installation = await _context.Installations
            .Include(i => i.Customer)
            .FirstOrDefaultAsync(i => i.Id == installationId);

        if (installation == null)
            return NotFound();

        var user = await _userManager.GetUserAsync(User);
        var roles = await _userManager.GetRolesAsync(user!);

        if (!roles.Contains(UserRoles.Admin))
        {
            if (roles.Contains(UserRoles.Partner))
            {
                if (user!.PartnerId == null || installation.PartnerId != user.PartnerId)
                    return Forbid();
            }
        }

        Installation = installation;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int installationId)
    {
        var installation = await _context.Installations
            .FirstOrDefaultAsync(i => i.Id == installationId);

        if (installation == null)
            return NotFound();

        var user = await _userManager.GetUserAsync(User);
        var roles = await _userManager.GetRolesAsync(user!);

        if (!roles.Contains(UserRoles.Admin))
        {
            if (roles.Contains(UserRoles.Partner))
            {
                if (user!.PartnerId == null || installation.PartnerId != user.PartnerId)
                    return Forbid();
            }
        }

        if (!ModelState.IsValid)
        {
            Installation = installation;
            return Page();
        }

        var equipment = new Equipment
        {
            InstallationId = installationId,
            EquipmentType = Input.EquipmentType,
            Brand = Input.Brand,
            Model = Input.Model,
            SerialNumber = Input.SerialNumber,
            Quantity = Input.Quantity,
            RatingKw = Input.RatingKw,
            RatingWatts = Input.RatingWatts,
            WarrantyYears = Input.WarrantyYears,
            InstalledDate = Input.InstalledDate,
            WarrantyExpiryDate = Input.InstalledDate.AddYears(Input.WarrantyYears),
            IsActive = true,
            Notes = Input.Notes
        };

        _context.Equipment.Add(equipment);
        await _context.SaveChangesAsync();

        return RedirectToPage("Detail", new { id = installationId });
    }
}
