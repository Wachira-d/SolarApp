using FluentAssertions;
using SolarApp.Services.PredictiveDiagnostics;
using Xunit;

namespace SolarApp.Tests.Services.PredictiveDiagnostics;

/// <summary>
/// Regression guard on the batch-load shape used by PredictiveDiagnosticsEngine.
///
/// Before the Sprint 10 refactor each analyze method ran its own queries
/// per-installation (800+ DB hits on a 100-site fleet). The new path
/// pre-builds a DiagnosticsBatchMetrics with dictionaries keyed by
/// installation, and the per-install methods are now pure functions.
///
/// These tests pin down the "missing key = skip / treat as zero" contract
/// so a future change can't silently break diagnostics for sites that have
/// no recent data.
/// </summary>
public class DiagnosticsBatchMetricsTests
{
    [Fact]
    public void NewInstance_HasEmptyDictionaries()
    {
        var m = new DiagnosticsBatchMetrics();

        m.RecentEnergyAvg.Should().BeEmpty();
        m.PreviousEnergyAvg.Should().BeEmpty();
        m.OfflineMarkedCount.Should().BeEmpty();
        m.SuccessfulSyncCount.Should().BeEmpty();
        m.RecentErrorCount.Should().BeEmpty();
        m.RecentTotalCount.Should().BeEmpty();
        m.PreviousErrorCount.Should().BeEmpty();
        m.PreviousTotalCount.Should().BeEmpty();
        m.TopRecentErrors.Should().BeEmpty();
        m.RecurringAlerts.Should().BeEmpty();
        m.RecentDistinctErrors.Should().BeEmpty();
        m.RecentAlarmStrings.Should().BeEmpty();
    }

    [Fact]
    public void MissingKey_DefaultsAreZeroOrNullList()
    {
        // The engine relies on "no entry = 0 / empty" semantics. If anyone
        // changes the dictionary type or adds default-fallback wrappers,
        // this test catches it.
        var m = new DiagnosticsBatchMetrics();

        m.RecentEnergyAvg.TryGetValue(42, out var avg).Should().BeFalse();
        avg.Should().Be(0m);

        m.OfflineMarkedCount.TryGetValue(42, out var c).Should().BeFalse();
        c.Should().Be(0);

        m.RecurringAlerts.TryGetValue(42, out var rec).Should().BeFalse();
        rec.Should().BeNull();
    }

    [Fact]
    public void Populated_PreservesAllValues()
    {
        var m = new DiagnosticsBatchMetrics
        {
            RecentEnergyAvg = new() { { 1, 12.5m }, { 2, 8.0m } },
            PreviousEnergyAvg = new() { { 1, 20.0m } },
            OfflineMarkedCount = new() { { 3, 15 } },
            RecurringAlerts = new()
            {
                { 5, new() { ("Grid Loss", 7, DateTime.UtcNow) } }
            }
        };

        m.RecentEnergyAvg[1].Should().Be(12.5m);
        m.RecentEnergyAvg[2].Should().Be(8.0m);
        m.PreviousEnergyAvg[1].Should().Be(20.0m);
        m.OfflineMarkedCount[3].Should().Be(15);
        m.RecurringAlerts[5].Should().HaveCount(1);
        m.RecurringAlerts[5][0].Title.Should().Be("Grid Loss");
        m.RecurringAlerts[5][0].Count.Should().Be(7);
    }
}
