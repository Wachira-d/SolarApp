using Microsoft.AspNetCore.Identity;

namespace SolarApp.Models;

public class ApplicationUser : IdentityUser
{
    public string FullName { get; set; } = string.Empty;
    public string? CompanyName { get; set; }
    public string? LineUserId { get; set; }
    public string? ProfileImageUrl { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
    public bool IsActive { get; set; } = true;

    // Navigation properties
    public ICollection<Installation> OwnedInstallations { get; set; } = new List<Installation>();
    public ICollection<Installation> ManagedInstallations { get; set; } = new List<Installation>();
}
