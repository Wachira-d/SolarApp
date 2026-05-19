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
using SolarApp.Services.Security;

namespace SolarApp.Pages.Installations;

[Authorize(Roles = "Admin,Partner")]
public class EditModel : PageModel
{
    private readonly IInstallationService _installationService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ApplicationDbContext _context;
    private readonly ICredentialProtector _credProtector;
    private readonly ILogger<EditModel> _logger;

    public EditModel(
        IInstallationService installationService,
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext context,
        ICredentialProtector credProtector,
        ILogger<EditModel> logger)
    {
        _installationService = installationService;
        _userManager = userManager;
        _context = context;
        _credProtector = credProtector;
        _logger = logger;
    }

    [BindProperty]
    public InstallationInput Input { get; set; } = new();

    public string CustomerDisplayName { get; set; } = string.Empty;

    // Show "configured" badges in the UI without exposing the actual values
    public bool HasApiKey { get; set; }
    public bool HasApiSecret { get; set; }
    public bool HasUsername { get; set; }
    public bool HasPassword { get; set; }
    public bool HasPlantId { get; set; }
    public string CredentialStorageNote { get; set; } = "";
    public List<SelectListItem> Partners { get; set; } = new();
    public List<SelectListItem> Customers { get; set; } = new();

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
        public string? CustomerFirstName { get; set; }
        public string? CustomerLastName { get; set; }
        public string? CustomerPhone { get; set; }
        public string? CustomerEmail { get; set; }
        public int? PartnerId { get; set; }

        [Required] public InverterBrand InverterBrand { get; set; }
        public string? InverterModel { get; set; }
        public int InverterCount { get; set; } = 1;
        public decimal InverterSizeKw { get; set; }
        public SystemType SystemType { get; set; } = SystemType.OnGrid;
        public string? CloudApiKey { get; set; }
        public string? CloudApiSecret { get; set; }
        public string? CloudUsername { get; set; }
        public string? CloudPassword { get; set; }
        public string? PlantId { get; set; }
        public string? CloudApiBaseUrl { get; set; }

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

        // Service scope (see Installation.cs for semantics)
        public ServiceLevel ServiceLevel { get; set; } = ServiceLevel.FullOM;
        public bool IncludesMonitoring { get; set; } = true;
        public bool IncludesCleaning { get; set; } = true;
        public bool IncludesPreventiveMaintenance { get; set; } = true;
        public bool IncludesCorrectiveMaintenance { get; set; } = true;
        public bool IncludesWarrantyService { get; set; } = true;
        public string? ServiceNotes { get; set; }

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
        await LoadCustomersAsync();
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

        // Defense layer 5 — duplicate form-field guard.  If the cshtml ever
        // accidentally emits two <input name="Input.CustomerId"> elements
        // (e.g. someone re-adds a hidden field next to the dropdown), the
        // default scalar binder takes the FIRST value, which is the stale
        // page-load id — making the page silently ignore the user's dropdown
        // selection and triggering the customer-rename bug.  Detect that
        // case here and prefer the LAST value (the dropdown is always the
        // last <select name="Input.CustomerId"> in form order).
        if (Request.HasFormContentType)
        {
            var rawCustomerIds = Request.Form["Input.CustomerId"];
            if (rawCustomerIds.Count > 1
                && int.TryParse(rawCustomerIds[rawCustomerIds.Count - 1], out var lastCid)
                && lastCid != Input.CustomerId)
            {
                _logger.LogWarning(
                    "Edit Installation {Id}: form submitted {Count} Input.CustomerId values ({Values}); binder picked '{Bound}' but dropdown was '{Last}'. Overriding to dropdown value.",
                    id, rawCustomerIds.Count, string.Join(",", rawCustomerIds.ToArray()),
                    Input.CustomerId, lastCid);
                Input.CustomerId = lastCid;
            }
        }

        if (!ModelState.IsValid)
        {
            await LoadCustomerDisplayNameAsync(installation.CustomerId);
            await LoadPartnersAsync();
            await LoadCustomersAsync();
            return Page();
        }

        // Reassigning an installation to a different Customer or Partner is an
        // ownership-changing operation. Only Admins may do this; Partner users
        // (who can otherwise edit their own installations) can only update fields,
        // not move installations to a different tenant.
        var isAdmin = User.IsInRole("Admin");

        // Snapshot original link before any reassign mutation — needed so the
        // inline-edit block below can tell whether the user changed the
        // dropdown (in which case those fields hold the *new* customer's data
        // auto-filled by Edit.cshtml's customerSelect change handler, not a
        // genuine edit of the originally-linked customer).
        var originalCustomerId = installation.CustomerId;

        if (Input.CustomerId > 0 && Input.CustomerId != installation.CustomerId)
        {
            if (!isAdmin)
            {
                ModelState.AddModelError(string.Empty, "Only admins may reassign an installation to a different customer.");
                await LoadCustomersAsync();
                return Page();
            }
            var customerExists = await _context.Customers.AnyAsync(c => c.Id == Input.CustomerId && c.IsActive);
            if (!customerExists)
            {
                ModelState.AddModelError(string.Empty, "Selected customer does not exist or is inactive.");
                await LoadCustomersAsync();
                return Page();
            }

            // Only flip the FK.  Earlier versions also did
            //   installation.Customer = null; _context.Entry(old).State = Detached;
            // but that throws on save because CustomerId is a required
            // (non-nullable) FK and EF's NavigationFixer treats nav-to-null
            // as "severing a required relationship" → InvalidOperationException.
            //
            // With the real root cause fixed (duplicate hidden Input.CustomerId
            // form field, see commit d870edb) the other defenses (inline-edit
            // gate, no-cascade UpdateAsync, Layer-2 change-tracker safety net)
            // already prevent the rename — detaching the navigation isn't
            // needed and was actively harmful.
            installation.CustomerId = Input.CustomerId;
        }

        // Inline-edit customer details — ONLY when the user did NOT switch
        // customers in the dropdown. When they did, CustomerFirstName/Last/
        // Phone/Email were auto-populated by the JS change handler with the
        // newly-selected customer's data; applying them would either be a
        // no-op (when reassign succeeded) or clobber the original customer's
        // name with the new one's (when reassign was rejected).
        var userSwitchedCustomer = Input.CustomerId > 0 && Input.CustomerId != originalCustomerId;
        int? intentionallyEditedCustomerId = null;
        if (!userSwitchedCustomer
            && (!string.IsNullOrWhiteSpace(Input.CustomerFirstName) || !string.IsNullOrWhiteSpace(Input.CustomerLastName)))
        {
            var customer = await _context.Customers.FindAsync(installation.CustomerId);
            if (customer != null)
            {
                if (!string.IsNullOrWhiteSpace(Input.CustomerFirstName))
                    customer.FirstName = Input.CustomerFirstName.Trim();
                if (!string.IsNullOrWhiteSpace(Input.CustomerLastName))
                    customer.LastName = Input.CustomerLastName.Trim();
                if (!string.IsNullOrWhiteSpace(Input.CustomerPhone))
                    customer.Phone = Input.CustomerPhone.Trim();
                if (Input.CustomerEmail != null)
                    customer.Email = Input.CustomerEmail.Trim();
                customer.UpdatedAt = DateTime.UtcNow;
                intentionallyEditedCustomerId = customer.Id;
            }
        }

        // Update fields from input
        installation.SiteName = Input.SiteName;
        installation.Address = Input.Address;
        installation.City = Input.City;
        installation.Province = Input.Province;
        installation.PostalCode = Input.PostalCode;
        installation.Latitude = Input.Latitude;
        installation.Longitude = Input.Longitude;
        // Only admins may move installation to a different Partner
        if (Input.PartnerId != installation.PartnerId)
        {
            if (!isAdmin)
            {
                ModelState.AddModelError(string.Empty, "Only admins may reassign an installation to a different partner.");
                await LoadCustomersAsync();
                return Page();
            }
            // FK-only update (see the Customer block above for why we don't
            // also null the Partner navigation).  PartnerId is nullable so
            // the severed-required-relationship error wouldn't fire here, but
            // we keep the two reassign blocks symmetrical for readability.
            installation.PartnerId = Input.PartnerId;
        }
        installation.InverterBrand = Input.InverterBrand;
        installation.InverterModel = Input.InverterModel;
        installation.InverterCount = Input.InverterCount;
        installation.InverterSizeKw = Input.InverterSizeKw;
        installation.SystemType = Input.SystemType;
        // Form NEVER displays stored secrets. Leaving blank = "keep existing".
        // Only overwrite when the user explicitly typed a new value.
        // IMPORTANT: empty HTML input binds as "" (not null), so IsNullOrEmpty
        // correctly detects "user didn't type anything".
        if (!string.IsNullOrWhiteSpace(Input.CloudApiKey))
            installation.CloudApiKey = _credProtector.Protect(Input.CloudApiKey.Trim());
        if (!string.IsNullOrWhiteSpace(Input.CloudApiSecret))
            installation.CloudApiSecret = _credProtector.Protect(Input.CloudApiSecret.Trim());

        var usernameChanged = !string.IsNullOrWhiteSpace(Input.CloudUsername);
        var passwordChanged = !string.IsNullOrWhiteSpace(Input.CloudPassword);
        if (usernameChanged)
            installation.CloudUsername = _credProtector.Protect(Input.CloudUsername!.Trim());
        if (passwordChanged)
            installation.CloudPassword = _credProtector.Protect(Input.CloudPassword!.Trim());

        // If Huawei credentials changed, purge the static XSRF token cache so the
        // next sync re-logins instead of using a stale 15-min cached token.
        if (installation.InverterBrand == SolarApp.Models.Enums.InverterBrand.Huawei
            && (usernameChanged || passwordChanged))
        {
            var newUsername = _credProtector.Unprotect(installation.CloudUsername);
            var newPassword = _credProtector.Unprotect(installation.CloudPassword);
            SolarApp.Services.InverterApi.Adapters.HuaweiAdapter
                .InvalidateTokenForCredentials(newUsername, newPassword);
        }
        // PlantId is NOT a secret but same keep-existing logic to prevent accidental wipe
        if (!string.IsNullOrWhiteSpace(Input.PlantId))
            installation.PlantId = Input.PlantId.Trim();
        // else keep the existing installation.PlantId

        // CloudApiBaseUrl: explicit empty = clear; null = keep; non-empty = set
        if (Input.CloudApiBaseUrl != null)
        {
            var trimmed = Input.CloudApiBaseUrl.Trim();
            installation.CloudApiBaseUrl = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        }
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

        // Service scope. ServiceLevel is the UI preset; the IncludesXxx flags
        // are the runtime source of truth (the Edit form's JS toggles them
        // when the preset radio changes, but the operator can also override
        // them directly for Custom contracts).
        installation.ServiceLevel = Input.ServiceLevel;
        installation.IncludesMonitoring = Input.IncludesMonitoring;
        installation.IncludesCleaning = Input.IncludesCleaning;
        installation.IncludesPreventiveMaintenance = Input.IncludesPreventiveMaintenance;
        installation.IncludesCorrectiveMaintenance = Input.IncludesCorrectiveMaintenance;
        installation.IncludesWarrantyService = Input.IncludesWarrantyService;
        installation.ServiceNotes = string.IsNullOrWhiteSpace(Input.ServiceNotes) ? null : Input.ServiceNotes.Trim();

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

        // Defense layer 2 — final safety net. Walk every Customer entity in
        // the ChangeTracker and revert any that's flagged Modified except
        // the one (if any) that the inline-edit block intentionally touched.
        // This catches future regressions where some code path (EF FK fix-up,
        // an auto-mapper, an audit interceptor, etc.) marks a Customer as
        // Modified without explicit caller intent.  Logging the revert
        // surfaces the underlying bug instead of letting data corruption
        // ship silently.
        foreach (var entry in _context.ChangeTracker.Entries<Customer>().ToList())
        {
            if (entry.State == EntityState.Modified
                && entry.Entity.Id != intentionallyEditedCustomerId)
            {
                _logger.LogWarning(
                    "Edit Installation {InstallationId}: reverted unintended Customer.Modified state for customerId={CustomerId} (intentional edit was for {IntentionalId}). Properties marked changed: {Changed}",
                    installation.Id,
                    entry.Entity.Id,
                    intentionallyEditedCustomerId,
                    string.Join(",", entry.Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name)));
                entry.State = EntityState.Unchanged;
            }
        }

        await _installationService.UpdateAsync(installation);
        return RedirectToPage("Index");
    }

    private void MapToInput(Installation installation)
    {
        HasApiKey    = !string.IsNullOrEmpty(installation.CloudApiKey);
        HasApiSecret = !string.IsNullOrEmpty(installation.CloudApiSecret);
        HasUsername  = !string.IsNullOrEmpty(installation.CloudUsername);
        HasPassword  = !string.IsNullOrEmpty(installation.CloudPassword);
        HasPlantId   = !string.IsNullOrEmpty(installation.PlantId);

        // Diagnostic note for admin to understand storage state
        var parts = new List<string>();
        if (HasApiKey) parts.Add($"ApiKey: {(installation.CloudApiKey!.StartsWith("enc:") ? "encrypted" : "plaintext")}");
        if (HasApiSecret) parts.Add($"ApiSecret: {(installation.CloudApiSecret!.StartsWith("enc:") ? "encrypted" : "plaintext")}");
        if (HasUsername) parts.Add($"Username: {(installation.CloudUsername!.StartsWith("enc:") ? "encrypted" : "plaintext")}");
        if (HasPassword) parts.Add($"Password: {(installation.CloudPassword!.StartsWith("enc:") ? "encrypted" : "plaintext")}");
        CredentialStorageNote = parts.Any() ? string.Join(" | ", parts) : "No credentials stored";

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
            InverterSizeKw = installation.InverterSizeKw,
            SystemType = installation.SystemType,
            // SECRETS (ApiKey/Secret/Password): never echo back to form.
            // NON-SECRETS (Username/PlantId): show value so user can verify.
            CloudApiKey = null,
            CloudApiSecret = null,
            CloudUsername = _credProtector.Unprotect(installation.CloudUsername),
            CloudPassword = null,
            PlantId = installation.PlantId,
            CloudApiBaseUrl = installation.CloudApiBaseUrl,
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
            Notes = installation.Notes,
            ServiceLevel = installation.ServiceLevel,
            IncludesMonitoring = installation.IncludesMonitoring,
            IncludesCleaning = installation.IncludesCleaning,
            IncludesPreventiveMaintenance = installation.IncludesPreventiveMaintenance,
            IncludesCorrectiveMaintenance = installation.IncludesCorrectiveMaintenance,
            IncludesWarrantyService = installation.IncludesWarrantyService,
            ServiceNotes = installation.ServiceNotes,
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
            .Select(c => new { c.FirstName, c.LastName, c.Email, c.Phone })
            .FirstOrDefaultAsync();

        if (customer != null)
        {
            CustomerDisplayName = $"{customer.FirstName} {customer.LastName} ({customer.Email})";
            Input.CustomerFirstName = customer.FirstName;
            Input.CustomerLastName = customer.LastName;
            Input.CustomerPhone = customer.Phone;
            Input.CustomerEmail = customer.Email;
        }
        else
        {
            CustomerDisplayName = "Unknown Customer";
        }
    }

    private async Task LoadPartnersAsync()
    {
        Partners = await _context.Partners
            .Where(p => p.IsActive)
            .Select(p => new SelectListItem(p.CompanyName, p.Id.ToString()))
            .ToListAsync();
    }

    private async Task LoadCustomersAsync()
    {
        Customers = await _context.Customers
            .Where(c => c.IsActive)
            .OrderBy(c => c.FirstName).ThenBy(c => c.LastName)
            .Select(c => new SelectListItem(
                $"{c.FirstName} {c.LastName} ({c.Phone})",
                c.Id.ToString()))
            .ToListAsync();
    }
}
