using Hangfire;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models.Enums;
using SolarApp.Services.LineApi;

namespace SolarApp.BackgroundJobs;

/// <summary>
/// Once a month, tells each customer what their system did.
///
/// The gap this fills is commercial, not technical. A customer on an O&amp;M
/// contract hears from us when something breaks and never otherwise, so the
/// only evidence they have of the service is a list of problems. Renewal
/// conversations start from there. The data to say "you produced 1,840 kWh and
/// saved ฿7,400 last month, and the system ran every day" already exists — it
/// was simply never sent.
///
/// Deliberately not the operator's view: no PR, no API failure counts, no
/// alert breakdown. A customer told "PR 0.78" learns nothing and worries
/// anyway. Production, money, uptime, and the visits we made.
///
/// Runs on the 1st. Only customers with a LINE ID are reachable; the rest are
/// counted in the log so the operator can see how much of the base is silent.
/// </summary>
public class MonthlyReportJob
{
    private readonly ApplicationDbContext _context;
    private readonly ILineMessagingService _line;
    private readonly ILogger<MonthlyReportJob> _logger;

    public MonthlyReportJob(
        ApplicationDbContext context,
        ILineMessagingService line,
        ILogger<MonthlyReportJob> logger)
    {
        _context = context;
        _line = line;
        _logger = logger;
    }

    // Thailand's grid mix is roughly 0.5 kgCO2 per kWh; a tree absorbs about
    // 21 kg a year. Same constants the customer portal already shows, so the
    // message and the website can't disagree.
    private const decimal Co2KgPerKwh = 0.5m;
    private const decimal Co2KgPerTreeYear = 21m;

    private static readonly string[] ThaiMonths =
    {
        "มกราคม", "กุมภาพันธ์", "มีนาคม", "เมษายน", "พฤษภาคม", "มิถุนายน",
        "กรกฎาคม", "สิงหาคม", "กันยายน", "ตุลาคม", "พฤศจิกายน", "ธันวาคม"
    };

    [DisableConcurrentExecution(timeoutInSeconds: 900)]
    public async Task ExecuteAsync()
    {
        // Report the month that just ended, in site-local time. Using UtcNow
        // here would report the wrong month for the first seven hours of the
        // 1st, which is exactly when this job runs.
        var today = Services.SiteTime.Today;
        var monthStart = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        var prevStart = monthStart.AddMonths(-1);

        var periodLabel = $"{ThaiMonths[monthStart.Month - 1]} {monthStart.Year + 543}";

        var customers = await _context.Customers
            .Include(c => c.Installations)
            .Where(c => c.IsActive && c.LineUserId != null && c.LineUserId != "")
            .ToListAsync();

        var silent = await _context.Customers
            .CountAsync(c => c.IsActive && (c.LineUserId == null || c.LineUserId == ""));

        var sent = 0;
        var skippedNoProduction = 0;

        foreach (var customer in customers)
        {
            try
            {
                var siteIds = customer.Installations.Select(i => i.Id).ToList();
                if (siteIds.Count == 0) continue;

                var daily = await _context.EnergyData.AsNoTracking()
                    .Where(e => siteIds.Contains(e.InstallationId)
                                && e.Granularity == DataGranularity.Daily
                                && e.Timestamp >= prevStart && e.Timestamp <= monthEnd)
                    .Select(e => new { e.Timestamp, e.EnergyGenerationKwh })
                    .ToListAsync();

                var thisMonth = daily.Where(d => d.Timestamp.Date >= monthStart && d.Timestamp.Date <= monthEnd).ToList();
                var totalKwh = thisMonth.Sum(d => d.EnergyGenerationKwh);

                // Nothing produced means nothing to celebrate and probably
                // something wrong. Sending "0 kWh, ประหยัด ฿0" to a paying
                // customer is worse than sending nothing; the operator gets
                // this in the log instead so someone looks at the site.
                if (totalKwh <= 0)
                {
                    skippedNoProduction++;
                    _logger.LogWarning(
                        "Monthly report skipped for customer {Id} ({Name}) — no production recorded for {Period}",
                        customer.Id, customer.FullName, periodLabel);
                    continue;
                }

                var prevKwh = daily
                    .Where(d => d.Timestamp.Date >= prevStart && d.Timestamp.Date < monthStart)
                    .Sum(d => d.EnergyGenerationKwh);

                decimal? change = prevKwh > 0
                    ? Math.Round((totalKwh - prevKwh) / prevKwh * 100m, 0)
                    : null;

                var byDay = thisMonth
                    .GroupBy(d => d.Timestamp.Date)
                    .Select(g => new { Day = g.Key, Kwh = g.Sum(x => x.EnergyGenerationKwh) })
                    .OrderByDescending(x => x.Kwh)
                    .ToList();
                var best = byDay.FirstOrDefault();

                // Value the kWh per site, not at one blended rate: a customer
                // with a factory and a house pays a different tariff on each,
                // and a flat average quietly overstates the cheaper one.
                var kwhPerSite = await _context.EnergyData.AsNoTracking()
                    .Where(e => siteIds.Contains(e.InstallationId)
                                && e.Granularity == DataGranularity.Daily
                                && e.Timestamp >= monthStart && e.Timestamp <= monthEnd)
                    .GroupBy(e => e.InstallationId)
                    .Select(g => new { InstallationId = g.Key, Kwh = g.Sum(x => (decimal?)x.EnergyGenerationKwh) ?? 0m })
                    .ToDictionaryAsync(x => x.InstallationId, x => x.Kwh);

                var savings = customer.Installations.Sum(inst =>
                {
                    var instKwh = kwhPerSite.TryGetValue(inst.Id, out var k) ? k : 0m;
                    var rate = inst.ElectricityRatePerKwh > 0 ? inst.ElectricityRatePerKwh : 4.0m;
                    return instKwh * rate;
                });

                // "Healthy days" counts days with no offline alert raised
                // against any of their sites. It is the closest honest answer
                // to "did the thing I pay you for keep working".
                var offlineDays = await _context.SystemAlerts.AsNoTracking()
                    .Where(a => siteIds.Contains(a.InstallationId)
                                && a.AlertType == AlertType.Offline
                                && a.OccurredAt >= monthStart && a.OccurredAt < monthEnd.AddDays(1))
                    .Select(a => a.OccurredAt.Date)
                    .Distinct()
                    .CountAsync();

                var daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);

                var cleanings = await _context.CleaningRecords.AsNoTracking()
                    .CountAsync(c => siteIds.Contains(c.InstallationId)
                                     && c.Status == CleaningStatus.Completed
                                     && c.ActualDate != null
                                     && c.ActualDate >= monthStart && c.ActualDate <= monthEnd);

                // Counted on the day the crew finished, not the day it was
                // booked — a job scheduled in June and done in July belongs in
                // July's report, which is the one the customer is reading.
                var monthEndExclusive = monthEnd.AddDays(1);
                var workOrders = await _context.WorkOrders.AsNoTracking()
                    .CountAsync(w => w.InstallationId != null
                                     && siteIds.Contains(w.InstallationId.Value)
                                     && (w.Status == WorkOrderStatus.Completed
                                         || w.Status == WorkOrderStatus.Verified
                                         || w.Status == WorkOrderStatus.Invoiced
                                         || w.Status == WorkOrderStatus.Closed)
                                     && w.ActualEndTime != null
                                     && w.ActualEndTime >= monthStart
                                     && w.ActualEndTime < monthEndExclusive);

                var co2 = totalKwh * Co2KgPerKwh;

                await _line.SendMonthlyReportAsync(customer.LineUserId!, new MonthlyReportData
                {
                    CustomerName = customer.FullName,
                    PeriodLabel = periodLabel,
                    SiteCount = siteIds.Count,
                    TotalKwh = Math.Round(totalKwh, 0),
                    SavingsThb = Math.Round(savings, 0),
                    Co2SavedKg = Math.Round(co2, 0),
                    TreesEquivalent = (int)Math.Round(co2 / Co2KgPerTreeYear),
                    ChangeVsPreviousPercent = change,
                    BestDayKwh = Math.Round(best?.Kwh ?? 0, 1),
                    BestDayDate = best?.Day.ToString("dd/MM"),
                    HealthyDays = Math.Max(0, daysInMonth - offlineDays),
                    DaysInMonth = daysInMonth,
                    CleaningsDone = cleanings,
                    WorkOrdersCompleted = workOrders
                });

                sent++;
            }
            catch (Exception ex)
            {
                // One customer's bad data must not cost everyone else their
                // report — this job runs once a month, there is no retry until
                // next month.
                _logger.LogError(ex, "Monthly report failed for customer {Id}", customer.Id);
            }
        }

        _logger.LogInformation(
            "Monthly report {Period}: {Sent} sent, {Skipped} skipped (no production), "
            + "{Silent} active customer(s) have no LINE ID and received nothing",
            periodLabel, sent, skippedNoProduction, silent);
    }
}
