using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;

namespace SolarApp.Pages.My;

/// <summary>
/// Customer self-service for notification preferences.  Lets the customer
/// flip the per-installation NotifyOn* toggles, set their own LINE
/// group/user id, and configure quiet hours without going through an
/// operator.  Operators retain the same fields on Edit Installation —
/// this is a customer-facing window into the same columns.
/// </summary>
[Authorize]
public class NotificationsModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;

    public NotificationsModel(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    public List<InstallationPrefsVm> Items { get; private set; } = new();

    [TempData] public string? StatusMessage { get; set; }

    public class InstallationPrefsVm
    {
        public int Id { get; set; }
        public string SiteName { get; set; } = "";
        public string? LineGroupId { get; set; }
        public bool NotifyOnGridLoss { get; set; }
        public bool NotifyOnLowProduction { get; set; }
        public bool NotifyOnCleaningDue { get; set; }
        public bool NotifyOnWarrantyExpiry { get; set; }
        public bool NotifyOnRecovery { get; set; }
        public TimeOnly? QuietHoursStart { get; set; }
        public TimeOnly? QuietHoursEnd { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Forbid();
        await LoadAsync(user.Id);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync([FromForm] List<InstallationPrefsVm> items)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Forbid();

        var ownedIds = await _context.Installations
            .Where(i => i.Customer.UserId == user.Id)
            .Select(i => i.Id)
            .ToListAsync();

        foreach (var vm in items ?? new())
        {
            // Strict ownership check — never trust the form id alone.
            if (!ownedIds.Contains(vm.Id)) continue;

            var install = await _context.Installations.FirstOrDefaultAsync(i => i.Id == vm.Id);
            if (install == null) continue;

            install.LineGroupId = string.IsNullOrWhiteSpace(vm.LineGroupId) ? null : vm.LineGroupId.Trim();
            install.NotifyOnGridLoss = vm.NotifyOnGridLoss;
            install.NotifyOnLowProduction = vm.NotifyOnLowProduction;
            install.NotifyOnCleaningDue = vm.NotifyOnCleaningDue;
            install.NotifyOnWarrantyExpiry = vm.NotifyOnWarrantyExpiry;
            install.NotifyOnRecovery = vm.NotifyOnRecovery;
            install.QuietHoursStart = vm.QuietHoursStart;
            install.QuietHoursEnd = vm.QuietHoursEnd;
            install.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();
        StatusMessage = "บันทึกการตั้งค่าแล้ว";
        return RedirectToPage();
    }

    private async Task LoadAsync(string userId)
    {
        Items = await _context.Installations
            .Where(i => i.Customer.UserId == userId)
            .OrderBy(i => i.SiteName)
            .Select(i => new InstallationPrefsVm
            {
                Id = i.Id,
                SiteName = i.SiteName,
                LineGroupId = i.LineGroupId,
                NotifyOnGridLoss = i.NotifyOnGridLoss,
                NotifyOnLowProduction = i.NotifyOnLowProduction,
                NotifyOnCleaningDue = i.NotifyOnCleaningDue,
                NotifyOnWarrantyExpiry = i.NotifyOnWarrantyExpiry,
                NotifyOnRecovery = i.NotifyOnRecovery,
                QuietHoursStart = i.QuietHoursStart,
                QuietHoursEnd = i.QuietHoursEnd,
            })
            .ToListAsync();
    }
}
