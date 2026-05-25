using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;

namespace SolarApp.Pages.My;

[Authorize]
public class SettingsModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ApplicationDbContext _context;

    public SettingsModel(UserManager<ApplicationUser> userManager, ApplicationDbContext context)
    {
        _userManager = userManager;
        _context = context;
    }

    public string? Email { get; private set; }
    public string? FullName { get; private set; }

    public async Task OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        Email = user?.Email;
        if (user != null)
        {
            var c = await _context.Customers
                .Where(c => c.UserId == user.Id)
                .Select(c => new { c.FirstName, c.LastName })
                .FirstOrDefaultAsync();
            FullName = c == null ? user.UserName : $"{c.FirstName} {c.LastName}".Trim();
        }
    }
}
