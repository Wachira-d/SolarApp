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
public class EditModel : PageModel
{
    private readonly IInstallationService _installationService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ApplicationDbContext _context;

    public EditModel(
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

    public string CustomerDisplayName { get; set; } = string.Empty;
    public List<SelectListItem> Partners { get; set; } = new();

    public class InstallationInput
    {
        public int Id { get; set; }

        [Required] public string SiteName { get; set; } = string.Empty;
        [Required] public string Address { get; set; } = string.Empty;
        public string? City { get; set; }
        public string? Province { get; set; }
        public string? PostalCode { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }

        public int CustomerId { get; set; }
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

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var installation = await _installationService.GetByIdAsync(id);
        if (installation == null)
            return NotFound();

        if (!await CanAccessInstallationAsync(installation))
            return Forbid();

        MapToInput(installation);
        await LoadCustomerDisplayNameAsync(installation.CustomerId);
        await LoadPartnersAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        var installation = await _installationService.GetByIdAsync(id);
        if (installation == null)
            return NotFound();

        if (!await CanAccessInstallationAsync(installation))
            return Forbid();

        // Ensure the route id matches the form id
        if (installation.Id != Input.Id)
            return BadRequest();

        if (!ModelState.IsValid)
        {
            await LoadCustomerDisplayNameAsync(installation.CustomerId);
            await LoadPartnersAsync();
            return Page();
        }

        // Update fields from input (CustomerId is NOT changed)
        installation.SiteName = Input.SiteName;
        installation.Address = Input.Address;
        installation.City = Input.City;
        installation.Province = Input.Province;
        installation.PostalCode = Input.PostalCode;
        installation.Latitude = Input.Latitude;
        installation.Longitude = Input.Longitude;
        installation.PartnerId = Input.PartnerId;
        installation.InverterBrand = Input.InverterBrand;
        installation.InverterModel = Input.InverterModel;
        installation.InverterCount = Input.InverterCount;
        installation.SystemType = Input.SystemType;
        installation.CloudApiKey = Input.CloudApiKey;
        installation.CloudApiSecret = Input.CloudApiSecret;
        installation.CloudUsername = Input.CloudUsername;
        installation.CloudPassword = Input.CloudPassword;
        installation.PlantId = Input.PlantId;
        installation.SystemCapacityKw = Input.SystemCapacityKw;
        installation.NumberOfPanels = Input.NumberOfPanels;
        installation.PanelWattage = Input.PanelWattage;
        installation.PanelBrand = Input.PanelBrand;
        installation.PanelModel = Input.PanelModel;
        installation.BatteryBrand = Input.BatteryBrand;
        installation.BatteryModel = Input.BatteryModel;
        installation.BatteryCapacityKwh = Input.BatteryCapacityKwh;
        installation.BatteryCount = Input.BatteryCount;
        installation.InstallationDate = Input.InstallationDate;
        installation.CommissionDate = Input.CommissionDate;
        installation.InverterWarrantyYears = Input.InverterWarrantyYears;
        installation.TotalCleaningCount = Input.TotalCleaningCount;
        installation.CleaningFrequencyMonths = Input.CleaningFrequencyMonths;
        installation.ElectricityRatePerKwh = Input.ElectricityRatePerKwh;
        installation.FeedInTariffRate = Input.FeedInTariffRate;
        installation.ContractValue = Input.ContractValue;
        installation.Notes = Input.Notes;

        // Recalculate WarrantyExpiryDate if WarrantyYears changed
        if (installation.WarrantyYears != Input.WarrantyYears)
        {
            installation.WarrantyYears = Input.WarrantyYears;
            installation.WarrantyExpiryDate = installation.InstallationDate.AddYears(Input.WarrantyYears);
        }
        else
        {
            installation.WarrantyYears = Input.WarrantyYears;
        }

        // Also recalculate if InstallationDate changed (warranty is based on install date)
        installation.WarrantyExpiryDate = installation.InstallationDate.AddYears(installation.WarrantyYears);
        installation.InverterWarrantyExpiryDate = installation.InstallationDate.AddYears(installation.InverterWarrantyYears);

        installation.UpdatedAt = DateTime.UtcNow;

        await _installationService.UpdateAsync(installation);
        return RedirectToPage("Index");
    }

    private void MapToInput(Installation installation)
    {
        Input = new InstallationInput
        {
            Id = installation.Id,
            SiteName = installation.SiteName,
            Address = installation.Address,
            City = installation.City,
            Province = installation.Province,
            PostalCode = installation.PostalCode,
            Latitude = installation.Latitude,
            Longitude = installation.Longitude,
            CustomerId = installation.CustomerId,
            PartnerId = installation.PartnerId,
            InverterBrand = installation.InverterBrand,
            InverterModel = installation.InverterModel,
            InverterCount = installation.InverterCount,
            SystemType = installation.SystemType,
            CloudApiKey = installation.CloudApiKey,
            CloudApiSecret = installation.CloudApiSecret,
            CloudUsername = installation.CloudUsername,
            CloudPassword = installation.CloudPassword,
            PlantId = installation.PlantId,
            SystemCapacityKw = installation.SystemCapacityKw,
            NumberOfPanels = installation.NumberOfPanels,
            PanelWattage = installation.PanelWattage,
            PanelBrand = installation.PanelBrand,
            PanelModel = installation.PanelModel,
            BatteryBrand = installation.BatteryBrand,
            BatteryModel = installation.BatteryModel,
            BatteryCapacityKwh = installation.BatteryCapacityKwh,
            BatteryCount = installation.BatteryCount,
            InstallationDate = installation.InstallationDate,
            CommissionDate = installation.CommissionDate,
            WarrantyYears = installation.WarrantyYears,
            InverterWarrantyYears = installation.InverterWarrantyYears,
            TotalCleaningCount = installation.TotalCleaningCount,
            CleaningFrequencyMonths = installation.CleaningFrequencyMonths,
            ElectricityRatePerKwh = installation.ElectricityRatePerKwh,
            FeedInTariffRate = installation.FeedInTariffRate,
            ContractValue = installation.ContractValue,
            Notes = installation.Notes
        };
    }

    private async Task<bool> CanAccessInstallationAsync(Installation installation)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
            return false;

        // Admin can access all installations
        if (await _userManager.IsInRoleAsync(user, "Admin"))
            return true;

        // Partner can only access their own installations
        if (await _userManager.IsInRoleAsync(user, "Partner"))
            return user.PartnerId.HasValue && installation.PartnerId == user.PartnerId;

        return false;
    }

    private async Task LoadCustomerDisplayNameAsync(int customerId)
    {
        var customer = await _context.Customers
            .Where(c => c.Id == customerId)
            .Select(c => new { c.FirstName, c.LastName, c.Email })
            .FirstOrDefaultAsync();

        CustomerDisplayName = customer != null
            ? $"{customer.FirstName} {customer.LastName} ({customer.Email})"
            : "Unknown Customer";
    }

    private async Task LoadPartnersAsync()
    {
        Partners = await _context.Partners
            .Where(p => p.IsActive)
            .Select(p => new SelectListItem(p.CompanyName, p.Id.ToString()))
            .ToListAsync();
    }
}
