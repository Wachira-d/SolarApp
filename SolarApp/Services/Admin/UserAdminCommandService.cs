using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Services.Admin;

public record CommandResult(bool Success, string Message)
{
    public static CommandResult Ok(string message) => new(true, message);
    public static CommandResult Fail(string message) => new(false, message);
}

/// <summary>
/// Command service extracted from UsersModel page handlers.
/// Preserves the exact validation order + error messages of the original code
/// so handlers can dispatch with no behavior change. The page model keeps its
/// HttpContext-coupled logic (current user lookup) inline and passes results in.
/// </summary>
public interface IUserAdminCommandService
{
    Task<CommandResult> CreateUserAsync(UserAdminInput input);
    Task<CommandResult> EditUserAsync(UserAdminInput input, string? currentUserId);
    Task<CommandResult> DeleteUserAsync(string userId);
    Task<CommandResult> ToggleUserActiveAsync(string userId);
    Task<CommandResult> ResetPasswordAsync(string userId, string newPassword);

    Task<CommandResult> CreateCustomerAsync(CustomerAdminInput input);
    Task<CommandResult> EditCustomerAsync(CustomerAdminInput input);
    Task<CommandResult> DeleteCustomerAsync(int customerId, string? actingUserId = null);

    Task<CommandResult> CreatePartnerAsync(PartnerAdminInput input);
    Task<CommandResult> EditPartnerAsync(PartnerAdminInput input);
    Task<CommandResult> DeletePartnerAsync(int partnerId, string? actingUserId = null);

    Task<CommandResult> InviteCustomerAsync(int customerId, string? password);
    Task<CommandResult> InvitePartnerAsync(int partnerId, string? password, string? firstName, string? lastName);
}

public class UserAdminInput
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

public class CustomerAdminInput
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

public class PartnerAdminInput
{
    public int Id { get; set; }
    public string CompanyName { get; set; } = string.Empty;
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
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

public class UserAdminCommandService : IUserAdminCommandService
{
    private static readonly HashSet<string> AllowedRoles =
        new(StringComparer.OrdinalIgnoreCase) { UserRoles.Admin, UserRoles.Partner, UserRoles.Customer };

    private static readonly Regex EmailRegex =
        new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ApplicationDbContext _context;

    public UserAdminCommandService(UserManager<ApplicationUser> userManager, ApplicationDbContext context)
    {
        _userManager = userManager;
        _context = context;
    }

    // ───────────────────────── USERS ─────────────────────────

    public async Task<CommandResult> CreateUserAsync(UserAdminInput input)
    {
        if (string.IsNullOrEmpty(input.Password))
            return CommandResult.Fail("Password is required for new users");
        if (!AllowedRoles.Contains(input.Role ?? ""))
            return CommandResult.Fail("Invalid role. Must be Admin, Partner, or Customer.");
        if (string.IsNullOrWhiteSpace(input.Email) || !EmailRegex.IsMatch(input.Email))
            return CommandResult.Fail("Valid email is required");
        if (input.Role == UserRoles.Partner && !input.PartnerId.HasValue)
            return CommandResult.Fail("Partner role requires a Partner assignment");

        var user = new ApplicationUser
        {
            UserName = input.Email,
            Email = input.Email,
            FullName = input.FullName,
            CompanyName = input.CompanyName,
            PhoneNumber = input.Phone,
            LineUserId = input.LineUserId,
            PartnerId = input.Role == UserRoles.Partner ? input.PartnerId : null,
            IsActive = true,
            EmailConfirmed = true
        };

        var result = await _userManager.CreateAsync(user, input.Password);
        if (!result.Succeeded)
            return CommandResult.Fail(string.Join(", ", result.Errors.Select(e => e.Description)));

        await _userManager.AddToRoleAsync(user, input.Role!);
        return CommandResult.Ok($"User '{user.Email}' created successfully");
    }

    public async Task<CommandResult> EditUserAsync(UserAdminInput input, string? currentUserId)
    {
        if (string.IsNullOrEmpty(input.Id))
            return CommandResult.Fail("User ID is required");

        var user = await _userManager.FindByIdAsync(input.Id);
        if (user == null)
            return CommandResult.Fail("User not found");

        if (!AllowedRoles.Contains(input.Role ?? ""))
            return CommandResult.Fail("Invalid role. Must be Admin, Partner, or Customer.");

        // Lockout-risk guard: admin cannot demote themselves
        if (currentUserId == user.Id && input.Role != UserRoles.Admin)
            return CommandResult.Fail("You cannot change your own role away from Admin.");

        if (input.Role == UserRoles.Partner && !input.PartnerId.HasValue)
            return CommandResult.Fail("Partner role requires a Partner assignment");

        user.FullName = input.FullName;
        user.Email = input.Email;
        user.UserName = input.Email;
        user.CompanyName = input.CompanyName;
        user.PhoneNumber = input.Phone;
        user.LineUserId = input.LineUserId;
        user.PartnerId = input.Role == UserRoles.Partner ? input.PartnerId : null;
        user.IsActive = input.IsActive;

        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
            return CommandResult.Fail(string.Join(", ", result.Errors.Select(e => e.Description)));

        var currentRoles = await _userManager.GetRolesAsync(user);
        if (!currentRoles.Contains(input.Role!))
        {
            await _userManager.RemoveFromRolesAsync(user, currentRoles);
            await _userManager.AddToRoleAsync(user, input.Role!);
        }

        return CommandResult.Ok($"User '{user.Email}' updated");
    }

    public async Task<CommandResult> DeleteUserAsync(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
            return CommandResult.Fail("User not found");

        // Don't delete the last admin
        var roles = await _userManager.GetRolesAsync(user);
        if (roles.Contains(UserRoles.Admin))
        {
            var adminCount = (await _userManager.GetUsersInRoleAsync(UserRoles.Admin)).Count;
            if (adminCount <= 1)
                return CommandResult.Fail("Cannot delete the last admin user");
        }

        // Unlink Customer first
        var linkedCustomer = await _context.Customers.FirstOrDefaultAsync(c => c.UserId == userId);
        if (linkedCustomer != null)
        {
            linkedCustomer.UserId = null;
            await _context.SaveChangesAsync();
        }

        var result = await _userManager.DeleteAsync(user);
        return result.Succeeded
            ? CommandResult.Ok($"User '{user.Email}' deleted")
            : CommandResult.Fail(string.Join(", ", result.Errors.Select(e => e.Description)));
    }

    public async Task<CommandResult> ToggleUserActiveAsync(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
            return CommandResult.Fail("User not found");

        user.IsActive = !user.IsActive;
        await _userManager.UpdateAsync(user);

        if (!user.IsActive)
            await _userManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
        else
            await _userManager.SetLockoutEndDateAsync(user, null);

        return CommandResult.Ok($"User '{user.Email}' {(user.IsActive ? "activated" : "deactivated")}");
    }

    public async Task<CommandResult> ResetPasswordAsync(string userId, string newPassword)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
            return CommandResult.Fail("User not found");

        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        var result = await _userManager.ResetPasswordAsync(user, token, newPassword);

        return result.Succeeded
            ? CommandResult.Ok($"Password reset for '{user.Email}'")
            : CommandResult.Fail(string.Join(", ", result.Errors.Select(e => e.Description)));
    }

    // ──────────────────────── CUSTOMERS ────────────────────────

    public async Task<CommandResult> CreateCustomerAsync(CustomerAdminInput input)
    {
        if (string.IsNullOrWhiteSpace(input.FirstName) || string.IsNullOrWhiteSpace(input.LastName))
            return CommandResult.Fail("First name and last name are required");
        if (!string.IsNullOrWhiteSpace(input.Email) && !EmailRegex.IsMatch(input.Email))
            return CommandResult.Fail("Invalid email format");

        if (!string.IsNullOrEmpty(input.UserId))
        {
            var conflict = await _context.Customers.AnyAsync(c => c.UserId == input.UserId);
            if (conflict)
                return CommandResult.Fail("That user is already linked to another customer.");
        }

        _context.Customers.Add(new Customer
        {
            FirstName = input.FirstName.Trim(),
            LastName = input.LastName.Trim(),
            Email = input.Email?.Trim() ?? string.Empty,
            Phone = input.Phone,
            LineUserId = input.LineUserId,
            Address = input.Address,
            City = input.City,
            Province = input.Province,
            PostalCode = input.PostalCode,
            TaxId = input.TaxId,
            UserId = input.UserId,
            IsActive = true
        });
        await _context.SaveChangesAsync();
        return CommandResult.Ok("Customer created");
    }

    public async Task<CommandResult> EditCustomerAsync(CustomerAdminInput input)
    {
        var customer = await _context.Customers.FindAsync(input.Id);
        if (customer == null)
            return CommandResult.Fail("Customer not found");

        // Prevent re-assigning a UserId already linked elsewhere
        var newUserId = string.IsNullOrEmpty(input.UserId) ? null : input.UserId;
        if (!string.IsNullOrEmpty(newUserId) && newUserId != customer.UserId)
        {
            var conflict = await _context.Customers
                .AnyAsync(c => c.UserId == newUserId && c.Id != customer.Id);
            if (conflict)
                return CommandResult.Fail("That user is already linked to another customer.");
        }

        if (string.IsNullOrWhiteSpace(input.FirstName) || string.IsNullOrWhiteSpace(input.LastName))
            return CommandResult.Fail("First name and last name are required");
        if (!string.IsNullOrWhiteSpace(input.Email) && !EmailRegex.IsMatch(input.Email))
            return CommandResult.Fail("Invalid email format");

        customer.FirstName = input.FirstName.Trim();
        customer.LastName = input.LastName.Trim();
        customer.Email = input.Email?.Trim() ?? string.Empty;
        customer.Phone = input.Phone;
        customer.LineUserId = input.LineUserId;
        customer.Address = input.Address;
        customer.City = input.City;
        customer.Province = input.Province;
        customer.PostalCode = input.PostalCode;
        customer.TaxId = input.TaxId;
        customer.UserId = newUserId;
        customer.IsActive = input.IsActive;
        customer.Notes = input.Notes;
        customer.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return CommandResult.Ok($"Customer '{customer.FullName}' updated");
    }

    public async Task<CommandResult> DeleteCustomerAsync(int customerId, string? actingUserId = null)
    {
        var customer = await _context.Customers
            .Include(c => c.Installations)
            .FirstOrDefaultAsync(c => c.Id == customerId);

        if (customer == null)
            return CommandResult.Fail("Customer not found");

        if (customer.Installations.Any())
            return CommandResult.Fail($"ลบไม่ได้: ลูกค้ารายนี้ยังมีไซต์งาน {customer.Installations.Count} แห่ง — ลบไซต์งานก่อน");

        // Soft delete: the production history, cleaning records and work orders
        // tied to this customer are the evidence an O&M contract is settled on.
        // A hard DELETE removed them at exactly the moment a dispute needs them.
        customer.DeletedAt = DateTime.UtcNow;
        customer.DeletedByUserId = actingUserId;
        customer.IsActive = false;
        await _context.SaveChangesAsync();
        return CommandResult.Ok($"ลบลูกค้า '{customer.FullName}' แล้ว (เก็บประวัติไว้ในระบบ)");
    }

    // ──────────────────────── PARTNERS ────────────────────────

    public async Task<CommandResult> CreatePartnerAsync(PartnerAdminInput input)
    {
        if (string.IsNullOrWhiteSpace(input.CompanyName))
            return CommandResult.Fail("Company name is required");
        if (input.CommissionRate.HasValue && (input.CommissionRate < 0 || input.CommissionRate > 100))
            return CommandResult.Fail("Commission rate must be between 0 and 100");
        if (input.ContractEndDate.HasValue && input.ContractStartDate.HasValue
            && input.ContractEndDate < input.ContractStartDate)
            return CommandResult.Fail("Contract end date cannot be before start date");

        _context.Partners.Add(new Partner
        {
            CompanyName = input.CompanyName.Trim(),
            ContactEmail = input.ContactEmail ?? string.Empty,
            ContactPhone = input.ContactPhone ?? string.Empty,
            Address = input.Address,
            City = input.City,
            Province = input.Province,
            PostalCode = input.PostalCode,
            CommissionRate = input.CommissionRate,
            TaxId = input.TaxId,
            ContractStartDate = input.ContractStartDate,
            ContractEndDate = input.ContractEndDate,
            IsActive = true
        });
        await _context.SaveChangesAsync();
        return CommandResult.Ok("Partner created");
    }

    public async Task<CommandResult> EditPartnerAsync(PartnerAdminInput input)
    {
        var partner = await _context.Partners.FindAsync(input.Id);
        if (partner == null)
            return CommandResult.Fail("Partner not found");

        if (string.IsNullOrWhiteSpace(input.CompanyName))
            return CommandResult.Fail("Company name is required");
        if (input.CommissionRate.HasValue && (input.CommissionRate < 0 || input.CommissionRate > 100))
            return CommandResult.Fail("Commission rate must be between 0 and 100");
        if (input.ContractEndDate.HasValue && input.ContractStartDate.HasValue
            && input.ContractEndDate < input.ContractStartDate)
            return CommandResult.Fail("Contract end date cannot be before start date");

        partner.CompanyName = input.CompanyName.Trim();
        partner.ContactEmail = input.ContactEmail ?? string.Empty;
        partner.ContactPhone = input.ContactPhone ?? string.Empty;
        partner.Address = input.Address;
        partner.City = input.City;
        partner.Province = input.Province;
        partner.PostalCode = input.PostalCode;
        partner.CommissionRate = input.CommissionRate;
        partner.TaxId = input.TaxId;
        partner.ContractStartDate = input.ContractStartDate;
        partner.ContractEndDate = input.ContractEndDate;
        partner.IsActive = input.IsActive;
        partner.Notes = input.Notes;
        partner.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return CommandResult.Ok($"Partner '{partner.CompanyName}' updated");
    }

    public async Task<CommandResult> DeletePartnerAsync(int partnerId, string? actingUserId = null)
    {
        var partner = await _context.Partners
            .Include(p => p.Installations)
            .Include(p => p.Users)
            .FirstOrDefaultAsync(p => p.Id == partnerId);

        if (partner == null)
            return CommandResult.Fail("Partner not found");

        if (partner.Installations.Any())
            return CommandResult.Fail($"ลบไม่ได้: พาร์ทเนอร์รายนี้ยังดูแลไซต์งาน {partner.Installations.Count} แห่ง");

        // Users still lose their link — they must stop seeing this partner's
        // sites immediately — but the partner row itself is kept so historical
        // work orders can still say who carried the job out.
        foreach (var u in partner.Users)
            u.PartnerId = null;

        partner.DeletedAt = DateTime.UtcNow;
        partner.DeletedByUserId = actingUserId;
        partner.IsActive = false;
        await _context.SaveChangesAsync();
        return CommandResult.Ok($"ลบพาร์ทเนอร์ '{partner.CompanyName}' แล้ว (เก็บประวัติไว้ในระบบ)");
    }

    // ──────────────────────── INVITES ────────────────────────

    public async Task<CommandResult> InviteCustomerAsync(int customerId, string? password)
    {
        var customer = await _context.Customers.FindAsync(customerId);
        if (customer == null)
            return CommandResult.Fail("Customer not found");
        if (string.IsNullOrEmpty(customer.Email))
            return CommandResult.Fail($"Customer '{customer.FullName}' has no email. Edit customer first.");
        if (!string.IsNullOrEmpty(customer.UserId))
            return CommandResult.Fail($"Customer '{customer.FullName}' already has a user account");

        var existing = await _userManager.FindByEmailAsync(customer.Email);
        if (existing != null)
        {
            customer.UserId = existing.Id;
            await _context.SaveChangesAsync();
            if (!await _userManager.IsInRoleAsync(existing, UserRoles.Customer))
                await _userManager.AddToRoleAsync(existing, UserRoles.Customer);
            return CommandResult.Ok($"Linked existing user '{customer.Email}' to customer '{customer.FullName}'");
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
            return CommandResult.Fail("Failed to create user: " + string.Join(", ", result.Errors.Select(e => e.Description)));

        await _userManager.AddToRoleAsync(user, UserRoles.Customer);
        customer.UserId = user.Id;
        await _context.SaveChangesAsync();

        return CommandResult.Ok($"Account created for {customer.Email} | Temp password: {defaultPwd}");
    }

    public async Task<CommandResult> InvitePartnerAsync(int partnerId, string? password, string? firstName, string? lastName)
    {
        var partner = await _context.Partners.FindAsync(partnerId);
        if (partner == null)
            return CommandResult.Fail("Partner not found");
        if (string.IsNullOrEmpty(partner.ContactEmail))
            return CommandResult.Fail($"Partner '{partner.CompanyName}' has no contact email");

        var existing = await _userManager.FindByEmailAsync(partner.ContactEmail);
        if (existing != null)
        {
            existing.PartnerId = partner.Id;
            await _userManager.UpdateAsync(existing);
            if (!await _userManager.IsInRoleAsync(existing, UserRoles.Partner))
                await _userManager.AddToRoleAsync(existing, UserRoles.Partner);
            return CommandResult.Ok($"Linked existing user '{partner.ContactEmail}' to partner '{partner.CompanyName}'");
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
            return CommandResult.Fail("Failed to create user: " + string.Join(", ", result.Errors.Select(e => e.Description)));

        await _userManager.AddToRoleAsync(user, UserRoles.Partner);
        return CommandResult.Ok($"Account created for {partner.ContactEmail} | Temp password: {defaultPwd}");
    }

    // Identity-compliant temp password generator. Pattern preserved exactly from
    // the original UsersModel.GenerateTempPassword: 1 upper + 4 lower + 2 digits + 1 special.
    internal static string GenerateTempPassword()
    {
        var rnd = new Random();
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string digits = "23456789";
        const string special = "!@#$%&*";
        return
            upper[rnd.Next(upper.Length)].ToString() +
            lower[rnd.Next(lower.Length)] +
            lower[rnd.Next(lower.Length)] +
            lower[rnd.Next(lower.Length)] +
            lower[rnd.Next(lower.Length)] +
            digits[rnd.Next(digits.Length)] +
            digits[rnd.Next(digits.Length)] +
            special[rnd.Next(special.Length)];
    }
}
