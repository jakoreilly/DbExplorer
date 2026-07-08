using DbExplorer.Core.Layout;
using FluentAssertions;
using Xunit;

namespace DbExplorer.Tests.Unit;

public class EntityMapLayoutTests
{
    private static LayoutNode Node(string id) => new(id, 100, 40);

    [Fact]
    public void Compute_EmptyGraph_ReturnsEmptyPositions()
    {
        var result = EntityMapLayout.Compute([], []);

        result.Positions.Should().BeEmpty();
    }

    [Fact]
    public void Compute_SingleNode_PlacesAtOrigin()
    {
        var result = EntityMapLayout.Compute([Node("A")], []);

        result.Positions.Should().ContainKey("A");
        result.Positions["A"].Should().Be((0, 0));
    }

    [Fact]
    public void Compute_Chain_PlacesEachLinkInASeparateColumn()
    {
        // A -> B -> C (A references B, B references C: C is the root/parent)
        var nodes = new[] { Node("A"), Node("B"), Node("C") };
        var edges = new[] { new LayoutEdge("A", "B"), new LayoutEdge("B", "C") };

        var result = EntityMapLayout.Compute(nodes, edges);

        var xa = result.Positions["A"].X;
        var xb = result.Positions["B"].X;
        var xc = result.Positions["C"].X;

        xc.Should().BeLessThan(xb);
        xb.Should().BeLessThan(xa);
    }

    [Fact]
    public void Compute_Cycle_TerminatesAndPlacesAllNodes()
    {
        var nodes = new[] { Node("A"), Node("B") };
        var edges = new[] { new LayoutEdge("A", "B"), new LayoutEdge("B", "A") };

        var result = EntityMapLayout.Compute(nodes, edges);

        result.Positions.Should().ContainKeys("A", "B");
    }

    [Fact]
    public void Compute_SameInput_IsDeterministic()
    {
        var nodes = new[] { Node("Orders"), Node("Customers"), Node("OrderItems"), Node("Products") };
        var edges = new[]
        {
            new LayoutEdge("Orders", "Customers"),
            new LayoutEdge("OrderItems", "Orders"),
            new LayoutEdge("OrderItems", "Products"),
        };

        var first = EntityMapLayout.Compute(nodes, edges);
        var second = EntityMapLayout.Compute(nodes, edges);

        first.Positions.Should().BeEquivalentTo(second.Positions);
    }

    [Fact]
    public void Compute_IsolatedNodes_PlacedInLayerZero()
    {
        var nodes = new[] { Node("Orphan1"), Node("Orphan2") };

        var result = EntityMapLayout.Compute(nodes, []);

        result.Positions["Orphan1"].X.Should().Be(0);
        result.Positions["Orphan2"].X.Should().Be(0);
    }

    [Fact]
    public void Compute_SelfReferencingForeignKey_IsIgnoredForLayering()
    {
        var nodes = new[] { Node("Employees") };
        var edges = new[] { new LayoutEdge("Employees", "Employees") };

        var result = EntityMapLayout.Compute(nodes, edges);

        result.Positions["Employees"].Should().Be((0, 0));
    }

    [Fact]
    public void Neighborhood_OneHop_ReturnsRootAndDirectNeighborsOnly()
    {
        var edges = new[]
        {
            new LayoutEdge("OrderItems", "Orders"),
            new LayoutEdge("Orders", "Customers"),
        };

        var result = EntityMapGraph.Neighborhood("Orders", 1, edges);

        result.Should().BeEquivalentTo(["Orders", "OrderItems", "Customers"]);
    }

    [Fact]
    public void Neighborhood_TwoHops_ReachesTransitiveNeighbors()
    {
        var edges = new[]
        {
            new LayoutEdge("OrderItems", "Orders"),
            new LayoutEdge("Orders", "Customers"),
            new LayoutEdge("Customers", "Regions"),
        };

        var result = EntityMapGraph.Neighborhood("OrderItems", 2, edges);

        result.Should().BeEquivalentTo(["OrderItems", "Orders", "Customers"]);
        result.Should().NotContain("Regions");
    }

    [Fact]
    public void Neighborhood_ZeroHops_ReturnsOnlyRoot()
    {
        var edges = new[] { new LayoutEdge("Orders", "Customers") };

        var result = EntityMapGraph.Neighborhood("Orders", 0, edges);

        result.Should().BeEquivalentTo(["Orders"]);
    }
}
