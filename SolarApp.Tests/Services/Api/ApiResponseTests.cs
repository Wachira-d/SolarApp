using FluentAssertions;
using SolarApp.Services.Api;
using Xunit;

namespace SolarApp.Tests.Services.Api;

public class ApiResponseTests
{
    [Fact]
    public void Ok_DefaultsToSuccessTrue()
    {
        var r = ApiResponse.Ok();

        r.Success.Should().BeTrue();
        r.Code.Should().BeNull();
    }

    [Fact]
    public void Ok_AllowsMessageAndData()
    {
        var payload = new { id = 42 };
        var r = ApiResponse.Ok("Created", payload);

        r.Success.Should().BeTrue();
        r.Message.Should().Be("Created");
        r.Data.Should().Be(payload);
    }

    [Fact]
    public void Fail_HasSuccessFalseAndOptionalCode()
    {
        var r = ApiResponse.Fail("Bad request", "BAD_REQUEST");

        r.Success.Should().BeFalse();
        r.Message.Should().Be("Bad request");
        r.Code.Should().Be("BAD_REQUEST");
        r.Data.Should().BeNull();
    }

    [Theory]
    [InlineData("ACCESS_DENIED")]
    [InlineData("NOT_FOUND")]
    [InlineData("VALIDATION_ERROR")]
    [InlineData("UNAUTHORIZED")]
    public void Factory_Methods_SetExpectedCode(string expectedCode)
    {
        ApiResponse r = expectedCode switch
        {
            "ACCESS_DENIED" => ApiResponse.AccessDenied(),
            "NOT_FOUND" => ApiResponse.NotFound(),
            "VALIDATION_ERROR" => ApiResponse.ValidationError("oops"),
            "UNAUTHORIZED" => ApiResponse.Unauthorized(),
            _ => throw new System.InvalidOperationException()
        };

        r.Success.Should().BeFalse();
        r.Code.Should().Be(expectedCode);
    }

    [Fact]
    public void AccessDenied_DefaultMessageIsNonEmpty()
    {
        var r = ApiResponse.AccessDenied();
        r.Message.Should().NotBeNullOrWhiteSpace();
    }
}
