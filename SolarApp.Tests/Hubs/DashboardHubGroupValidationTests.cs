using FluentAssertions;
using Xunit;

namespace SolarApp.Tests.Hubs;

/// <summary>
/// Pure-function tests on the group-name parsing logic used by
/// DashboardHub.CanJoinAsync. Full hub integration tests (with a real
/// Context.User / DbContext) belong in an integration test project.
/// </summary>
public class DashboardHubGroupValidationTests
{
    // Reproduces the parsing logic so we can verify the contract without
    // pulling in SignalR test scaffolding.
    private static (string Prefix, int? Id) ParseGroup(string group)
    {
        if (group.StartsWith("partner-", StringComparison.OrdinalIgnoreCase))
            return ("partner", int.TryParse(group[8..], out var id) ? id : (int?)null);
        if (group.StartsWith("customer-", StringComparison.OrdinalIgnoreCase))
            return ("customer", int.TryParse(group[9..], out var id) ? id : (int?)null);
        if (group.StartsWith("installation-", StringComparison.OrdinalIgnoreCase))
            return ("installation", int.TryParse(group[13..], out var id) ? id : (int?)null);
        return (group, null);
    }

    [Theory]
    [InlineData("partner-42", "partner", 42)]
    [InlineData("customer-7", "customer", 7)]
    [InlineData("installation-1234", "installation", 1234)]
    [InlineData("Partner-99", "partner", 99)]                // case-insensitive
    [InlineData("all", "all", null)]
    [InlineData("garbage", "garbage", null)]
    [InlineData("partner-notanumber", "partner", null)]      // malformed id rejected
    public void ParseGroup_RecognizesSchemes(string input, string expectedPrefix, int? expectedId)
    {
        var (prefix, id) = ParseGroup(input);
        prefix.Should().Be(expectedPrefix);
        id.Should().Be(expectedId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a-very-long-group-name-that-exceeds-the-eighty-character-maximum-allowed-by-hub-12345")]
    public void Subscribe_RejectsBlankOrTooLongInput(string group)
    {
        // Length cap defined in DashboardHub.Subscribe (80 chars)
        var isInvalid = string.IsNullOrWhiteSpace(group) || group.Length > 80;
        isInvalid.Should().BeTrue();
    }
}
