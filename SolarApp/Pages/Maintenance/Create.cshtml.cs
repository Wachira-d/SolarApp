using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.Interfaces;

namespace SolarApp.Pages.Maintenance;

[Authorize(Roles = "Admin,Partner")]
public class CreateModel : PageModel
{
    private readonly IMaintenanceService _maintenanceService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ApplicationDbContext _context;

    public CreateModel(
        IMaintenanceService maintenanceService,
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext context)
    {
        _maintenanceService = maintenanceService;
        _userManager = userManager;
        _context = context;
    }

    [BindProperty]
    public int InstallationId { get; set; }

    [BindProperty]
    public MaintenanceType MaintenanceType { get; set; }

    [BindProperty]
    [Required]
    public string Description { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    public DateTime ScheduledDate { get; set; } = SolarApp.Services.SiteTime.Today;

    [BindProperty]
    public decimal? Cost { get; set; }

    public List<SelectListItem> Installations { get; set; } = new();

    public async Task OnGetAsync()
    {
        await LoadInstallationsAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadInstallationsAsync();
            return Page();
        }

        var record = new MaintenanceRecord
        {
            InstallationId = InstallationId,
            MaintenanceType = MaintenanceType,
            Description = Description,
            ScheduledDate = ScheduledDate,
            Cost = Cost,
            Status = MaintenanceStatus.Scheduled
        };

        await _maintenanceService.CreateAsync(record);
        return RedirectToPage("Index");
    }

    private async Task LoadInstallationsAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        var roles = await _userManager.GetRolesAsync(user!);

        var query = _context.Installations.AsQueryable();

        if (roles.Contains(UserRoles.Partner))
        {
            if (user!.PartnerId != null)
                query = query.Where(i => i.PartnerId == user.PartnerId);
            else
                query = query.Where(i => false);
        }

        Installations = await query
            .Select(i => new SelectListItem($"{i.SiteName} ({i.SiteCode})", i.Id.ToString()))
            .ToListAsync();
    }
}
