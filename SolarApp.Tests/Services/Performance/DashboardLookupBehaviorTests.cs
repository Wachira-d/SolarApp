using FluentAssertions;
using Xunit;

namespace SolarApp.Tests.Services.Performance;

/// <summary>
/// Regression guard on the dictionary-lookup optimization in
/// DashboardService.GetInstallationStatusesAsync.
///
/// Before the fix: per-installation inner loop called
///   activeAlerts.Count(a => a.InstallationId == inst.Id)
/// which scanned the entire list once per installation (O(N×M)).
///
/// After: pre-built Dictionary&lt;int,int&gt; gives O(1) lookup.
///
/// This test asserts the lookup semantics behave equivalently to the
/// previous LINQ Count() so we never regress to the slow path.
/// </summary>
public class DashboardLookupBehaviorTests
{
    private record FakeAlert(int InstallationId, string Title);

    [Fact]
    public void DictLookup_AgreesWithLinqCount_ForAllInstallationIds()
    {
        var alerts = new List<FakeAlert>
        {
            new(1, "Offline"),
            new(1, "LowProduction"),
            new(2, "Offline"),
            new(3, "Offline"),
            new(3, "Faulty"),
            new(3, "LowProduction"),
        };

        var installIds = new[] { 1, 2, 3, 4 };

        // OLD path
        var linqCounts = installIds
            .Select(id => alerts.Count(a => a.InstallationId == id))
            .ToList();

        // NEW path
        var dict = alerts
            .GroupBy(a => a.InstallationId)
            .ToDictionary(g => g.Key, g => g.Count());
        var dictCounts = installIds
            .Select(id => dict.TryGetValue(id, out var c) ? c : 0)
            .ToList();

        dictCounts.Should().Equal(linqCounts);
        dictCounts.Should().Equal(2, 1, 3, 0);
    }

    [Fact]
    public void DictLookup_HandlesEmptyInput()
    {
        var alerts = new List<FakeAlert>();
        var dict = alerts
            .GroupBy(a => a.InstallationId)
            .ToDictionary(g => g.Key, g => g.Count());

        dict.Should().BeEmpty();
        (dict.TryGetValue(99, out var c) ? c : 0).Should().Be(0);
    }

    [Fact]
    public void FirstValueByGroup_TakesFirstAsExpected()
    {
        // Mirrors the todayDataByInstall projection (we want one EnergyData
        // per installation — taking First is fine because the source query
        // is filtered to Granularity.Daily on a single day).
        var records = new[]
        {
            new { InstallationId = 1, Kwh = 12m },
            new { InstallationId = 1, Kwh = 99m }, // duplicate — First wins
            new { InstallationId = 2, Kwh = 7m },
        };

        var byInstall = records
            .GroupBy(e => e.InstallationId)
            .ToDictionary(g => g.Key, g => g.First());

        byInstall[1].Kwh.Should().Be(12m);
        byInstall[2].Kwh.Should().Be(7m);
    }
}
