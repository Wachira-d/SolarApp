namespace SolarApp.Models;

public class Partner
{
    public int Id { get; set; }
    public string CompanyName { get; set; } = string.Empty;
    public string? TaxId { get; set; }
    public string ContactEmail { get; set; } = string.Empty;
    public string ContactPhone { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Province { get; set; }
    public string? PostalCode { get; set; }
    public string? LogoUrl { get; set; }

    public bool IsActive { get; set; } = true;
    public decimal? CommissionRate { get; set; }
    public DateTime? ContractStartDate { get; set; }
    public DateTime? ContractEndDate { get; set; }

    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<ApplicationUser> Users { get; set; } = new List<ApplicationUser>();
    public ICollection<Installation> Installations { get; set; } = new List<Installation>();
}
