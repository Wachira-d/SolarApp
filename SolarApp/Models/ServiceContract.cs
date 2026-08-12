using System.ComponentModel.DataAnnotations;
using SolarApp.Models.Enums;

namespace SolarApp.Models;

/// <summary>
/// The commercial agreement behind a site.
///
/// The system knew when a warranty expired and never when a service contract
/// did — so the question "who are we still being paid to look after" had no
/// answer in the app, and a contract that lapsed silently kept generating
/// alerts, cleaning reminders and truck rolls for a customer who had stopped
/// paying. It also had no idea who owed money: the invoice trail lived in
/// someone's spreadsheet, which is where an unpaid year goes unnoticed.
///
/// One contract covers one site. A customer with three sites on one piece of
/// paper gets three rows — the alternative is a contract-to-site join table
/// for a case that resolves fine by giving them the same reference number,
/// and this way "is this site covered" stays a single lookup.
/// </summary>
public class ServiceContract
{
    public int Id { get; set; }

    public int InstallationId { get; set; }
    public Installation Installation { get; set; } = null!;

    /// <summary>Whatever the paperwork calls it — quoted back on invoices and in disputes.</summary>
    [StringLength(50)]
    public string? ContractNumber { get; set; }

    [Required]
    public DateTime StartDate { get; set; }

    /// <summary>Null = open-ended (rolling monthly, no agreed end).</summary>
    public DateTime? EndDate { get; set; }

    public ContractBillingCycle BillingCycle { get; set; } = ContractBillingCycle.Yearly;

    /// <summary>Fee per billing cycle, in THB.</summary>
    public decimal FeePerCycle { get; set; }

    public ContractStatus Status { get; set; } = ContractStatus.Active;

    /// <summary>
    /// True when the contract rolls over on its end date unless cancelled.
    /// Changes what an approaching end date means: a reminder to invoice
    /// rather than a reminder to chase a signature.
    /// </summary>
    public bool AutoRenew { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<ContractInvoice> Invoices { get; set; } = new List<ContractInvoice>();

    // ─── Computed ───

    public bool IsExpired => EndDate.HasValue && EndDate.Value.Date < DateTime.UtcNow.Date;

    /// <summary>Days until the contract ends; negative once past. Null when open-ended.</summary>
    public int? DaysRemaining => EndDate.HasValue
        ? (int)Math.Floor((EndDate.Value.Date - DateTime.UtcNow.Date).TotalDays)
        : null;

    /// <summary>
    /// Ending within 60 days, or already ended while still marked Active.
    /// Sixty days because renewing a Thai O&amp;M contract means getting a
    /// signature from someone who is not in a hurry.
    /// </summary>
    public bool NeedsAttention =>
        Status == ContractStatus.Active
        && DaysRemaining.HasValue
        && DaysRemaining.Value <= 60;

    /// <summary>Annualised fee, so contracts on different cycles can be compared or summed.</summary>
    public decimal AnnualValue => BillingCycle switch
    {
        ContractBillingCycle.Monthly => FeePerCycle * 12,
        ContractBillingCycle.Quarterly => FeePerCycle * 4,
        ContractBillingCycle.Yearly => FeePerCycle,
        ContractBillingCycle.OneTime => 0,   // not recurring revenue; excluded from run-rate
        _ => FeePerCycle
    };
}

/// <summary>
/// One bill raised against a contract. Deliberately not an accounting system —
/// it records what was invoiced, when it was due, and whether it was paid,
/// which is the part the operations team needs in order to know whether to
/// keep sending trucks. The accounting package remains the source of truth
/// for the money itself.
/// </summary>
public class ContractInvoice
{
    public int Id { get; set; }

    public int ServiceContractId { get; set; }
    public ServiceContract ServiceContract { get; set; } = null!;

    [StringLength(50)]
    public string? InvoiceNumber { get; set; }

    /// <summary>The service period this bill covers, not the date it was raised.</summary>
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }

    public DateTime IssuedDate { get; set; } = DateTime.UtcNow.Date;
    public DateTime DueDate { get; set; }

    public decimal Amount { get; set; }

    public DateTime? PaidDate { get; set; }
    public decimal? PaidAmount { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public bool IsPaid => PaidDate.HasValue;

    /// <summary>Past its due date and unpaid. Paid-late is not overdue — it's settled.</summary>
    public bool IsOverdue => !IsPaid && DueDate.Date < DateTime.UtcNow.Date;

    public int DaysOverdue => IsOverdue
        ? (int)Math.Floor((DateTime.UtcNow.Date - DueDate.Date).TotalDays)
        : 0;
}
