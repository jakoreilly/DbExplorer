using DbExplorer.Controllers;
using Xunit;

namespace DbExplorer.Tests.Unit;

/// <summary>
/// Unit tests for ExternalAuthController helper methods accessible without an HTTP context.
/// </summary>
public class ExternalAuthControllerTests
{
    // ── EmailMatchesAllowList ─────────────────────────────────────────────────

    [Theory]
    [InlineData("alice@example.com",  "alice@example.com",  true)]   // exact match
    [InlineData("ALICE@EXAMPLE.COM",  "alice@example.com",  true)]   // case-insensitive
    [InlineData("bob@example.com",    "alice@example.com",  false)]  // different local part
    public void EmailMatchesAllowList_ExactPattern_MatchesCorrectly(
        string email, string pattern, bool expected)
    {
        Assert.Equal(expected, ExternalAuthController.EmailMatchesAllowList(email, [pattern]));
    }

    [Theory]
    [InlineData("alice@example.com",   "*@example.com",  true)]   // domain wildcard
    [InlineData("bob@example.com",     "*@example.com",  true)]
    [InlineData("alice@other.com",     "*@example.com",  false)]  // wrong domain
    [InlineData("alice@sub.example.com", "*@example.com", false)] // subdomain not matched by domain wildcard
    public void EmailMatchesAllowList_DomainWildcard_MatchesCorrectly(
        string email, string pattern, bool expected)
    {
        Assert.Equal(expected, ExternalAuthController.EmailMatchesAllowList(email, [pattern]));
    }

    [Theory]
    [InlineData("alice@sub.example.com", "*@*.example.com",  true)]
    [InlineData("bob@other.example.com", "*@*.example.com",  true)]
    [InlineData("alice@example.com",     "*@*.example.com",  false)] // no subdomain
    public void EmailMatchesAllowList_SubdomainWildcard_MatchesCorrectly(
        string email, string pattern, bool expected)
    {
        Assert.Equal(expected, ExternalAuthController.EmailMatchesAllowList(email, [pattern]));
    }

    [Fact]
    public void EmailMatchesAllowList_AllowAll_MatchesAnyEmail()
    {
        Assert.True(ExternalAuthController.EmailMatchesAllowList("anyone@anything.org", ["*@*.*"]));
    }

    [Fact]
    public void EmailMatchesAllowList_EmptyPatternList_ReturnsFalse()
    {
        Assert.False(ExternalAuthController.EmailMatchesAllowList("alice@example.com", []));
    }

    [Fact]
    public void EmailMatchesAllowList_MultiplePatterns_MatchesIfAnyPatternMatches()
    {
        var patterns = new[] { "alice@acme.com", "*@partner.com" };
        Assert.True(ExternalAuthController.EmailMatchesAllowList("bob@partner.com", patterns));
        Assert.False(ExternalAuthController.EmailMatchesAllowList("eve@other.com", patterns));
    }

    [Fact]
    public void EmailMatchesAllowList_InvalidEmailNoAtSign_ReturnsFalse()
    {
        Assert.False(ExternalAuthController.EmailMatchesAllowList("notanemail", ["*@*.*"]));
    }

    [Fact]
    public void EmailMatchesAllowList_EmailWithMultipleAtSigns_ReturnsFalse()
    {
        // e.g. "a@@b.com" — split produces 3 parts; method should return false
        Assert.False(ExternalAuthController.EmailMatchesAllowList("a@@b.com", ["*@*.*"]));
    }
}
