using DbExplorer.Services;
using Xunit;

namespace DbExplorer.Tests.Unit;

public class MetadataSearchServiceTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("50%", "50\\%")]
    [InlineData("under_score", "under\\_score")]
    [InlineData("[bracket]", "\\[bracket]")]
    [InlineData("back\\slash", "back\\\\slash")]
    public void EscapeLike_EscapesWildcardsLiterally(string input, string expected)
    {
        Assert.Equal(expected, MetadataSearchService.EscapeLike(input));
    }
}
