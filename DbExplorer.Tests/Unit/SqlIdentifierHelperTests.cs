using DbExplorer.Core.Validation;
using FluentAssertions;
using Xunit;

namespace DbExplorer.Tests.Unit;

public class SqlIdentifierHelperTests
{
    [Theory]
    [InlineData("dbo")]
    [InlineData("MySchema")]
    [InlineData("_private")]
    [InlineData("schema123")]
    [InlineData("A")]
    public void IsValidIdentifierFormat_ValidIdentifiers_ReturnsTrue(string identifier)
    {
        SqlIdentifierHelper.IsValidIdentifierFormat(identifier).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("123startsWithNumber")]
    [InlineData("has space")]
    [InlineData("has-dash")]
    [InlineData("has.dot")]
    [InlineData("has'quote")]
    [InlineData(null!)]
    public void IsValidIdentifierFormat_InvalidIdentifiers_ReturnsFalse(string? identifier)
    {
        SqlIdentifierHelper.IsValidIdentifierFormat(identifier!).Should().BeFalse();
    }

    [Fact]
    public void IsValidIdentifierFormat_TooLong_ReturnsFalse()
    {
        var tooLong = new string('a', 129);
        SqlIdentifierHelper.IsValidIdentifierFormat(tooLong).Should().BeFalse();
    }

    [Theory]
    [InlineData("dbo", "[dbo]")]
    [InlineData("MyTable", "[MyTable]")]
    [InlineData("has]bracket", "[has]]bracket]")]
    public void Quote_ReturnsCorrectlyBracketedIdentifier(string input, string expected)
    {
        SqlIdentifierHelper.Quote(input).Should().Be(expected);
    }

    [Fact]
    public void Quote_EmptyString_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => SqlIdentifierHelper.Quote(""));
    }

    [Fact]
    public void Quote_TooLongIdentifier_ThrowsArgumentException()
    {
        var tooLong = new string('a', 129);
        Assert.Throws<ArgumentException>(() => SqlIdentifierHelper.Quote(tooLong));
    }

    [Fact]
    public void ThrowIfInvalidFormat_ValidIdentifier_DoesNotThrow()
    {
        var act = () => SqlIdentifierHelper.ThrowIfInvalidFormat("dbo", "param");
        act.Should().NotThrow();
    }

    [Fact]
    public void ThrowIfInvalidFormat_InvalidIdentifier_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            SqlIdentifierHelper.ThrowIfInvalidFormat("bad identifier!", "param"));
    }
}
