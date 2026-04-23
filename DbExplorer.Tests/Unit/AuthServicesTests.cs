using DbExplorer.Services;
using FluentAssertions;
using Xunit;

namespace DbExplorer.Tests.Unit;

public class AuthServicesTests
{
    [Fact]
    public void BCryptHelper_HashAndVerify_RoundTrip()
    {
        var password = "SecureP@ssw0rd!";
        var hash = BCryptHelper.Hash(password);

        BCryptHelper.Verify(password, hash).Should().BeTrue();
    }

    [Fact]
    public void BCryptHelper_WrongPassword_ReturnsFalse()
    {
        var hash = BCryptHelper.Hash("correct");
        BCryptHelper.Verify("wrong", hash).Should().BeFalse();
    }

    [Fact]
    public void BCryptHelper_TamperedHash_ReturnsFalse()
    {
        BCryptHelper.Verify("password", "pbkdf2:badsalt:badhash").Should().BeFalse();
    }

    [Fact]
    public void BCryptHelper_InvalidFormat_ReturnsFalse()
    {
        BCryptHelper.Verify("password", "notvalid").Should().BeFalse();
    }

    [Fact]
    public void BCryptHelper_TwoDifferentHashesForSamePassword_AreNotEqual()
    {
        var h1 = BCryptHelper.Hash("same");
        var h2 = BCryptHelper.Hash("same");
        // Salt randomness means hashes differ
        h1.Should().NotBe(h2);
    }
}
