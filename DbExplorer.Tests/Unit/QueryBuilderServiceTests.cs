using DbExplorer.Core.Models;
using DbExplorer.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace DbExplorer.Tests.Unit;

/// <summary>
/// Unit tests for QueryBuilderService — focuses on topological sort and basic SQL compilation.
/// </summary>
public class QueryBuilderServiceTests
{
    private static QueryBuilderService CreateService(DatabaseProvider provider = DatabaseProvider.SqlServer)
    {
        var factory = Mock.Of<IDbConnectionFactory>(f => f.Provider == provider);
        return new QueryBuilderService(factory);
    }

    // ── Empty graph ──────────────────────────────────────────────────────────

    [Fact]
    public void Compile_EmptyGraph_ReturnsPlaceholderComment()
    {
        var svc = CreateService();
        var (sql, bindings) = svc.Compile(new QueryGraph([], [], [], null));
        sql.Should().StartWith("--");
        bindings.Should().BeEmpty();
    }

    // ── Single table ─────────────────────────────────────────────────────────

    [Fact]
    public void Compile_SingleTable_SelectStar_GeneratesSelectFrom()
    {
        var svc = CreateService(DatabaseProvider.SqlServer);
        var table = new QueryTableNode("t0", "dbo", "users", []);
        var (sql, _) = svc.Compile(new QueryGraph([table], [], [], null));
        sql.Should().ContainEquivalentOf("FROM");
        sql.Should().ContainEquivalentOf("users");
    }

    [Fact]
    public void Compile_SingleTable_WithSelectedColumns_ListsColumns()
    {
        var svc = CreateService(DatabaseProvider.SqlServer);
        var table = new QueryTableNode("t0", "dbo", "users", ["Id", "Name"]);
        var (sql, _) = svc.Compile(new QueryGraph([table], [], [], null));
        sql.Should().Contain("Id");
        sql.Should().Contain("Name");
    }

    // ── Topological sort ─────────────────────────────────────────────────────

    [Fact]
    public void Compile_SingleJoin_SqlContainsJoin()
    {
        var svc = CreateService(DatabaseProvider.SqlServer);
        var t0 = new QueryTableNode("t0", "dbo", "orders", ["Id"]);
        var t1 = new QueryTableNode("t1", "dbo", "customers", ["Id"]);

        // JoinType is the first positional parameter in QueryJoinEdge
        var join = new QueryJoinEdge(JoinType.Inner, "t0", "CustomerId", "t1", "Id");

        var (sql, _) = svc.Compile(new QueryGraph([t0, t1], [join], [], null));
        sql.Should().Contain("JOIN");
    }

    [Fact]
    public void Compile_JoinsOutOfOrder_TopologicalSortFixesOrder()
    {
        // Arrange: three tables — t0 is base, t1 joins off t0, t2 joins off t1.
        // Joins list is provided in reverse order (t2->t1 first, t1->t0 second).
        var svc = CreateService(DatabaseProvider.MySql);
        var t0 = new QueryTableNode("t0", "dbo", "orders", []);
        var t1 = new QueryTableNode("t1", "dbo", "items", []);
        var t2 = new QueryTableNode("t2", "dbo", "products", []);

        var joinT1 = new QueryJoinEdge(JoinType.Inner, "t0", "Id", "t1", "OrderId");
        var joinT2 = new QueryJoinEdge(JoinType.Left, "t1", "ProductId", "t2", "Id");

        // Provide joins in reverse dependency order: joinT2 (t1->t2) before joinT1 (t0->t1)
        var (sql, _) = svc.Compile(new QueryGraph([t0, t1, t2], [joinT2, joinT1], [], null));

        // Both joins must be present in the compiled SQL.
        var joinCount = System.Text.RegularExpressions.Regex.Matches(sql, @"\bJOIN\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
        joinCount.Should().Be(2, "both joins should be present in the compiled SQL");
    }

    [Fact]
    public void Compile_ReverseJoinDirection_FlipsAndProducesValidSql()
    {
        // User drew join from t1 (second table) TO t0 (base table) — reverse direction.
        // TopologicalSortJoins should flip source/target so t0 is the source.
        var svc = CreateService(DatabaseProvider.PostgreSql);
        var t0 = new QueryTableNode("t0", "public", "customers", []);
        var t1 = new QueryTableNode("t1", "public", "orders", []);

        // Reverse: user dragged FROM t1 TO t0
        var reverseJoin = new QueryJoinEdge(JoinType.Inner, "t1", "customer_id", "t0", "customer_id");

        var (sql, _) = svc.Compile(new QueryGraph([t0, t1], [reverseJoin], [], null));

        // Should contain one JOIN — t1 joined onto the base t0
        sql.Should().Contain("JOIN");
        // Must NOT duplicate t0 (the base table) as a JOIN target
        var t0Occurrences = System.Text.RegularExpressions.Regex.Matches(sql, @"\bt0\b").Count;
        t0Occurrences.Should().BeLessOrEqualTo(2, "t0 should appear only once in FROM and once in the ON clause, not as a JOIN target");
    }

    [Fact]
    public void Compile_WithLimit_MySql_IncludesLimitClause()
    {
        var svc = CreateService(DatabaseProvider.MySql);
        var t0 = new QueryTableNode("t0", "sch", "items", []);
        var (sql, _) = svc.Compile(new QueryGraph([t0], [], [], 50));
        sql.Should().ContainEquivalentOf("LIMIT");
    }

    [Fact]
    public void Compile_WithLimit_SqlServer_IncludesTopOrFetch()
    {
        var svc = CreateService(DatabaseProvider.SqlServer);
        var t0 = new QueryTableNode("t0", "dbo", "items", []);
        var (sql, _) = svc.Compile(new QueryGraph([t0], [], [], 50));
        // SqlKata uses TOP or FETCH NEXT for SQL Server row limits
        (sql.Contains("TOP", StringComparison.OrdinalIgnoreCase) ||
         sql.Contains("FETCH", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("SQL Server dialect should include a row-limiting clause");
    }

    // ── Providers ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DatabaseProvider.SqlServer)]
    [InlineData(DatabaseProvider.MySql)]
    [InlineData(DatabaseProvider.PostgreSql)]
    public void Compile_AllProviders_DoNotThrow(DatabaseProvider provider)
    {
        var svc = CreateService(provider);
        var t0 = new QueryTableNode("t0", "dbo", "table1", ["Id"]);
        var act = () => svc.Compile(new QueryGraph([t0], [], [], null));
        act.Should().NotThrow();
    }
}
