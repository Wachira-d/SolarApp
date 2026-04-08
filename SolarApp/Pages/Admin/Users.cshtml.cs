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

    [BindProperty]
    public CreateUserInput NewUser { get; set; } = new();

    [BindProperty]
    public CreateCustomerInput NewCustomer { get; set; } = new();

    [BindProperty]
    public CreatePartnerInput NewPartner { get; set; } = new();

    public class UserViewModel
    {
        public string Id { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? CompanyName { get; set; }
        public string Role { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class CreateUserInput
    {
        public string FullName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string? CompanyName { get; set; }
        public string? Phone { get; set; }
        public string? LineUserId { get; set; }
        public string Role { get; set; } = UserRoles.Customer;
    }

    public class CreateCustomerInput
    {
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
    }

    public class CreatePartnerInput
    {
        public string CompanyName { get; set; } = string.Empty;
        public string ContactEmail { get; set; } = string.Empty;
        public string ContactPhone { get; set; } = string.Empty;
        public string? Address { get; set; }
        public string? City { get; set; }
        public string? Province { get; set; }
        public decimal? CommissionRate { get; set; }
        public string? TaxId { get; set; }
    }

    public async Task OnGetAsync()
    {
        await LoadDataAsync();
    }

    public async Task<IActionResult> OnPostCreateUserAsync()
    {
        var user = new ApplicationUser
        {
            UserName = NewUser.Email,
            Email = NewUser.Email,
            FullName = NewUser.FullName,
            CompanyName = NewUser.CompanyName,
            PhoneNumber = NewUser.Phone,
            LineUserId = NewUser.LineUserId,
            EmailConfirmed = true
        };

        var result = await _userManager.CreateAsync(user, NewUser.Password);
        if (result.Succeeded)
            await _userManager.AddToRoleAsync(user, NewUser.Role);

        return RedirectToPage();
    }

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
            TaxId = NewCustomer.TaxId
        });
        await _context.SaveChangesAsync();
        return RedirectToPage();
    }

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
            CommissionRate = NewPartner.CommissionRate,
            TaxId = NewPartner.TaxId
        });
        await _context.SaveChangesAsync();
        return RedirectToPage();
    }

    private async Task LoadDataAsync()
    {
        var users = await _userManager.Users.OrderByDescending(u => u.CreatedAt).ToListAsync();
        foreach (var u in users)
        {
            var roles = await _userManager.GetRolesAsync(u);
            Users.Add(new UserViewModel
            {
                Id = u.Id,
                FullName = u.FullName,
                Email = u.Email ?? "",
                CompanyName = u.CompanyName,
                Role = roles.FirstOrDefault() ?? "-",
                IsActive = u.IsActive,
                CreatedAt = u.CreatedAt
            });
        }

        CustomerList = await _context.Customers.OrderByDescending(c => c.CreatedAt).ToListAsync();
        PartnerList = await _context.Partners.OrderByDescending(p => p.CreatedAt).ToListAsync();
    }
}
