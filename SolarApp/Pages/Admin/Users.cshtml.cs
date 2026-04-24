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

    // Whitelist of roles that can be assigned via UI
    private static readonly HashSet<string> AllowedRoles = new(StringComparer.OrdinalIgnoreCase)
        { UserRoles.Admin, UserRoles.Partner, UserRoles.Customer };

    public async Task<IActionResult> OnPostCreateUserAsync()
    {
        if (string.IsNullOrEmpty(NewUser.Password))
        {
            ErrorMessage = "Password is required for new users";
            return RedirectToPage();
        }
        if (!AllowedRoles.Contains(NewUser.Role ?? ""))
        {
            ErrorMessage = "Invalid role. Must be Admin, Partner, or Customer.";
            return RedirectToPage();
        }
        if (string.IsNullOrWhiteSpace(NewUser.Email) ||
            !System.Text.RegularExpressions.Regex.IsMatch(NewUser.Email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
        {
            ErrorMessage = "Valid email is required";
            return RedirectToPage();
        }
        // Partner role must have a PartnerId
        if (NewUser.Role == UserRoles.Partner && !NewUser.PartnerId.HasValue)
        {
            ErrorMessage = "Partner role requires a Partner assignment";
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
            PartnerId = NewUser.Role == UserRoles.Partner ? NewUser.PartnerId : null,
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

        if (!AllowedRoles.Contains(EditUser.Role ?? ""))
        {
            ErrorMessage = "Invalid role. Must be Admin, Partner, or Customer.";
            return RedirectToPage();
        }

        // Prevent admins from demoting themselves (lockout risk)
        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser?.Id == user.Id && EditUser.Role != UserRoles.Admin)
        {
            ErrorMessage = "You cannot change your own role away from Admin.";
            return RedirectToPage();
        }

        // Partner assignment must match role
        if (EditUser.Role == UserRoles.Partner && !EditUser.PartnerId.HasValue)
        {
            ErrorMessage = "Partner role requires a Partner assignment";
            return RedirectToPage();
        }

        user.FullName = EditUser.FullName;
        user.Email = EditUser.Email;
        user.UserName = EditUser.Email;
        user.CompanyName = EditUser.CompanyName;
        user.PhoneNumber = EditUser.Phone;
        user.LineUserId = EditUser.LineUserId;
        user.PartnerId = EditUser.Role == UserRoles.Partner ? EditUser.PartnerId : null;
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
        if (string.IsNullOrWhiteSpace(NewCustomer.FirstName) || string.IsNullOrWhiteSpace(NewCustomer.LastName))
        {
            ErrorMessage = "First name and last name are required";
            return RedirectToPage();
        }
        if (!string.IsNullOrWhiteSpace(NewCustomer.Email) &&
            !System.Text.RegularExpressions.Regex.IsMatch(NewCustomer.Email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
        {
            ErrorMessage = "Invalid email format";
            return RedirectToPage();
        }

        // Uniqueness check on UserId link
        if (!string.IsNullOrEmpty(NewCustomer.UserId))
        {
            var conflict = await _context.Customers.AnyAsync(c => c.UserId == NewCustomer.UserId);
            if (conflict)
            {
                ErrorMessage = "That user is already linked to another customer.";
                return RedirectToPage();
            }
        }

        _context.Customers.Add(new Customer
        {
            FirstName = NewCustomer.FirstName.Trim(),
            LastName = NewCustomer.LastName.Trim(),
            Email = NewCustomer.Email?.Trim() ?? string.Empty,
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

        // Uniqueness check: prevent re-assigning a UserId already linked to another Customer
        // (identity theft / split-brain risk)
        var newUserId = string.IsNullOrEmpty(EditCustomer.UserId) ? null : EditCustomer.UserId;
        if (!string.IsNullOrEmpty(newUserId) && newUserId != customer.UserId)
        {
            var conflict = await _context.Customers
                .AnyAsync(c => c.UserId == newUserId && c.Id != customer.Id);
            if (conflict)
            {
                ErrorMessage = "That user is already linked to another customer.";
                return RedirectToPage();
            }
        }

        if (string.IsNullOrWhiteSpace(EditCustomer.FirstName) || string.IsNullOrWhiteSpace(EditCustomer.LastName))
        {
            ErrorMessage = "First name and last name are required";
            return RedirectToPage();
        }
        if (!string.IsNullOrWhiteSpace(EditCustomer.Email) &&
            !System.Text.RegularExpressions.Regex.IsMatch(EditCustomer.Email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
        {
            ErrorMessage = "Invalid email format";
            return RedirectToPage();
        }

        customer.FirstName = EditCustomer.FirstName.Trim();
        customer.LastName = EditCustomer.LastName.Trim();
        customer.Email = EditCustomer.Email?.Trim() ?? string.Empty;
        customer.Phone = EditCustomer.Phone;
        customer.LineUserId = EditCustomer.LineUserId;
        customer.Address = EditCustomer.Address;
        customer.City = EditCustomer.City;
        customer.Province = EditCustomer.Province;
        customer.PostalCode = EditCustomer.PostalCode;
        customer.TaxId = EditCustomer.TaxId;
        customer.UserId = newUserId;
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
        if (string.IsNullOrWhiteSpace(NewPartner.CompanyName))
        {
            ErrorMessage = "Company name is required";
            return RedirectToPage();
        }
        if (NewPartner.CommissionRate.HasValue
            && (NewPartner.CommissionRate < 0 || NewPartner.CommissionRate > 100))
        {
            ErrorMessage = "Commission rate must be between 0 and 100";
            return RedirectToPage();
        }
        if (NewPartner.ContractEndDate.HasValue && NewPartner.ContractStartDate.HasValue
            && NewPartner.ContractEndDate < NewPartner.ContractStartDate)
        {
            ErrorMessage = "Contract end date cannot be before start date";
            return RedirectToPage();
        }

        _context.Partners.Add(new Partner
        {
            CompanyName = NewPartner.CompanyName.Trim(),
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

        if (string.IsNullOrWhiteSpace(EditPartner.CompanyName))
        {
            ErrorMessage = "Company name is required";
            return RedirectToPage();
        }
        if (EditPartner.CommissionRate.HasValue
            && (EditPartner.CommissionRate < 0 || EditPartner.CommissionRate > 100))
        {
            ErrorMessage = "Commission rate must be between 0 and 100";
            return RedirectToPage();
        }
        if (EditPartner.ContractEndDate.HasValue && EditPartner.ContractStartDate.HasValue
            && EditPartner.ContractEndDate < EditPartner.ContractStartDate)
        {
            ErrorMessage = "Contract end date cannot be before start date";
            return RedirectToPage();
        }

        partner.CompanyName = EditPartner.CompanyName.Trim();
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

    // ══════════════════════════════════════════════════════════════
    //  Invite: create user account linked to Customer/Partner entity
    // ══════════════════════════════════════════════════════════════

    public async Task<IActionResult> OnPostInviteCustomerAsync(int customerId, string? password)
    {
        var customer = await _context.Customers.FindAsync(customerId);
        if (customer == null)
        {
            ErrorMessage = "Customer not found";
            return RedirectToPage();
        }

        if (string.IsNullOrEmpty(customer.Email))
        {
            ErrorMessage = $"Customer '{customer.FullName}' has no email. Edit customer first.";
            return RedirectToPage();
        }

        if (!string.IsNullOrEmpty(customer.UserId))
        {
            ErrorMessage = $"Customer '{customer.FullName}' already has a user account";
            return RedirectToPage();
        }

        var existing = await _userManager.FindByEmailAsync(customer.Email);
        if (existing != null)
        {
            // Just link the existing user
            customer.UserId = existing.Id;
            await _context.SaveChangesAsync();
            if (!await _userManager.IsInRoleAsync(existing, UserRoles.Customer))
                await _userManager.AddToRoleAsync(existing, UserRoles.Customer);
            StatusMessage = $"Linked existing user '{customer.Email}' to customer '{customer.FullName}'";
            return RedirectToPage();
        }

        var defaultPwd = string.IsNullOrWhiteSpace(password) ? GenerateTempPassword() : password;

        var user = new ApplicationUser
        {
            UserName = customer.Email,
            Email = customer.Email,
            FullName = customer.FullName,
            PhoneNumber = customer.Phone,
            LineUserId = customer.LineUserId,
            IsActive = true,
            EmailConfirmed = true
        };

        var result = await _userManager.CreateAsync(user, defaultPwd);
        if (!result.Succeeded)
        {
            ErrorMessage = "Failed to create user: " + string.Join(", ", result.Errors.Select(e => e.Description));
            return RedirectToPage();
        }

        await _userManager.AddToRoleAsync(user, UserRoles.Customer);

        customer.UserId = user.Id;
        await _context.SaveChangesAsync();

        StatusMessage = $"Account created for {customer.Email} | Temp password: {defaultPwd}";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostInvitePartnerAsync(int partnerId, string? password, string? firstName, string? lastName)
    {
        var partner = await _context.Partners.FindAsync(partnerId);
        if (partner == null)
        {
            ErrorMessage = "Partner not found";
            return RedirectToPage();
        }

        if (string.IsNullOrEmpty(partner.ContactEmail))
        {
            ErrorMessage = $"Partner '{partner.CompanyName}' has no contact email";
            return RedirectToPage();
        }

        var existing = await _userManager.FindByEmailAsync(partner.ContactEmail);
        if (existing != null)
        {
            existing.PartnerId = partner.Id;
            await _userManager.UpdateAsync(existing);
            if (!await _userManager.IsInRoleAsync(existing, UserRoles.Partner))
                await _userManager.AddToRoleAsync(existing, UserRoles.Partner);
            StatusMessage = $"Linked existing user '{partner.ContactEmail}' to partner '{partner.CompanyName}'";
            return RedirectToPage();
        }

        var defaultPwd = string.IsNullOrWhiteSpace(password) ? GenerateTempPassword() : password;

        var user = new ApplicationUser
        {
            UserName = partner.ContactEmail,
            Email = partner.ContactEmail,
            FullName = string.IsNullOrEmpty(firstName) && string.IsNullOrEmpty(lastName)
                ? partner.CompanyName
                : $"{firstName} {lastName}".Trim(),
            CompanyName = partner.CompanyName,
            PhoneNumber = partner.ContactPhone,
            PartnerId = partner.Id,
            IsActive = true,
            EmailConfirmed = true
        };

        var result = await _userManager.CreateAsync(user, defaultPwd);
        if (!result.Succeeded)
        {
            ErrorMessage = "Failed to create user: " + string.Join(", ", result.Errors.Select(e => e.Description));
            return RedirectToPage();
        }

        await _userManager.AddToRoleAsync(user, UserRoles.Partner);

        StatusMessage = $"Account created for {partner.ContactEmail} | Temp password: {defaultPwd}";
        return RedirectToPage();
    }

    private static string GenerateTempPassword()
    {
        // Satisfies Identity requirements: uppercase, lowercase, digit, special, 8+
        var rnd = new Random();
        var lower = "abcdefghijkmnopqrstuvwxyz";
        var upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        var digits = "23456789";
        var special = "!@#$%&*";
        var pwd =
            upper[rnd.Next(upper.Length)].ToString() +
            lower[rnd.Next(lower.Length)] +
            lower[rnd.Next(lower.Length)] +
            lower[rnd.Next(lower.Length)] +
            lower[rnd.Next(lower.Length)] +
            digits[rnd.Next(digits.Length)] +
            digits[rnd.Next(digits.Length)] +
            special[rnd.Next(special.Length)];
        return pwd;
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
