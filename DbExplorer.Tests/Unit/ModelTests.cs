using DbExplorer.Core.Models;
using FluentAssertions;
using Xunit;

namespace DbExplorer.Tests.Unit;

public class ModelTests
{
    [Fact]
    public void PagingOptions_Defaults_AreCorrect()
    {
        var opts = new PagingOptions();
        opts.PageNumber.Should().Be(1);
        opts.PageSize.Should().Be(50);
    }

    [Fact]
    public void DataRow_FieldsAreReadable()
    {
        var dict = new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "Alice" };
        var row = new DataRow(dict);

        row.Fields["Id"].Should().Be(1);
        row.Fields["Name"].Should().Be("Alice");
    }

    [Fact]
    public void PagedResult_TotalCountMatchesInput()
    {
        var items = new List<DataRow>();
        var result = new PagedResult<DataRow>(items, PageNumber: 1, PageSize: 50, TotalCount: 200);

        result.TotalCount.Should().Be(200);
        result.PageNumber.Should().Be(1);
        result.PageSize.Should().Be(50);
    }

    [Fact]
    public void ColumnInfo_PrimaryKeyFlag_IsSet()
    {
        var col = new ColumnInfo("dbo", "Users", "Id", 1, false, "int", null, null, null, true, null);
        col.IsPrimaryKey.Should().BeTrue();
    }

    [Fact]
    public void ForeignKeyInfo_ReferencedProperties_AreSet()
    {
        var fk = new ForeignKeyInfo("FK_Orders_Users", "dbo", "Orders", "UserId", "dbo", "Users", "Id");
        fk.ReferencedTable.Should().Be("Users");
        fk.ReferencedColumn.Should().Be("Id");
    }
}
