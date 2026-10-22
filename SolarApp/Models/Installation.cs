using SolarApp.Models.Enums;

namespace SolarApp.Models;

public class Installation : ISoftDeletable
{
    public int Id { get; set; }
    public string SiteCode { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string? City { get; set; }
    public string? Province { get; set; }
    public string? PostalCode { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    // Customer & Partner (separate entities)
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public int? PartnerId { get; set; }
    public Partner? Partner { get; set; }

    // Inverter Cloud connection
    public InverterBrand InverterBrand { get; set; }
    public string? InverterModel { get; set; }
    public int InverterCount { get; set; } = 1;
    public decimal InverterSizeKw { get; set; }       // กำลังของ inverter แต่ละตัว (kW)
    public decimal TotalInverterCapacityKw => InverterSizeKw * InverterCount;
    public string? CloudApiKey { get; set; }
    public string? CloudApiSecret { get; set; }
    public string? CloudUsername { get; set; }
    public string? CloudPassword { get; set; }
    public string? PlantId { get; set; }

    /// <summary>
    /// Optional override for the inverter cloud API base URL. When set, the
    /// adapter skips region auto-detection and tries this endpoint FIRST.
    /// Useful when:
    ///   - The account lives on a regional server the adapter doesn't auto-detect
    ///     (e.g. global.hoymiles.com vs euapi/neapi for Hoymiles)
    ///   - The manufacturer migrated APIs and the default is out of date
    ///   - Behind a corporate proxy / custom routing
    /// Examples: https://global.hoymiles.com  |  https://neapi.hoymiles.com
    /// Leave blank for auto-detection (default behaviour, no change).
    /// </summary>
    public string? CloudApiBaseUrl { get; set; }

    // System specifications
    public SystemType SystemType { get; set; } = SystemType.OnGrid;
    public decimal SystemCapacityKw { get; set; }
    public int NumberOfPanels { get; set; }
    public int PanelWattage { get; set; }
    public string? PanelBrand { get; set; }
    public string? PanelModel { get; set; }

    // ─────────────────────────────────────────────────────────────────────
    //  Battery storage — opt-in per site
    // ─────────────────────────────────────────────────────────────────────
    // HasBattery is the single source of truth for "does this site have a
    // battery?".  When false, ALL battery UI (SOC tiles, charge/discharge
    // columns, battery-SoC alert rules) is suppressed system-wide even if a
    // stray SOC value sneaks through from the inverter cloud — some hybrid
    // inverters report a phantom 0% SOC with no battery attached.  When the
    // operator unchecks it on the Edit form the battery metadata below is
    // also cleared so nothing stale lingers.

    /// <summary>True when this site has battery storage. Gates all battery UI/alerts.</summary>
    public bool HasBattery { get; set; }

    public string? BatteryBrand { get; set; }
    public string? BatteryModel { get; set; }
    public decimal? BatteryCapacityKwh { get; set; }
    public int? BatteryCount { get; set; }

    // Dates
    /// <summary>
    /// Handover date — the day the job was signed off and the warranty clock
    /// started. Named "installation" for historical reasons and kept that way
    /// so the column doesn't have to move; the UI calls it what it is.
    /// </summary>
    public DateTime InstallationDate { get; set; }
    public DateTime? CommissionDate { get; set; }

    // Warranty
    public int WarrantyYears { get; set; }

    /// <summary>
    /// The agreed warranty end date.
    ///
    /// This used to be derived on every save — handover + WarrantyYears,
    /// overwritten unconditionally — so an operator could type a correct end
    /// date, save, and watch it revert. Real warranties don't always run a
    /// whole number of years from handover: a supplier may date cover from
    /// commissioning, a dispute may extend it, a contract may simply name a
    /// date. It is a stored fact now, seeded from the calculation and editable
    /// afterwards.
    /// </summary>
    public DateTime WarrantyExpiryDate { get; set; }
    public int InverterWarrantyYears { get; set; }
    public DateTime? InverterWarrantyExpiryDate { get; set; }

    /// <summary>
    /// The end date the years would give, for pre-filling the form and for
    /// showing the operator when the stored date has been overridden.
    /// </summary>
    public static DateTime CalculateWarrantyEnd(DateTime handoverDate, int years) =>
        handoverDate.Date.AddYears(years);

    /// <summary>
    /// When the hardware started ageing in the field — the earliest date we
    /// hold for it.
    ///
    /// Age-based predictions (fan wear, capacitor drift, recall windows) must
    /// not be measured from handover: that is a paperwork date, and on sites
    /// where commissioning and sign-off are months apart it makes the
    /// equipment look younger than it is, which delays exactly the inspection
    /// the prediction exists to trigger. Taking the earliest of the two can
    /// only age equipment, never make it younger, so nothing that used to
    /// warn stops warning.
    /// </summary>
    public DateTime HardwareInServiceDate =>
        CommissionDate.HasValue && CommissionDate.Value.Date < InstallationDate.Date
            ? CommissionDate.Value.Date
            : InstallationDate.Date;

    // Panel Cleaning
    public int CleaningFrequencyMonths { get; set; } = 6;
    public int TotalCleaningCount { get; set; }
    public int CompletedCleaningCount { get; set; }
    public DateTime? LastCleaningDate { get; set; }
    public DateTime? NextCleaningDueDate { get; set; }

    public int RemainingCleanings => TotalCleaningCount - CompletedCleaningCount;

    /// <summary>
    /// False when no end date was ever recorded. The column is non-nullable,
    /// so an unset warranty reads as 0001-01-01 — which every comparison
    /// treated as "expired two thousand years ago", quietly hiding the site
    /// from the expiry reminder instead of flagging that nobody filled it in.
    /// </summary>
    public bool HasWarrantyDate => WarrantyExpiryDate.Year > 1900;

    /// <summary>
    /// Compared on site-local dates. UtcNow put the rollover at 07:00
    /// Thailand time, so a warranty read as live for most of its last morning
    /// and expired for the first seven hours of the day it should still cover.
    /// </summary>
    public bool IsWarrantyExpired =>
        HasWarrantyDate && Services.SiteTime.Today > WarrantyExpiryDate.Date;

    /// <summary>
    /// Days until the warranty ends; negative once past, null when no date was
    /// recorded. Callers that clamped at zero couldn't tell "expires today"
    /// from "expired last year".
    /// </summary>
    public int? WarrantyDaysRemaining => HasWarrantyDate
        ? (int)(WarrantyExpiryDate.Date - Services.SiteTime.Today).TotalDays
        : null;

    // ── Cleaning: one definition of "due", used everywhere ─────────────────
    //
    // "Due" used to be just NextCleaningDueDate <= now. Every caller was then
    // expected to remember to also check IncludesCleaning, and several didn't
    // — so a site whose cleaning had been switched off kept its "ครบกำหนด!"
    // badge on the dashboard table, the site card and the detail page,
    // indefinitely. The rule lives here now, and the query form below is the
    // same rule for the database, so the two cannot drift.

    /// <summary>
    /// The contract had a fixed number of cleanings and they've all been done.
    /// A total of 0 means "every cycle, no fixed count" — the meaning Edit and
    /// CSV import already gave it — so it never runs out.
    /// </summary>
    public bool CleaningContractUsedUp =>
        TotalCleaningCount > 0 && CompletedCleaningCount >= TotalCleaningCount;

    /// <summary>
    /// We clean this site, still have cleanings left to do, and the site is
    /// live. Inactive is included because the reminder job used to keep
    /// raising cleaning alerts — and sending LINE — for deactivated sites.
    /// </summary>
    public bool HasCleaningInScope =>
        IncludesCleaning && !CleaningContractUsedUp && Status != InstallationStatus.Inactive;

    /// <summary>
    /// Cleaning is in scope and its date has arrived, on site-local dates
    /// (UtcNow flipped the answer at 07:00 Thailand time).
    /// </summary>
    public bool IsCleaningDue =>
        HasCleaningInScope
        && NextCleaningDueDate.HasValue
        && NextCleaningDueDate.Value.Date <= Services.SiteTime.Today;

    /// <summary>
    /// <see cref="IsCleaningDue"/> as a database filter — computed properties
    /// can't be translated to SQL, and a hand-copied condition in each query is
    /// exactly how the pages ended up disagreeing.
    /// </summary>
    public static System.Linq.Expressions.Expression<Func<Installation, bool>> CleaningDueAsOf(DateTime siteToday)
    {
        var cutoff = siteToday.Date.AddDays(1);
        return i => i.IncludesCleaning
                    && (i.TotalCleaningCount <= 0 || i.CompletedCleaningCount < i.TotalCleaningCount)
                    && i.Status != InstallationStatus.Inactive
                    && i.NextCleaningDueDate != null
                    && i.NextCleaningDueDate < cutoff;
    }

    /// <summary>
    /// Records one completed cleaning and moves the schedule on.
    ///
    /// Five pages and services each did this by hand, all with
    /// "next = date + freq only if Completed &lt; Total, else null". For a
    /// contract with no fixed count (Total = 0, "every cycle") that condition
    /// is never true, so recording the very first cleaning erased the site's
    /// schedule — which then reappeared the next time anyone saved the site
    /// for an unrelated reason. A backdated entry also wound LastCleaningDate
    /// backwards; the latest cleaning on record now wins.
    /// </summary>
    public void RecordCompletedCleaning(DateTime performedOn)
    {
        CompletedCleaningCount++;
        if (!LastCleaningDate.HasValue || performedOn > LastCleaningDate.Value)
            LastCleaningDate = performedOn;
        NextCleaningDueDate = ComputeNextCleaningDue();
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// The next due date this site should have right now, or null when it has
    /// no cleaning schedule (switched off, or the contract's cleanings are used
    /// up). Counted from the last cleaning when there is one — if that plus a
    /// cycle is already past, the site genuinely is overdue and should say so.
    /// With no cleaning on record it counts from handover and rolls forward to
    /// the next cycle, because we have no history to call it overdue against.
    /// </summary>
    public DateTime? ComputeNextCleaningDue()
    {
        if (!HasCleaningInScope) return null;

        var freq = CleaningFrequencyMonths > 0 ? CleaningFrequencyMonths : 6;

        if (LastCleaningDate.HasValue)
            return LastCleaningDate.Value.Date.AddMonths(freq);

        var today = Services.SiteTime.Today;
        var next = InstallationDate.Date.AddMonths(freq);
        while (next < today) next = next.AddMonths(freq);
        return next;
    }

    // Status
    public InstallationStatus Status { get; set; } = InstallationStatus.Active;
    public DateTime? LastOnlineAt { get; set; }
    public string? Notes { get; set; }

    // ─────────────────────────────────────────────────────────────────────
    //  Service scope — which services we actually provide for this site
    // ─────────────────────────────────────────────────────────────────────
    // Background jobs respect these flags:
    //   * SolarDataSyncJob skips sites with IncludesMonitoring = false
    //     (no cloud account, partner monitors themselves).
    //   * GetInstallationsNeedingCleaning / cleaning reminders skip sites
    //     with IncludesCleaning = false.
    //   * PM/CM schedulers skip the corresponding flag.
    // The ServiceLevel enum is a UI preset that pre-fills the flags; the
    // flags are the source of truth at runtime.

    /// <summary>Preset that pre-fills the IncludesXxx flags from the UI.</summary>
    public ServiceLevel ServiceLevel { get; set; } = ServiceLevel.FullOM;

    /// <summary>Online cloud monitoring (data sync, alerts, dashboard tiles).</summary>
    public bool IncludesMonitoring { get; set; } = true;

    /// <summary>Scheduled panel-cleaning visits.</summary>
    public bool IncludesCleaning { get; set; } = true;

    /// <summary>Preventive maintenance: routine checks, inspections, calibration.</summary>
    public bool IncludesPreventiveMaintenance { get; set; } = true;

    /// <summary>Corrective maintenance: respond to faults / breakdowns / repairs.</summary>
    public bool IncludesCorrectiveMaintenance { get; set; } = true;

    /// <summary>Warranty claim handling on behalf of the customer.</summary>
    public bool IncludesWarrantyService { get; set; } = true;

    /// <summary>Free-form notes for unusual contract terms (e.g. "PM 2 ครั้ง/ปี เท่านั้น").</summary>
    public string? ServiceNotes { get; set; }

    // ─────────────────────────────────────────────────────────────────────
    //  Cloud connection — how and how often we poll the brand API
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>How we authenticate to the brand's cloud (see BrandCapabilities for what each brand supports).</summary>
    public ConnectionMethod ConnectionMethod { get; set; } = ConnectionMethod.OfficialApi;

    /// <summary>
    /// How often the background sync job should hit this site's cloud API.
    /// Operator-chosen from a dropdown of presets (5 / 15 / 30 / 60 / 120 /
    /// 360 / 720 / 1440 min); clamped to the brand's minimum at save time.
    /// </summary>
    public int SyncIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// Earliest UTC time the next sync may run.  SolarDataSyncJob filters on
    /// this so each site fires at its own cadence even though the Hangfire
    /// job runs on a uniform 5-min schedule.  Null = never synced yet (eligible).
    /// </summary>
    public DateTime? NextSyncDueAt { get; set; }

    // ─────────────────────────────────────────────────────────────────────
    //  Customer alerts — LINE group routing + per-event toggles
    // ─────────────────────────────────────────────────────────────────────
    // We push event-driven alerts to the customer over LINE.  Customers
    // care most about "solar system down" (grid loss, inverter offline).
    // Operators choose which event categories to forward per installation.

    /// <summary>LINE group / user / room id to push customer-facing alerts to.</summary>
    public string? LineGroupId { get; set; }

    /// <summary>Notify on grid-loss / inverter offline (customer-visible — recommended ON).</summary>
    public bool NotifyOnGridLoss { get; set; } = true;

    /// <summary>Notify when cloud API stops responding (system-internal — usually OFF for customer LINE).</summary>
    public bool NotifyOnApiFailure { get; set; } = false;

    /// <summary>Notify when production is well below the expected curve for the day.</summary>
    public bool NotifyOnLowProduction { get; set; } = false;

    /// <summary>Remind when a scheduled cleaning is due.</summary>
    public bool NotifyOnCleaningDue { get; set; } = false;

    /// <summary>Remind when warranty expiry is approaching.</summary>
    public bool NotifyOnWarrantyExpiry { get; set; } = false;

    /// <summary>
    /// "Do-not-disturb" window for customer notifications (24-h local time).
    /// When the start &lt; end (e.g. 22:00 → 07:00) notifications between
    /// those hours go to the operator alert group only — the customer's
    /// LINE is quiet so they aren't woken up at 3 AM for a Grid Loss that
    /// resolved itself in 20 minutes.  Null in either field = quiet hours
    /// disabled.  Critical alerts ignore quiet hours by design.
    /// </summary>
    public TimeOnly? QuietHoursStart { get; set; }

    /// <summary>Local-time end of the customer LINE do-not-disturb window.</summary>
    public TimeOnly? QuietHoursEnd { get; set; }

    /// <summary>
    /// Forward recovery ("✅ ระบบกลับมาแล้ว") messages to the same
    /// channels that received the original alert.  Default ON — most
    /// customers want closure, but some prefer "tell me only when it
    /// breaks, not when it comes back".
    /// </summary>
    public bool NotifyOnRecovery { get; set; } = true;

    // Financial
    public decimal? ContractValue { get; set; }
    public decimal ElectricityRatePerKwh { get; set; } = 4.0m;
    public decimal? FeedInTariffRate { get; set; }

    // Audit
    public string? CreatedByUserId { get; set; }
    public ApplicationUser? CreatedByUser { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public ICollection<Equipment> Equipment { get; set; } = new List<Equipment>();
    public ICollection<EnergyData> EnergyData { get; set; } = new List<EnergyData>();
    public ICollection<SystemAlert> Alerts { get; set; } = new List<SystemAlert>();
    public ICollection<MaintenanceRecord> MaintenanceRecords { get; set; } = new List<MaintenanceRecord>();
    public ICollection<ServiceTicket> ServiceTickets { get; set; } = new List<ServiceTicket>();
    public ICollection<WarrantyRecord> WarrantyRecords { get; set; } = new List<WarrantyRecord>();
    public ICollection<CleaningRecord> CleaningRecords { get; set; } = new List<CleaningRecord>();
    public ICollection<InstallationDocument> Documents { get; set; } = new List<InstallationDocument>();
    public ICollection<ElectricityTariff> Tariffs { get; set; } = new List<ElectricityTariff>();

    // ── Soft delete (see ISoftDeletable) ──
    public DateTime? DeletedAt { get; set; }
    public string? DeletedByUserId { get; set; }

}
