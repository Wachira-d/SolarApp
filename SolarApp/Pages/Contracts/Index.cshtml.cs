using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Pages.Contracts;

/// <summary>
/// Which sites we are still being paid to look after, and who owes us money.
///
/// Neither question had an answer in the app. A contract that lapsed silently
/// kept generating alerts, cleaning reminders and truck rolls for a customer
/// who had stopped paying, and the invoice trail lived in a spreadsheet, which
/// is where an unpaid year goes unnoticed.
/// </summary>
[Authorize(Roles = "Admin")]
public class IndexModel : PageModel
{
    private readonly ApplicationDbContext _context;

    public IndexModel(ApplicationDbContext context) => _context = context;

    public List<ServiceContract> Contracts { get; private set; } = new();
    public List<ContractInvoice> OverdueInvoices { get; private set; } = new();
    public List<Installation> SiteOptions { get; private set; } = new();

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    /// <summary>null = all; otherwise only contracts in this state.</summary>
    [BindProperty(SupportsGet = true)] public ContractStatus? Status { get; set; }
    /// <summary>Only contracts ending within 60 days (or already ended while Active).</summary>
    [BindProperty(SupportsGet = true)] public bool ExpiringOnly { get; set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    // ── Headline numbers ──
    public decimal AnnualRunRate { get; private set; }
    public int ActiveCount { get; private set; }
    public int ExpiringCount { get; private set; }
    public decimal OutstandingThb { get; private set; }
    /// <summary>Live sites with no contract row at all — work we may be giving away.</summary>
    public int UncoveredSites { get; private set; }

    public async Task OnGetAsync()
    {
        var query = _context.ServiceContracts.AsNoTracking()
            .Include(c => c.Installation).ThenInclude(i => i.Customer)
            .Include(c => c.Invoices)
            .AsQueryable();

        if (Status.HasValue)
            query = query.Where(c => c.Status == Status.Value);

        if (!string.IsNullOrWhiteSpace(Q))
        {
            var q = Q.Trim();
            query = query.Where(c =>
                (c.ContractNumber != null && c.ContractNumber.Contains(q))
                || c.Installation.SiteName.Contains(q)
                || c.Installation.SiteCode.Contains(q)
                || c.Installation.Customer.FirstName.Contains(q)
                || c.Installation.Customer.LastName.Contains(q));
        }

        var all = await query.ToListAsync();

        // NeedsAttention is computed in C#, so the filter has to be too.
        Contracts = (ExpiringOnly ? all.Where(c => c.NeedsAttention) : all)
            // Soonest to expire first — that's the working order of this page.
            // Open-ended contracts have nothing to chase, so they sort last.
            .OrderBy(c => c.EndDate ?? DateTime.MaxValue)
            .ThenBy(c => c.Installation.SiteName)
            .ToList();

        ActiveCount = all.Count(c => c.Status == ContractStatus.Active);
        ExpiringCount = all.Count(c => c.NeedsAttention);
        AnnualRunRate = all.Where(c => c.Status == ContractStatus.Active).Sum(c => c.AnnualValue);

        OverdueInvoices = await _context.ContractInvoices.AsNoTracking()
            .Include(i => i.ServiceContract).ThenInclude(c => c.Installation)
            .Where(i => i.PaidDate == null && i.DueDate < DateTime.UtcNow.Date)
            .OrderBy(i => i.DueDate)
            .Take(50)
            .ToListAsync();

        OutstandingThb = OverdueInvoices.Sum(i => i.Amount);

        var contractedSiteIds = await _context.ServiceContracts
            .Where(c => c.Status == ContractStatus.Active)
            .Select(c => c.InstallationId)
            .Distinct()
            .ToListAsync();

        UncoveredSites = await _context.Installations
            .CountAsync(i => i.Status != InstallationStatus.Inactive
                             && !contractedSiteIds.Contains(i.Id));

        SiteOptions = await _context.Installations.AsNoTracking()
            .Include(i => i.Customer)
            .Where(i => i.Status != InstallationStatus.Inactive)
            .OrderBy(i => i.SiteName)
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync(
        int installationId, string? contractNumber, DateTime startDate, DateTime? endDate,
        ContractBillingCycle billingCycle, decimal feePerCycle, bool autoRenew, string? notes)
    {
        if (!await _context.Installations.AnyAsync(i => i.Id == installationId))
        {
            ErrorMessage = "ไม่พบไซต์งานที่เลือก";
            return RedirectToPage();
        }

        if (endDate.HasValue && endDate.Value.Date < startDate.Date)
        {
            ErrorMessage = "วันสิ้นสุดสัญญาต้องไม่มาก่อนวันเริ่มสัญญา";
            return RedirectToPage();
        }

        _context.ServiceContracts.Add(new ServiceContract
        {
            InstallationId = installationId,
            ContractNumber = string.IsNullOrWhiteSpace(contractNumber) ? null : contractNumber.Trim(),
            StartDate = startDate.Date,
            EndDate = endDate?.Date,
            BillingCycle = billingCycle,
            FeePerCycle = feePerCycle,
            AutoRenew = autoRenew,
            Notes = notes,
            Status = ContractStatus.Active
        });
        await _context.SaveChangesAsync();

        StatusMessage = "บันทึกสัญญาแล้ว";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSetStatusAsync(int contractId, ContractStatus newStatus)
    {
        var contract = await _context.ServiceContracts.FindAsync(contractId);
        if (contract == null)
        {
            ErrorMessage = "ไม่พบสัญญา";
            return RedirectToPage();
        }

        contract.Status = newStatus;
        contract.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        StatusMessage = $"เปลี่ยนสถานะสัญญาเป็น {newStatus} แล้ว";
        return RedirectToPage();
    }

    /// <summary>
    /// Renews in place by pushing the end date out one billing term and
    /// leaving everything else alone. Creating a fresh row would break the
    /// link from the invoices already raised against this agreement.
    /// </summary>
    public async Task<IActionResult> OnPostRenewAsync(int contractId)
    {
        var contract = await _context.ServiceContracts.FindAsync(contractId);
        if (contract == null)
        {
            ErrorMessage = "ไม่พบสัญญา";
            return RedirectToPage();
        }

        // Extend from whichever is later: the old end date, or today. Renewing
        // a contract that lapsed six months ago should not hand the customer
        // six months of free cover.
        var basis = contract.EndDate.HasValue && contract.EndDate.Value.Date > DateTime.UtcNow.Date
            ? contract.EndDate.Value.Date
            : DateTime.UtcNow.Date;

        contract.EndDate = contract.BillingCycle switch
        {
            ContractBillingCycle.Monthly => basis.AddMonths(1),
            ContractBillingCycle.Quarterly => basis.AddMonths(3),
            _ => basis.AddYears(1)
        };
        contract.Status = ContractStatus.Active;
        contract.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        StatusMessage = $"ต่อสัญญาถึง {contract.EndDate:dd/MM/yyyy} แล้ว";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddInvoiceAsync(
        int contractId, string? invoiceNumber, DateTime periodStart, DateTime periodEnd,
        DateTime dueDate, decimal amount, string? notes)
    {
        var contract = await _context.ServiceContracts.FindAsync(contractId);
        if (contract == null)
        {
            ErrorMessage = "ไม่พบสัญญา";
            return RedirectToPage();
        }

        _context.ContractInvoices.Add(new ContractInvoice
        {
            ServiceContractId = contractId,
            InvoiceNumber = string.IsNullOrWhiteSpace(invoiceNumber) ? null : invoiceNumber.Trim(),
            PeriodStart = periodStart.Date,
            PeriodEnd = periodEnd.Date,
            IssuedDate = DateTime.UtcNow.Date,
            DueDate = dueDate.Date,
            Amount = amount,
            Notes = notes
        });
        await _context.SaveChangesAsync();

        StatusMessage = "บันทึกใบแจ้งหนี้แล้ว";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostMarkPaidAsync(int invoiceId, decimal? paidAmount)
    {
        var invoice = await _context.ContractInvoices.FindAsync(invoiceId);
        if (invoice == null)
        {
            ErrorMessage = "ไม่พบใบแจ้งหนี้";
            return RedirectToPage();
        }

        invoice.PaidDate = DateTime.UtcNow.Date;
        // A short payment is recorded as what actually arrived, not as the
        // amount billed — otherwise the shortfall disappears.
        invoice.PaidAmount = paidAmount is > 0 ? paidAmount : invoice.Amount;
        await _context.SaveChangesAsync();

        StatusMessage = "บันทึกการชำระเงินแล้ว";
        return RedirectToPage();
    }
}
