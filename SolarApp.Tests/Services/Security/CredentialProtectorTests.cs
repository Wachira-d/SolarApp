using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using SolarApp.Services.Security;
using Xunit;

namespace SolarApp.Tests.Services.Security;

public class CredentialProtectorTests
{
    private static CredentialProtector CreateSubject()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddDataProtection().Services.BuildServiceProvider();
        var provider = services.GetService(typeof(IDataProtectionProvider)) as IDataProtectionProvider;
        return new CredentialProtector(provider!, NullLogger<CredentialProtector>.Instance);
    }

    [Fact]
    public void Protect_Then_Unprotect_ReturnsOriginal()
    {
        var subject = CreateSubject();
        const string secret = "my-api-key-12345";

        var encrypted = subject.Protect(secret);
        var decrypted = subject.Unprotect(encrypted);

        encrypted.Should().NotBeNullOrEmpty();
        encrypted.Should().NotBe(secret, "encryption must change the string");
        encrypted!.Should().StartWith("enc:");
        decrypted.Should().Be(secret);
    }

    [Fact]
    public void Protect_Twice_DoesNotDoubleEncrypt()
    {
        var subject = CreateSubject();
        var encryptedOnce = subject.Protect("secret-value");
        var encryptedTwice = subject.Protect(encryptedOnce);

        encryptedOnce.Should().Be(encryptedTwice,
            "calling Protect on already-encrypted value must be idempotent");
    }

    [Fact]
    public void Unprotect_LegacyPlaintext_ReturnsAsIs()
    {
        var subject = CreateSubject();
        const string legacy = "stored-before-encryption-was-added";

        var result = subject.Unprotect(legacy);

        result.Should().Be(legacy, "legacy unencrypted credentials must remain readable");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Protect_NullOrEmpty_ReturnsInput(string? input)
    {
        var subject = CreateSubject();
        subject.Protect(input).Should().Be(input);
    }

    [Fact]
    public void IsProtected_IdentifiesEncryptedValuesCorrectly()
    {
        var subject = CreateSubject();
        var enc = subject.Protect("hello");

        subject.IsProtected(enc).Should().BeTrue();
        subject.IsProtected("plaintext").Should().BeFalse();
        subject.IsProtected(null).Should().BeFalse();
        subject.IsProtected("").Should().BeFalse();
    }
}
