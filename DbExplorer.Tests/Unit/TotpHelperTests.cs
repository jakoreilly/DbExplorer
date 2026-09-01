using System.Text;
using DbExplorer.Services;
using FluentAssertions;
using Xunit;

namespace DbExplorer.Tests.Unit;

public class TotpHelperTests
{
    // RFC 6238 Appendix B seed for the SHA-1 test vectors: ASCII "12345678901234567890".
    private static string RfcSecret =>
        TotpHelper.Base32Encode(Encoding.ASCII.GetBytes("12345678901234567890"));

    [Theory]
    // time (unix seconds) -> the low 6 digits of the RFC 6238 Appendix B SHA-1 vector
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    public void Verify_AcceptsRfc6238Vectors(long unixSeconds, string code)
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        TotpHelper.Verify(RfcSecret, code, at, window: 0).Should().BeTrue();
    }

    [Fact]
    public void Verify_RejectsAWrongCode()
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(59);
        TotpHelper.Verify(RfcSecret, "000000", at).Should().BeFalse();
    }

    [Fact]
    public void Verify_ToleratesOneStepOfSkew_ButNotTwo()
    {
        var secret = TotpHelper.GenerateSecret();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000_000);
        var oneStepAgo = TotpHelper.CurrentCode(secret, now.AddSeconds(-30));
        var twoStepsAgo = TotpHelper.CurrentCode(secret, now.AddSeconds(-60));

        TotpHelper.Verify(secret, oneStepAgo, now, window: 1).Should().BeTrue();
        TotpHelper.Verify(secret, twoStepsAgo, now, window: 1).Should().BeFalse();
    }

    [Fact]
    public void Verify_RejectsMalformedInput()
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(59);
        TotpHelper.Verify(RfcSecret, null, at).Should().BeFalse();
        TotpHelper.Verify(RfcSecret, "", at).Should().BeFalse();
        TotpHelper.Verify(RfcSecret, "12345", at).Should().BeFalse();   // too short
        TotpHelper.Verify(RfcSecret, "1234567", at).Should().BeFalse();  // too long
        TotpHelper.Verify(RfcSecret, "12ab56", at).Should().BeFalse();   // non-digit
        TotpHelper.Verify("not base32!!!", "287082", at).Should().BeFalse();
        TotpHelper.Verify("", "287082", at).Should().BeFalse();
    }

    [Fact]
    public void GenerateSecret_ProducesA160BitDecodableSecret()
    {
        var secret = TotpHelper.GenerateSecret();
        TotpHelper.Base32Decode(secret).Should().HaveCount(20);
        secret.Should().MatchRegex("^[A-Z2-7]+$");
    }

    [Fact]
    public void GenerateSecret_IsDifferentEachCall()
    {
        TotpHelper.GenerateSecret().Should().NotBe(TotpHelper.GenerateSecret());
    }

    [Theory]
    [InlineData("")]
    [InlineData("f")]
    [InlineData("foobar")]
    [InlineData("Hello, TOTP world — 12345")]
    public void Base32_RoundTrips(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var decoded = TotpHelper.Base32Decode(TotpHelper.Base32Encode(bytes));
        decoded.Should().Equal(bytes);
    }

    [Fact]
    public void Base32Decode_IgnoresSpacesPaddingAndCase()
    {
        var canonical = TotpHelper.Base32Decode("JBSWY3DPEHPK3PXP");
        TotpHelper.Base32Decode("jbsw y3dp ehpk 3pxp").Should().Equal(canonical);
        TotpHelper.Base32Decode("JBSWY3DPEHPK3PXP======").Should().Equal(canonical);
    }

    [Fact]
    public void BuildOtpAuthUri_HasTheFieldsAnAuthenticatorNeeds()
    {
        var uri = TotpHelper.BuildOtpAuthUri("JBSWY3DPEHPK3PXP", "alice@example.com", "DbExplorer");
        uri.Should().StartWith("otpauth://totp/DbExplorer:alice%40example.com?");
        uri.Should().Contain("secret=JBSWY3DPEHPK3PXP");
        uri.Should().Contain("issuer=DbExplorer");
        uri.Should().Contain("digits=6").And.Contain("period=30").And.Contain("algorithm=SHA1");
    }
}
