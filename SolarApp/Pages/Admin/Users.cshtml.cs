using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SolarApp.Models;
using SolarApp.Models.Enums;
using SolarApp.Services.Admin;

namespace SolarApp.Pages.Admin;

[Authorize(Roles = "Admin")]
public class UsersModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IUserAdminQueryService _queryService;
    private readonly IUserAdminCommandService _commandService;

    public UsersModel(
        UserManager<ApplicationUser> userManager,
        IUserAdminQueryService queryService,
        IUserAdminCommandService commandService)
    {
        _userManager = userManager;
        _queryService = queryService;
        _commandService = commandService;
    }

    public List<UserViewModel> Users { get; set; } = new();
    public List<Customer> CustomerList { get; set; } = new();
    public List<Partner> PartnerList { get; set; } = new();

    [BindProperty] public UserInput NewUser { get; set; } = new();
    [BindProperty] public UserInput EditUser { get; set; } = new();
    [BindProperty] public CustomerInput NewCustomer { get; set; } = new();
    [BindProperty] public CustomerInput EditCustomer { get; set; } = new();
    [BindProperty] public PartnerInput NewPartner { get; set; } = new();
    [BindProperty] public PartnerInput EditPartner { get; set; } = new();
    [BindProperty] public ResetPasswordInput PwReset { get; set; } = new();

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    // ─────────── Nested view-models (bound by Razor) ───────────

    public class UserViewModel
    {
        public string Id { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? CompanyName { get; set; }
        public string? PhoneNumber { get; set; }
        public string? LineUserId { get; set; }
        public int? PartnerId { get; set; }
        public string? PartnerName { get; set; }
        public string Role { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastLoginAt { get; set; }
    }

    public class UserInput
    {
        public string? Id { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? Password { get; set; }
        public string? CompanyName { get; set; }
        public string? Phone { get; set; }
        public string? LineUserId { get; set; }
        public string Role { get; set; } = UserRoles.Customer;
        public int? PartnerId { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public class CustomerInput
    {
        public int Id { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string? Email { get; set; }
        public string Phone { get; set; } = string.Empty;
        public string? LineUserId { get; set; }
        public string Address { get; set; } = string.Empty;
        public string? City { get; set; }
        public string? Province { get; set; }
        public string? PostalCode { get; set; }
        public string? TaxId { get; set; }
        public string? UserId { get; set; }
        public bool IsActive { get; set; } = true;
        public string? Notes { get; set; }
    }

    public class PartnerInput
    {
        public int Id { get; set; }
        public string CompanyName { get; set; } = string.Empty;
        public string ContactEmail { get; set; } = string.Empty;
        public string ContactPhone { get; set; } = string.Empty;
        public string? Address { get; set; }
        public string? City { get; set; }
        public string? Province { get; set; }
        public string? PostalCode { get; set; }
        public decimal? CommissionRate { get; set; }
        public string? TaxId { get; set; }
        public DateTime? ContractStartDate { get; set; }
        public DateTime? ContractEndDate { get; set; }
        public bool IsActive { get; set; } = true;
        public string? Notes { get; set; }
    }

    public class ResetPasswordInput
    {
        public string UserId { get; set; } = string.Empty;
        public string NewPassword { get; set; } = string.Empty;
    }

    // ─────────── GET ───────────

    public async Task OnGetAsync() => await LoadDataAsync();

    // ─────────── User handlers ───────────

    public async Task<IActionResult> OnPostCreateUserAsync()
        => Dispatch(await _commandService.CreateUserAsync(ToServiceInput(NewUser)));

    public async Task<IActionResult> OnPostEditUserAsync()
    {
        var currentUser = await _userManager.GetUserAsync(User);
        return Dispatch(await _commandService.EditUserAsync(ToServiceInput(EditUser), currentUser?.Id));
    }

    public async Task<IActionResult> OnPostDeleteUserAsync(string userId)
        => Dispatch(await _commandService.DeleteUserAsync(userId));

    public async Task<IActionResult> OnPostToggleUserActiveAsync(string userId)
        => Dispatch(await _commandService.ToggleUserActiveAsync(userId));

    public async Task<IActionResult> OnPostResetPasswordAsync()
        => Dispatch(await _commandService.ResetPasswordAsync(PwReset.UserId, PwReset.NewPassword));

    // ─────────── Customer handlers ───────────

    public async Task<IActionResult> OnPostCreateCustomerAsync()
        => Dispatch(await _commandService.CreateCustomerAsync(ToServiceInput(NewCustomer)));

    public async Task<IActionResult> OnPostEditCustomerAsync()
        => Dispatch(await _commandService.EditCustomerAsync(ToServiceInput(EditCustomer)));

    public async Task<IActionResult> OnPostDeleteCustomerAsync(int customerId)
        => Dispatch(await _commandService.DeleteCustomerAsync(customerId));

    // ─────────── Partner handlers ───────────

    public async Task<IActionResult> OnPostCreatePartnerAsync()
        => Dispatch(await _commandService.CreatePartnerAsync(ToServiceInput(NewPartner)));

    public async Task<IActionResult> OnPostEditPartnerAsync()
        => Dispatch(await _commandService.EditPartnerAsync(ToServiceInput(EditPartner)));

    public async Task<IActionResult> OnPostDeletePartnerAsync(int partnerId)
        => Dispatch(await _commandService.DeletePartnerAsync(partnerId));

    // ─────────── Invite handlers ───────────

    public async Task<IActionResult> OnPostInviteCustomerAsync(int customerId, string? password)
        => Dispatch(await _commandService.InviteCustomerAsync(customerId, password));

    public async Task<IActionResult> OnPostInvitePartnerAsync(int partnerId, string? password, string? firstName, string? lastName)
        => Dispatch(await _commandService.InvitePartnerAsync(partnerId, password, firstName, lastName));

    // ─────────── Helpers ───────────

    private IActionResult Dispatch(CommandResult result)
    {
        if (result.Success) StatusMessage = result.Message;
        else ErrorMessage = result.Message;
        return RedirectToPage();
    }

    private static UserAdminInput ToServiceInput(UserInput x) => new()
    {
        Id = x.Id,
        FullName = x.FullName,
        Email = x.Email,
        Password = x.Password,
        CompanyName = x.CompanyName,
        Phone = x.Phone,
        LineUserId = x.LineUserId,
        Role = x.Role,
        PartnerId = x.PartnerId,
        IsActive = x.IsActive
    };

    private static CustomerAdminInput ToServiceInput(CustomerInput x) => new()
    {
        Id = x.Id,
        FirstName = x.FirstName,
        LastName = x.LastName,
        Email = x.Email,
        Phone = x.Phone,
        LineUserId = x.LineUserId,
        Address = x.Address,
        City = x.City,
        Province = x.Province,
        PostalCode = x.PostalCode,
        TaxId = x.TaxId,
        UserId = x.UserId,
        IsActive = x.IsActive,
        Notes = x.Notes
    };

    private static PartnerAdminInput ToServiceInput(PartnerInput x) => new()
    {
        Id = x.Id,
        CompanyName = x.CompanyName,
        ContactEmail = x.ContactEmail,
        ContactPhone = x.ContactPhone,
        Address = x.Address,
        City = x.City,
        Province = x.Province,
        PostalCode = x.PostalCode,
        CommissionRate = x.CommissionRate,
        TaxId = x.TaxId,
        ContractStartDate = x.ContractStartDate,
        ContractEndDate = x.ContractEndDate,
        IsActive = x.IsActive,
        Notes = x.Notes
    };

    private async Task LoadDataAsync()
    {
        // Delegates to UserAdminQueryService — uses a single JOIN for roles
        // (eliminates the previous N+1 GetRolesAsync-per-user pattern).
        var data = await _queryService.LoadPageDataAsync();

        Users = data.Users.Select(r => new UserViewModel
        {
            Id = r.Id,
            FullName = r.FullName,
            Email = r.Email,
            CompanyName = r.CompanyName,
            PhoneNumber = r.PhoneNumber,
            LineUserId = r.LineUserId,
            PartnerId = r.PartnerId,
            PartnerName = r.PartnerName,
            Role = r.Role,
            IsActive = r.IsActive,
            CreatedAt = r.CreatedAt,
            LastLoginAt = r.LastLoginAt
        }).ToList();

        CustomerList = data.Customers;
        PartnerList = data.Partners;
    }
}
