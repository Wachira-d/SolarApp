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
        public string? City { get; set; }
        public string? Province { get; set; }
        public string? PostalCode { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }

        [Required] public int CustomerId { get; set; }
        public int? PartnerId { get; set; }

        [Required] public InverterBrand InverterBrand { get; set; }
        public string? InverterModel { get; set; }
        public int InverterCount { get; set; } = 1;
        public SystemType SystemType { get; set; } = SystemType.OnGrid;
        public string? CloudApiKey { get; set; }
        public string? CloudApiSecret { get; set; }
        public string? CloudUsername { get; set; }
        public string? CloudPassword { get; set; }
        public string? PlantId { get; set; }

        [Required][Range(0.1, 10000)] public decimal SystemCapacityKw { get; set; }
        [Required][Range(1, 10000)] public int NumberOfPanels { get; set; }
        [Required][Range(100, 1000)] public int PanelWattage { get; set; }
        public string? PanelBrand { get; set; }
        public string? PanelModel { get; set; }
        public string? BatteryBrand { get; set; }
        public string? BatteryModel { get; set; }
        public decimal? BatteryCapacityKwh { get; set; }
        public int? BatteryCount { get; set; }

        [Required] public DateTime InstallationDate { get; set; } = DateTime.Today;
        public DateTime? CommissionDate { get; set; }

        [Required][Range(1, 30)] public int WarrantyYears { get; set; } = 10;
        public int InverterWarrantyYears { get; set; } = 10;
        [Range(0, 100)] public int TotalCleaningCount { get; set; }
        public int CleaningFrequencyMonths { get; set; } = 6;
        public decimal ElectricityRatePerKwh { get; set; } = 4.0m;
        public decimal? FeedInTariffRate { get; set; }
        public decimal? ContractValue { get; set; }

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

        var user = await _userManager.GetUserAsync(User);

        var installation = new Installation
        {
            SiteName = Input.SiteName,
            Address = Input.Address,
            City = Input.City,
            Province = Input.Province,
            PostalCode = Input.PostalCode,
            Latitude = Input.Latitude,
            Longitude = Input.Longitude,
            CustomerId = Input.CustomerId,
            PartnerId = Input.PartnerId,
            InverterBrand = Input.InverterBrand,
            InverterModel = Input.InverterModel,
            InverterCount = Input.InverterCount,
            SystemType = Input.SystemType,
            CloudApiKey = Input.CloudApiKey,
            CloudApiSecret = Input.CloudApiSecret,
            CloudUsername = Input.CloudUsername,
            CloudPassword = Input.CloudPassword,
            PlantId = Input.PlantId,
            SystemCapacityKw = Input.SystemCapacityKw,
            NumberOfPanels = Input.NumberOfPanels,
            PanelWattage = Input.PanelWattage,
            PanelBrand = Input.PanelBrand,
            PanelModel = Input.PanelModel,
            BatteryBrand = Input.BatteryBrand,
            BatteryModel = Input.BatteryModel,
            BatteryCapacityKwh = Input.BatteryCapacityKwh,
            BatteryCount = Input.BatteryCount,
            InstallationDate = Input.InstallationDate,
            CommissionDate = Input.CommissionDate,
            WarrantyYears = Input.WarrantyYears,
            InverterWarrantyYears = Input.InverterWarrantyYears,
            TotalCleaningCount = Input.TotalCleaningCount,
            CleaningFrequencyMonths = Input.CleaningFrequencyMonths,
            ElectricityRatePerKwh = Input.ElectricityRatePerKwh,
            FeedInTariffRate = Input.FeedInTariffRate,
            ContractValue = Input.ContractValue,
            Notes = Input.Notes,
            CreatedByUserId = user?.Id
        };

        if (installation.TotalCleaningCount > 0)
            installation.NextCleaningDueDate = installation.InstallationDate.AddMonths(installation.CleaningFrequencyMonths);

        await _installationService.CreateAsync(installation);
        return RedirectToPage("Index");
    }

    private async Task LoadDropdownsAsync()
    {
        Customers = await _context.Customers
            .Where(c => c.IsActive)
            .Select(c => new SelectListItem($"{c.FirstName} {c.LastName} ({c.Email})", c.Id.ToString()))
            .ToListAsync();

        Partners = await _context.Partners
            .Where(p => p.IsActive)
            .Select(p => new SelectListItem(p.CompanyName, p.Id.ToString()))
            .ToListAsync();
    }
}
