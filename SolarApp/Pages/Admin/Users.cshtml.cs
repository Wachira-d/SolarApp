using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Pages.Admin;

[Authorize(Roles = "Admin")]
public class UsersModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;

    public UsersModel(UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager)
    {
        _userManager = userManager;
        _roleManager = roleManager;
    }

    public List<UserViewModel> Users { get; set; } = new();

    [BindProperty]
    public CreateUserInput NewUser { get; set; } = new();

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

    public async Task OnGetAsync()
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
    }

    public async Task<IActionResult> OnPostAsync()
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
        {
            await _userManager.AddToRoleAsync(user, NewUser.Role);
        }
        else
        {
            foreach (var error in result.Errors)
                ModelState.AddModelError(string.Empty, error.Description);
        }

        return RedirectToPage();
    }
}
