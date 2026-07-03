using Dapper;
using DbExplorer.Controllers;
using DbExplorer.Core.Interfaces;
using DbExplorer.Core.Models;
using DbExplorer.Options;
using DbExplorer.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DbExplorer.Tests.Unit;

public class FilterSqlTests
{
    [Theory]
    [InlineData(ColumnFilterOperator.Contains, "abc", "[Col] LIKE @p0", "%abc%")]
    [InlineData(ColumnFilterOperator.StartsWith, "abc", "[Col] LIKE @p0", "abc%")]
    [InlineData(ColumnFilterOperator.Equals, "abc", "[Col] = @p0", "abc")]
    [InlineData(ColumnFilterOperator.NotEquals, "abc", "[Col] <> @p0", "abc")]
    [InlineData(ColumnFilterOperator.GreaterThan, "5", "[Col] > @p0", "5")]
    [InlineData(ColumnFilterOperator.LessThan, "5", "[Col] < @p0", "5")]
    public void BuildPredicate_ValueOperators_EmitClauseAndParameter(
        ColumnFilterOperator op, string value, string expectedClause, string expectedParam)
    {
        var args = new DynamicParameters();
        var clause = FilterSql.BuildPredicate("[Col]", "p0", new ColumnFilter("Col", value, op), args);

        clause.Should().Be(expectedClause);
        args.Get<string>("p0").Should().Be(expectedParam);
    }

    [Theory]
    [InlineData(ColumnFilterOperator.IsNull, "[Col] IS NULL")]
    [InlineData(ColumnFilterOperator.IsNotNull, "[Col] IS NOT NULL")]
    public void BuildPredicate_NullOperators_IgnoreValueAndTakeNoParameters(
        ColumnFilterOperator op, string expectedClause)
    {
        var args = new DynamicParameters();
        var clause = FilterSql.BuildPredicate("[Col]", "p0", new ColumnFilter("Col", "ignored", op), args);

        clause.Should().Be(expectedClause);
        args.ParameterNames.Should().BeEmpty();
    }

    [Fact]
    public void BuildPredicate_Between_RegistersBothBounds()
    {
        var args = new DynamicParameters();
        var clause = FilterSql.BuildPredicate("[Col]", "p0",
            new ColumnFilter("Col", "1", ColumnFilterOperator.Between, "9"), args);

        clause.Should().Be("[Col] BETWEEN @p0 AND @p0b");
        args.Get<string>("p0").Should().Be("1");
        args.Get<string>("p0b").Should().Be("9");
    }

    [Fact]
    public void BuildPredicate_BetweenWithoutValue2_ReturnsNull()
    {
        var args = new DynamicParameters();
        var clause = FilterSql.BuildPredicate("[Col]", "p0",
            new ColumnFilter("Col", "1", ColumnFilterOperator.Between), args);

        clause.Should().BeNull();
        args.ParameterNames.Should().BeEmpty();
    }

    [Fact]
    public void BuildPredicate_InjectionAttemptInValue_StaysParameterized()
    {
        var args = new DynamicParameters();
        var payload = "'; DROP TABLE Users;--";
        var clause = FilterSql.BuildPredicate("[Col]", "p0",
            new ColumnFilter("Col", payload, ColumnFilterOperator.Equals), args);

        clause.Should().Be("[Col] = @p0");
        args.Get<string>("p0").Should().Be(payload);
    }

    [Fact]
    public void ColumnFilter_Defaults_PreserveLegacyContainsBehaviour()
    {
        var f = new ColumnFilter("Col", "x");
        f.Operator.Should().Be(ColumnFilterOperator.Contains);
        f.Value2.Should().BeNull();
    }
}

public class FilterEncodingTests
{
    [Fact]
    public void EncodeThenParse_RoundTrips()
    {
        var original = new List<ColumnFilter>
        {
            new("Name", "a;b~c", ColumnFilterOperator.Contains),
            new("Age", "18", ColumnFilterOperator.Between, "65"),
            new("DeletedAt", "", ColumnFilterOperator.IsNull),
        };

        var encoded = DataController.EncodeFilters(original);
        DataController.TryParseFilters(encoded, out var parsed, out var error).Should().BeTrue();

        error.Should().BeNull();
        parsed.Should().BeEquivalentTo(original);
    }

    [Theory]
    [InlineData("justacolumn")]
    [InlineData("Col~NotAnOperator~x")]
    [InlineData("~Equals~x")]
    [InlineData("Col~Equals~a~b~c")]
    public void TryParseFilters_Malformed_ReturnsFalse(string encoded)
    {
        DataController.TryParseFilters(encoded, out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void TryParseFilters_Empty_ReturnsTrueWithNullFilters()
    {
        DataController.TryParseFilters(null, out var filters, out _).Should().BeTrue();
        filters.Should().BeNull();
    }
}

public class DataControllerSortFilterTests
{
    private static DataController CreateController(IDataBrowsingService svc)
    {
        var controller = new DataController(
            svc,
            Mock.Of<IAuditLogger>(),
            Microsoft.Extensions.Options.Options.Create(new DataBrowsingOptions()),
            NullLogger<DataController>.Instance);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    [Fact]
    public async Task GetPage_SortAndFilterParams_FlowIntoPagingOptions()
    {
        PagingOptions? captured = null;
        var svc = new Mock<IDataBrowsingService>();
        svc.Setup(s => s.GetPagedDataAsync("dbo", "T", It.IsAny<PagingOptions>(), It.IsAny<CancellationToken>()))
           .Callback<string, string, PagingOptions, CancellationToken>((_, _, p, _) => captured = p)
           .ReturnsAsync(new PagedResult<DataRow>([], 1, 50, 0));

        var controller = CreateController(svc.Object);
        var result = await controller.GetPage("dbo", "T",
            sortCol: "Created", sortColDir: (int)SortDirection.Descending,
            filters: "Name~Equals~bob", ct: CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        captured!.SortColumn.Should().Be("Created");
        captured.SortColumnDirection.Should().Be(SortDirection.Descending);
        captured.Filters.Should().ContainSingle()
            .Which.Should().Be(new ColumnFilter("Name", "bob", ColumnFilterOperator.Equals));
    }

    [Fact]
    public async Task GetPage_MalformedFilters_Returns400()
    {
        var controller = CreateController(Mock.Of<IDataBrowsingService>());
        var result = await controller.GetPage("dbo", "T", filters: "Col~Nope~x", ct: CancellationToken.None);
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task ExportCsv_MalformedFilters_Returns400()
    {
        var controller = CreateController(Mock.Of<IDataBrowsingService>());
        var result = await controller.ExportCsv("dbo", "T", filters: "broken", ct: CancellationToken.None);
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
    }
}
