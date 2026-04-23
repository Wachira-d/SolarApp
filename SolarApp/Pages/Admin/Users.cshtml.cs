using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Pages.Admin;

[Authorize(Roles = "Admin")]
public class UsersModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ApplicationDbContext _context;

    public UsersModel(UserManager<ApplicationUser> userManager, ApplicationDbContext context)
    {
        _userManager = userManager;
        _context = context;
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

    public async Task OnGetAsync()
    {
        await LoadDataAsync();
    }

    // ── User CRUD ──

    public async Task<IActionResult> OnPostCreateUserAsync()
    {
        if (string.IsNullOrEmpty(NewUser.Password))
        {
            ErrorMessage = "Password is required for new users";
            return RedirectToPage();
        }

        var user = new ApplicationUser
        {
            UserName = NewUser.Email,
            Email = NewUser.Email,
            FullName = NewUser.FullName,
            CompanyName = NewUser.CompanyName,
            PhoneNumber = NewUser.Phone,
            LineUserId = NewUser.LineUserId,
            PartnerId = NewUser.PartnerId,
            IsActive = true,
            EmailConfirmed = true
        };

        var result = await _userManager.CreateAsync(user, NewUser.Password);
        if (result.Succeeded)
        {
            await _userManager.AddToRoleAsync(user, NewUser.Role);
            StatusMessage = $"User '{user.Email}' created successfully";
        }
        else
        {
            ErrorMessage = string.Join(", ", result.Errors.Select(e => e.Description));
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostEditUserAsync()
    {
        if (string.IsNullOrEmpty(EditUser.Id))
        {
            ErrorMessage = "User ID is required";
            return RedirectToPage();
        }

        var user = await _userManager.FindByIdAsync(EditUser.Id);
        if (user == null)
        {
            ErrorMessage = "User not found";
            return RedirectToPage();
        }

        user.FullName = EditUser.FullName;
        user.Email = EditUser.Email;
        user.UserName = EditUser.Email;
        user.CompanyName = EditUser.CompanyName;
        user.PhoneNumber = EditUser.Phone;
        user.LineUserId = EditUser.LineUserId;
        user.PartnerId = EditUser.PartnerId;
        user.IsActive = EditUser.IsActive;

        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            ErrorMessage = string.Join(", ", result.Errors.Select(e => e.Description));
            return RedirectToPage();
        }

        // Update role if changed
        var currentRoles = await _userManager.GetRolesAsync(user);
        if (!currentRoles.Contains(EditUser.Role))
        {
            await _userManager.RemoveFromRolesAsync(user, currentRoles);
            await _userManager.AddToRoleAsync(user, EditUser.Role);
        }

        StatusMessage = $"User '{user.Email}' updated";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteUserAsync(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            ErrorMessage = "User not found";
            return RedirectToPage();
        }

        // Prevent deleting last admin
        var roles = await _userManager.GetRolesAsync(user);
        if (roles.Contains(UserRoles.Admin))
        {
            var adminCount = (await _userManager.GetUsersInRoleAsync(UserRoles.Admin)).Count;
            if (adminCount <= 1)
            {
                ErrorMessage = "Cannot delete the last admin user";
                return RedirectToPage();
            }
        }

        // Unlink from Customer
        var linkedCustomer = await _context.Customers.FirstOrDefaultAsync(c => c.UserId == userId);
        if (linkedCustomer != null)
        {
            linkedCustomer.UserId = null;
            await _context.SaveChangesAsync();
        }

        var result = await _userManager.DeleteAsync(user);
        if (result.Succeeded)
            StatusMessage = $"User '{user.Email}' deleted";
        else
            ErrorMessage = string.Join(", ", result.Errors.Select(e => e.Description));

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleUserActiveAsync(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            ErrorMessage = "User not found";
            return RedirectToPage();
        }

        user.IsActive = !user.IsActive;
        await _userManager.UpdateAsync(user);

        // Lock/unlock the user account too
        if (!user.IsActive)
            await _userManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
        else
            await _userManager.SetLockoutEndDateAsync(user, null);

        StatusMessage = $"User '{user.Email}' {(user.IsActive ? "activated" : "deactivated")}";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResetPasswordAsync()
    {
        var user = await _userManager.FindByIdAsync(PwReset.UserId);
        if (user == null)
        {
            ErrorMessage = "User not found";
            return RedirectToPage();
        }

        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        var result = await _userManager.ResetPasswordAsync(user, token, PwReset.NewPassword);

        if (result.Succeeded)
            StatusMessage = $"Password reset for '{user.Email}'";
        else
            ErrorMessage = string.Join(", ", result.Errors.Select(e => e.Description));

        return RedirectToPage();
    }

    // ── Customer CRUD ──

    public async Task<IActionResult> OnPostCreateCustomerAsync()
    {
        _context.Customers.Add(new Customer
        {
            FirstName = NewCustomer.FirstName,
            LastName = NewCustomer.LastName,
            Email = NewCustomer.Email,
            Phone = NewCustomer.Phone,
            LineUserId = NewCustomer.LineUserId,
            Address = NewCustomer.Address,
            City = NewCustomer.City,
            Province = NewCustomer.Province,
            PostalCode = NewCustomer.PostalCode,
            TaxId = NewCustomer.TaxId,
            UserId = NewCustomer.UserId,
            IsActive = true
        });
        await _context.SaveChangesAsync();
        StatusMessage = "Customer created";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostEditCustomerAsync()
    {
        var customer = await _context.Customers.FindAsync(EditCustomer.Id);
        if (customer == null)
        {
            ErrorMessage = "Customer not found";
            return RedirectToPage();
        }

        customer.FirstName = EditCustomer.FirstName;
        customer.LastName = EditCustomer.LastName;
        customer.Email = EditCustomer.Email;
        customer.Phone = EditCustomer.Phone;
        customer.LineUserId = EditCustomer.LineUserId;
        customer.Address = EditCustomer.Address;
        customer.City = EditCustomer.City;
        customer.Province = EditCustomer.Province;
        customer.PostalCode = EditCustomer.PostalCode;
        customer.TaxId = EditCustomer.TaxId;
        customer.UserId = string.IsNullOrEmpty(EditCustomer.UserId) ? null : EditCustomer.UserId;
        customer.IsActive = EditCustomer.IsActive;
        customer.Notes = EditCustomer.Notes;
        customer.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        StatusMessage = $"Customer '{customer.FullName}' updated";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteCustomerAsync(int customerId)
    {
        var customer = await _context.Customers
            .Include(c => c.Installations)
            .FirstOrDefaultAsync(c => c.Id == customerId);

        if (customer == null)
        {
            ErrorMessage = "Customer not found";
            return RedirectToPage();
        }

        if (customer.Installations.Any())
        {
            ErrorMessage = $"Cannot delete: customer has {customer.Installations.Count} installation(s). Delete installations first.";
            return RedirectToPage();
        }

        _context.Customers.Remove(customer);
        await _context.SaveChangesAsync();
        StatusMessage = $"Customer '{customer.FullName}' deleted";
        return RedirectToPage();
    }

    // ── Partner CRUD ──

    public async Task<IActionResult> OnPostCreatePartnerAsync()
    {
        _context.Partners.Add(new Partner
        {
            CompanyName = NewPartner.CompanyName,
            ContactEmail = NewPartner.ContactEmail,
            ContactPhone = NewPartner.ContactPhone,
            Address = NewPartner.Address,
            City = NewPartner.City,
            Province = NewPartner.Province,
            PostalCode = NewPartner.PostalCode,
            CommissionRate = NewPartner.CommissionRate,
            TaxId = NewPartner.TaxId,
            ContractStartDate = NewPartner.ContractStartDate,
            ContractEndDate = NewPartner.ContractEndDate,
            IsActive = true
        });
        await _context.SaveChangesAsync();
        StatusMessage = "Partner created";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostEditPartnerAsync()
    {
        var partner = await _context.Partners.FindAsync(EditPartner.Id);
        if (partner == null)
        {
            ErrorMessage = "Partner not found";
            return RedirectToPage();
        }

        partner.CompanyName = EditPartner.CompanyName;
        partner.ContactEmail = EditPartner.ContactEmail;
        partner.ContactPhone = EditPartner.ContactPhone;
        partner.Address = EditPartner.Address;
        partner.City = EditPartner.City;
        partner.Province = EditPartner.Province;
        partner.PostalCode = EditPartner.PostalCode;
        partner.CommissionRate = EditPartner.CommissionRate;
        partner.TaxId = EditPartner.TaxId;
        partner.ContractStartDate = EditPartner.ContractStartDate;
        partner.ContractEndDate = EditPartner.ContractEndDate;
        partner.IsActive = EditPartner.IsActive;
        partner.Notes = EditPartner.Notes;
        partner.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        StatusMessage = $"Partner '{partner.CompanyName}' updated";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeletePartnerAsync(int partnerId)
    {
        var partner = await _context.Partners
            .Include(p => p.Installations)
            .Include(p => p.Users)
            .FirstOrDefaultAsync(p => p.Id == partnerId);

        if (partner == null)
        {
            ErrorMessage = "Partner not found";
            return RedirectToPage();
        }

        if (partner.Installations.Any())
        {
            ErrorMessage = $"Cannot delete: partner has {partner.Installations.Count} installation(s)";
            return RedirectToPage();
        }

        // Unlink users
        foreach (var u in partner.Users)
        {
            u.PartnerId = null;
        }
        await _context.SaveChangesAsync();

        _context.Partners.Remove(partner);
        await _context.SaveChangesAsync();
        StatusMessage = $"Partner '{partner.CompanyName}' deleted";
        return RedirectToPage();
    }

    private async Task LoadDataAsync()
    {
        var users = await _userManager.Users
            .Include(u => u.Partner)
            .OrderByDescending(u => u.CreatedAt)
            .ToListAsync();

        foreach (var u in users)
        {
            var roles = await _userManager.GetRolesAsync(u);
            Users.Add(new UserViewModel
            {
                Id = u.Id,
                FullName = u.FullName,
                Email = u.Email ?? "",
                CompanyName = u.CompanyName,
                PhoneNumber = u.PhoneNumber,
                LineUserId = u.LineUserId,
                PartnerId = u.PartnerId,
                PartnerName = u.Partner?.CompanyName,
                Role = roles.FirstOrDefault() ?? "-",
                IsActive = u.IsActive,
                CreatedAt = u.CreatedAt,
                LastLoginAt = u.LastLoginAt
            });
        }

        CustomerList = await _context.Customers
            .Include(c => c.Installations)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();

        PartnerList = await _context.Partners
            .Include(p => p.Installations)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();
    }
}
