namespace SolarApp.Models;

/// <summary>
/// Records that must survive being "deleted".
///
/// An O&M contract is settled from history: which panels were cleaned when,
/// what failed, what the site produced before and after. A hard DELETE on a
/// customer or a site took that with it, and the row that proved we did the
/// work was gone at exactly the moment a warranty dispute needed it.
///
/// Deleting now stamps <see cref="DeletedAt"/> and the rows drop out of every
/// query through a global filter in ApplicationDbContext — no per-query
/// bookkeeping, so a page that forgets about this still can't show deleted
/// data. Reports that must count history use IgnoreQueryFilters().
/// </summary>
public interface ISoftDeletable
{
    DateTime? DeletedAt { get; set; }

    /// <summary>Identity id of whoever deleted it — "who did this" is the first question asked.</summary>
    string? DeletedByUserId { get; set; }
}
