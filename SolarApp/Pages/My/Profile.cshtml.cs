using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;

namespace SolarApp.Pages.My;

/// <summary>
/// Customer's own profile.  Lets them update their contact details and
/// (optionally) LINE-user id without going through an operator.
/// Read-only: email (identity), name (set by operator on signup), and
/// the linked installations list.
/// </summary>
[Authorize]
public class ProfileModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;

    public ProfileModel(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    [BindProperty] public InputModel Input { get; set; } = new();

    public string Email { get; private set; } = "";
    public string FullName { get; private set; } = "";
    public List<string> SiteNames { get; private set; } = new();

    [TempData] public string? StatusMessage { get; set; }

    public class InputModel
    {
        [Required] public string Phone { get; set; } = "";
        public string? Address { get; set; }
        public string? City { get; set; }
        public string? Province { get; set; }
        public string? PostalCode { get; set; }
        public string? LineUserId { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Forbid();
        Email = user.Email ?? user.UserName ?? "";

        var customer = await _context.Customers
            .Include(c => c.Installations)
            .FirstOrDefaultAsync(c => c.UserId == user.Id);
        if (customer != null)
        {
            FullName = $"{customer.FirstName} {customer.LastName}".Trim();
            Input.Phone = customer.Phone;
            Input.Address = customer.Address;
            Input.City = customer.City;
            Input.Province = customer.Province;
            Input.PostalCode = customer.PostalCode;
            Input.LineUserId = customer.LineUserId;
            SiteNames = customer.Installations.Select(i => i.SiteName).ToList();
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Forbid();

        var customer = await _context.Customers.FirstOrDefaultAsync(c => c.UserId == user.Id);
        if (customer == null) return NotFound();

        if (!ModelState.IsValid) return Page();

        customer.Phone = Input.Phone.Trim();
        customer.Address = string.IsNullOrWhiteSpace(Input.Address) ? customer.Address : Input.Address.Trim();
        customer.City = Input.City?.Trim();
        customer.Province = Input.Province?.Trim();
        customer.PostalCode = Input.PostalCode?.Trim();
        customer.LineUserId = string.IsNullOrWhiteSpace(Input.LineUserId) ? null : Input.LineUserId.Trim();
        customer.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        StatusMessage = "บันทึกข้อมูลแล้ว";
        return RedirectToPage();
    }
}
