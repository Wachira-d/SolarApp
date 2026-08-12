namespace SolarApp.Models;

public class Customer : ISoftDeletable
{
    public int Id { get; set; }
    public string? UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string FullName => $"{FirstName} {LastName}";
    public string? Email { get; set; }
    public string Phone { get; set; } = string.Empty;
    public string? LineUserId { get; set; }

    public string Address { get; set; } = string.Empty;
    public string? City { get; set; }
    public string? Province { get; set; }
    public string? PostalCode { get; set; }
    public string? TaxId { get; set; }

    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Installation> Installations { get; set; } = new List<Installation>();

    // ── Soft delete (see ISoftDeletable) ──
    public DateTime? DeletedAt { get; set; }
    public string? DeletedByUserId { get; set; }

}
