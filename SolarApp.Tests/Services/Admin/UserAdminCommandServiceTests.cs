using FluentAssertions;
using SolarApp.Services.Admin;
using Xunit;

namespace SolarApp.Tests.Services.Admin;

/// <summary>
/// Pure-function tests on the extracted command service. Full handler-level
/// tests that exercise UserManager/DbContext belong in integration tests.
/// </summary>
public class UserAdminCommandServiceTests
{
    [Fact]
    public void CommandResult_Ok_HasSuccessTrue()
    {
        var r = CommandResult.Ok("Created");

        r.Success.Should().BeTrue();
        r.Message.Should().Be("Created");
    }

    [Fact]
    public void CommandResult_Fail_HasSuccessFalse()
    {
        var r = CommandResult.Fail("Bad input");

        r.Success.Should().BeFalse();
        r.Message.Should().Be("Bad input");
    }

    [Fact]
    public void GenerateTempPassword_MeetsIdentityRequirements()
    {
        for (var i = 0; i < 50; i++)
        {
            var pwd = UserAdminCommandService.GenerateTempPassword();

            pwd.Should().HaveLength(8, "Identity default min length is 6 but pattern produces 8");
            pwd.Should().MatchRegex("[A-Z]", "must contain uppercase");
            pwd.Should().MatchRegex("[a-z]", "must contain lowercase");
            pwd.Should().MatchRegex("[0-9]", "must contain digit");
            pwd.Should().MatchRegex("[!@#$%&*]", "must contain special character");
        }
    }

    [Fact]
    public void GenerateTempPassword_ExcludesAmbiguousChars()
    {
        // Original pattern excludes 'l', 'I', 'O', '0', '1' to avoid confusion
        for (var i = 0; i < 100; i++)
        {
            var pwd = UserAdminCommandService.GenerateTempPassword();
            pwd.Should().NotContainAny("l", "I", "O", "0", "1");
        }
    }
}
