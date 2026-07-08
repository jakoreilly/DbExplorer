using DbExplorer.Services;
using Xunit;

namespace DbExplorer.Tests.Unit;

public class DataValueSearchServiceTests
{
    [Theory]
    [InlineData("PZ12345", "PZ12345")]
    [InlineData("50%", "50\\%")]
    [InlineData("a_b", "a\\_b")]
    [InlineData("[x]", "\\[x]")]
    [InlineData("c\\d", "c\\\\d")]
    public void EscapeLike_EscapesWildcardsLiterally(string input, string expected)
    {
        Assert.Equal(expected, DataValueSearchService.EscapeLike(input));
    }
}
