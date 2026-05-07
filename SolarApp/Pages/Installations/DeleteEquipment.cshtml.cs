using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;

namespace SolarApp.Pages.Installations;

[Authorize(Roles = "Admin,Partner")]
public class DeleteEquipmentModel : PageModel
{
    private readonly ApplicationDbContext _context;

    public DeleteEquipmentModel(ApplicationDbContext context)
    {
        _context = context;
    }

    public Equipment Equipment { get; set; } = null!;

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var equipment = await _context.Equipment
            .Include(e => e.Installation)
            .FirstOrDefaultAsync(e => e.Id == id);

        if (equipment == null)
            return NotFound();

        Equipment = equipment;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        var equipment = await _context.Equipment
            .FirstOrDefaultAsync(e => e.Id == id);

        if (equipment == null)
            return NotFound();

        var installationId = equipment.InstallationId;

        _context.Equipment.Remove(equipment);
        await _context.SaveChangesAsync();

        return RedirectToPage("Detail", new { id = installationId });
    }
}
