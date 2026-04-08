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

namespace SolarApp.Pages.Installations;

[Authorize(Roles = "Admin,Partner")]
public class CreateModel : PageModel
{
    private readonly IInstallationService _installationService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ApplicationDbContext _context;

    public CreateModel(
        IInstallationService installationService,
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext context)
    {
        _installationService = installationService;
        _userManager = userManager;
        _context = context;
    }

    [BindProperty]
    public InstallationInput Input { get; set; } = new();

    public List<SelectListItem> Customers { get; set; } = new();
    public List<SelectListItem> Partners { get; set; } = new();

    public class InstallationInput
    {
        [Required] public string SiteName { get; set; } = string.Empty;
        [Required] public string Address { get; set; } = string.Empty;
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }

        [Required] public string CustomerId { get; set; } = string.Empty;
        public string? PartnerId { get; set; }

        [Required] public InverterBrand InverterBrand { get; set; }
        public string? CloudApiKey { get; set; }
        public string? CloudApiSecret { get; set; }
        public string? CloudUsername { get; set; }
        public string? CloudPassword { get; set; }
        public string? PlantId { get; set; }

        [Required] [Range(0.1, 10000)] public decimal SystemCapacityKw { get; set; }
        [Required] [Range(1, 10000)] public int NumberOfPanels { get; set; }
        [Required] [Range(100, 1000)] public int PanelWattage { get; set; }

        [Required] public DateTime InstallationDate { get; set; } = DateTime.Today;
        public DateTime? CommissionDate { get; set; }

        [Required] [Range(1, 30)] public int WarrantyYears { get; set; } = 10;
        [Range(0, 100)] public int TotalCleaningCount { get; set; }
        public decimal ElectricityRatePerKwh { get; set; } = 4.0m;

        public string? Notes { get; set; }
    }

    public async Task OnGetAsync()
    {
        await LoadDropdownsAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadDropdownsAsync();
            return Page();
        }

        var installation = new Installation
        {
            SiteName = Input.SiteName,
            Address = Input.Address,
            Latitude = Input.Latitude,
            Longitude = Input.Longitude,
            CustomerId = Input.CustomerId,
            PartnerId = Input.PartnerId,
            InverterBrand = Input.InverterBrand,
            CloudApiKey = Input.CloudApiKey,
            CloudApiSecret = Input.CloudApiSecret,
            CloudUsername = Input.CloudUsername,
            CloudPassword = Input.CloudPassword,
            PlantId = Input.PlantId,
            SystemCapacityKw = Input.SystemCapacityKw,
            NumberOfPanels = Input.NumberOfPanels,
            PanelWattage = Input.PanelWattage,
            InstallationDate = Input.InstallationDate,
            CommissionDate = Input.CommissionDate,
            WarrantyYears = Input.WarrantyYears,
            TotalCleaningCount = Input.TotalCleaningCount,
            ElectricityRatePerKwh = Input.ElectricityRatePerKwh,
            Notes = Input.Notes
        };

        if (installation.TotalCleaningCount > 0)
        {
            installation.NextCleaningDueDate = installation.InstallationDate.AddMonths(6);
        }

        await _installationService.CreateAsync(installation);
        return RedirectToPage("Index");
    }

    private async Task LoadDropdownsAsync()
    {
        var customerRole = await _context.Roles.FirstOrDefaultAsync(r => r.Name == UserRoles.Customer);
        var partnerRole = await _context.Roles.FirstOrDefaultAsync(r => r.Name == UserRoles.Partner);

        if (customerRole != null)
        {
            var customerIds = await _context.UserRoles
                .Where(ur => ur.RoleId == customerRole.Id)
                .Select(ur => ur.UserId)
                .ToListAsync();
            var customers = await _context.Users
                .Where(u => customerIds.Contains(u.Id))
                .ToListAsync();
            Customers = customers.Select(c => new SelectListItem(
                $"{c.FullName} ({c.Email})", c.Id)).ToList();
        }

        if (partnerRole != null)
        {
            var partnerIds = await _context.UserRoles
                .Where(ur => ur.RoleId == partnerRole.Id)
                .Select(ur => ur.UserId)
                .ToListAsync();
            var partners = await _context.Users
                .Where(u => partnerIds.Contains(u.Id))
                .ToListAsync();
            Partners = partners.Select(p => new SelectListItem(
                $"{p.FullName} - {p.CompanyName}", p.Id)).ToList();
        }
    }
}
