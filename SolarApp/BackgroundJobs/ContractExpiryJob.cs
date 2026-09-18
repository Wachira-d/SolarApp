using Hangfire;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.BackgroundJobs;

/// <summary>
/// Marks lapsed contracts and raises an alert before they lapse.
///
/// A contract page nobody opens is a spreadsheet with extra steps. The failure
/// this prevents is specific and expensive: a contract ends, nobody notices,
/// and the crew keeps cleaning panels and answering alerts for a site that
/// stopped paying — sometimes for a year, because nothing in the system ever
/// said otherwise.
///
/// Two things happen daily:
///   • Contracts past their end date and still marked Active become Expired,
///     unless AutoRenew is set, in which case the end date rolls forward.
///   • Contracts inside 60 days of ending raise an alert against their site,
///     so it appears in the same list as everything else needing action.
///
/// Sixty days because renewing a Thai O&amp;M contract means getting a
/// signature from someone who is not in a hurry.
/// </summary>
public class ContractExpiryJob
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<ContractExpiryJob> _logger;

    public ContractExpiryJob(ApplicationDbContext context, ILogger<ContractExpiryJob> logger)
    {
        _context = context;
        _logger = logger;
    }

    private const int WarnWithinDays = 60;
    private const string AlertTitlePrefix = "สัญญาบริการใกล้หมดอายุ: ";

    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task ExecuteAsync()
    {
        var today = Services.SiteTime.Today;

        var contracts = await _context.ServiceContracts
            .Include(c => c.Installation)
            .Where(c => c.Status == ContractStatus.Active && c.EndDate != null)
            .ToListAsync();

        var renewed = 0;
        var expired = 0;
        var warned = 0;

        foreach (var contract in contracts)
        {
            var end = contract.EndDate!.Value.Date;

            if (end < today)
            {
                if (contract.AutoRenew)
                {
                    // Roll forward until it's in the future. A job that didn't
                    // run for two months would otherwise leave the contract
                    // still expired after "renewing" it once.
                    while (contract.EndDate!.Value.Date < today)
                    {
                        contract.EndDate = contract.BillingCycle switch
                        {
                            ContractBillingCycle.Monthly => contract.EndDate.Value.AddMonths(1),
                            ContractBillingCycle.Quarterly => contract.EndDate.Value.AddMonths(3),
                            _ => contract.EndDate.Value.AddYears(1)
                        };
                    }
                    contract.UpdatedAt = DateTime.UtcNow;
                    renewed++;
                }
                else
                {
                    contract.Status = ContractStatus.Expired;
                    contract.UpdatedAt = DateTime.UtcNow;
                    expired++;

                    _logger.LogInformation(
                        "Contract {Id} for {Site} expired on {End} — the site is still being serviced",
                        contract.Id, contract.Installation.SiteName, end);
                }
                continue;
            }

            var daysLeft = (int)Math.Floor((end - today).TotalDays);
            if (daysLeft > WarnWithinDays) continue;

            // One open alert per contract, not one per day.
            var alreadyOpen = await _context.SystemAlerts.AnyAsync(a =>
                a.InstallationId == contract.InstallationId
                && a.AlertType == AlertType.WarrantyExpiring
                && a.ResolvedAt == null
                && a.Title.StartsWith(AlertTitlePrefix));
            if (alreadyOpen) continue;

            _context.SystemAlerts.Add(new SystemAlert
            {
                InstallationId = contract.InstallationId,
                // Reusing WarrantyExpiring rather than adding an alert type:
                // it already routes as a scheduled reminder rather than a
                // fault, which is exactly what this is — nothing is broken.
                AlertType = AlertType.WarrantyExpiring,
                Severity = daysLeft <= 14 ? AlertSeverity.High : AlertSeverity.Medium,
                Title = $"{AlertTitlePrefix}{contract.Installation.SiteName}",
                Message =
                    $"สัญญา {contract.ContractNumber ?? "#" + contract.Id} สิ้นสุด {end:dd/MM/yyyy} "
                    + $"(อีก {daysLeft} วัน) · ค่าบริการ {contract.FeePerCycle:N0} บาท/รอบ"
                    + (contract.AutoRenew ? " · ตั้งค่าต่อสัญญาอัตโนมัติไว้" : ""),
                RootCauseAnalysis = contract.AutoRenew
                    ? "สัญญานี้จะต่ออายุอัตโนมัติ — เตรียมออกใบแจ้งหนี้รอบถัดไป "
                      + "หากลูกค้าไม่ต้องการต่อ ต้องแจ้งยกเลิกก่อนวันสิ้นสุด"
                    : "ต้องติดต่อลูกค้าเพื่อต่อสัญญา — ถ้าปล่อยให้หมดอายุ "
                      + "ระบบจะยังส่งแจ้งเตือนและงานบำรุงรักษาให้ไซต์นี้ต่อไปโดยไม่มีการเรียกเก็บเงิน",
                OccurredAt = DateTime.UtcNow,
                IsNotified = false
            });
            warned++;
        }

        // Close alerts for contracts that have since been renewed or ended.
        // Without this the alert stays open forever and the dedupe guard above
        // then blocks the NEXT expiry from ever warning — the same trap the
        // cleaning-due check had to be fixed for.
        var stillWarned = contracts
            .Where(c => c.EndDate.HasValue
                        && c.EndDate.Value.Date >= today
                        && (c.EndDate.Value.Date - today).TotalDays <= WarnWithinDays)
            .Select(c => c.InstallationId)
            .ToHashSet();

        var openContractAlerts = await _context.SystemAlerts
            .Where(a => a.AlertType == AlertType.WarrantyExpiring
                        && a.ResolvedAt == null
                        && a.Title.StartsWith(AlertTitlePrefix))
            .ToListAsync();

        var resolved = 0;
        foreach (var a in openContractAlerts)
        {
            if (stillWarned.Contains(a.InstallationId)) continue;
            a.ResolvedAt = DateTime.UtcNow;
            resolved++;
        }

        if (renewed + expired + warned + resolved > 0)
            await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Contract expiry sweep: {Renewed} auto-renewed, {Expired} marked expired, "
            + "{Warned} expiry alert(s) raised, {Resolved} closed",
            renewed, expired, warned, resolved);
    }
}
