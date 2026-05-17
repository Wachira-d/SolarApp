namespace SolarApp.Services.PredictiveDiagnostics;

/// <summary>
/// Pre-computed aggregations used by PredictiveDiagnosticsEngine. Built ONCE
/// per AnalyzeAllInstallationsAsync call instead of re-querying per installation.
///
/// Before this struct existed, each analysis method ran 2–5 queries per
/// installation; a 100-installation pass hit the DB 800+ times. Now the
/// engine builds these dictionaries in a fixed ~6 queries regardless of
/// fleet size, then each analysis method does O(1) dict lookups.
///
/// All dictionaries are keyed by InstallationId; missing keys mean
/// "no data in window" (analysis methods should treat as zero / skip).
/// </summary>
public sealed class DiagnosticsBatchMetrics
{
    // Energy decline (Pattern 1): per-installation average kWh in two windows
    public Dictionary<int, decimal> RecentEnergyAvg { get; init; } = new();      // last 7d
    public Dictionary<int, decimal> PreviousEnergyAvg { get; init; } = new();    // 30–37d ago

    // Intermittent offline (Pattern 2): API log counts in last 7d
    public Dictionary<int, int> OfflineMarkedCount { get; init; } = new();
    public Dictionary<int, int> SuccessfulSyncCount { get; init; } = new();

    // API error trend (Pattern 3): error/total counts in two windows
    public Dictionary<int, int> RecentErrorCount { get; init; } = new();      // last 3d
    public Dictionary<int, int> RecentTotalCount { get; init; } = new();
    public Dictionary<int, int> PreviousErrorCount { get; init; } = new();    // 3–10d ago
    public Dictionary<int, int> PreviousTotalCount { get; init; } = new();
    public Dictionary<int, List<(string Error, int Count)>> TopRecentErrors { get; init; } = new();

    // Repeating faults (Pattern 6): alerts grouped by title in 30d
    public Dictionary<int, List<(string Title, int Count, DateTime Latest)>> RecurringAlerts { get; init; } = new();

    // Fault code patterns (Pattern 8): distinct error messages + alarm strings in 7d
    public Dictionary<int, List<string>> RecentDistinctErrors { get; init; } = new();
    public Dictionary<int, List<string>> RecentAlarmStrings { get; init; } = new();
}
