using DbExplorer.Core.Analysis;
using DbExplorer.Core.Models;
using FluentAssertions;
using Xunit;

namespace DbExplorer.Tests.Unit;

public class AnalyserMathTests
{
    private static DbActionEvent Evt(long elapsedMs, bool success = true, DateTimeOffset? ts = null, string operation = "op",
        DbActionCategory category = DbActionCategory.Metadata) =>
        new(ts ?? DateTimeOffset.UtcNow, "MySql", category, operation,
            null, null, elapsedMs, 1, success, success ? null : "TestException",
            success ? null : "boom", "tester");

    [Fact]
    public void Percentile_EmptyList_ReturnsZero()
    {
        AnalyserMath.Percentile([], 0.95).Should().Be(0);
    }

    [Fact]
    public void Percentile_SingleValue_ReturnsThatValue()
    {
        AnalyserMath.Percentile([42], 0.5).Should().Be(42);
    }

    [Fact]
    public void Percentile_KnownQuartiles_InterpolatesCorrectly()
    {
        List<long> values = [10, 20, 30, 40, 50];

        // rank = 0.5 * 4 = 2 -> exact index 2 -> 30
        AnalyserMath.Percentile(values, 0.5).Should().Be(30);
        // rank = 0 -> index 0 -> 10
        AnalyserMath.Percentile(values, 0).Should().Be(10);
        // rank = 1 * 4 = 4 -> index 4 -> 50
        AnalyserMath.Percentile(values, 1).Should().Be(50);
    }

    [Fact]
    public void Percentile_BetweenRanks_LinearlyInterpolates()
    {
        List<long> values = [0, 100];

        // rank = 0.25 * 1 = 0.25 -> interpolate between 0 and 100 -> 25
        AnalyserMath.Percentile(values, 0.25).Should().Be(25);
    }

    [Fact]
    public void Bucketize_EventsOutsideRange_AreExcluded()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        var events = new List<DbActionEvent>
        {
            Evt(5, ts: start.AddMinutes(-5)), // before window
            Evt(5, ts: start.AddMinutes(2)),  // in window, bucket 2
            Evt(5, ts: start.AddMinutes(50)), // after window
        };

        var buckets = AnalyserMath.Bucketize(events, start, bucketMinutes: 1, bucketCount: 10);

        buckets.Should().HaveCount(10);
        buckets.Sum(b => b.Total).Should().Be(1);
        buckets[2].Total.Should().Be(1);
    }

    [Fact]
    public void Bucketize_CountsErrorsSeparatelyFromTotal()
    {
        var start = DateTimeOffset.UtcNow;
        var events = new List<DbActionEvent>
        {
            Evt(5, success: true, ts: start),
            Evt(5, success: false, ts: start),
        };

        var buckets = AnalyserMath.Bucketize(events, start, bucketMinutes: 1, bucketCount: 1);

        buckets[0].Total.Should().Be(2);
        buckets[0].Errors.Should().Be(1);
    }

    [Fact]
    public void SummarizeByCategory_GroupsAndOrdersByCount()
    {
        var events = new List<DbActionEvent>
        {
            Evt(10, category: DbActionCategory.Metadata),
            Evt(20, category: DbActionCategory.Metadata),
            Evt(30, category: DbActionCategory.AdHocQuery, success: false),
        };

        var summary = AnalyserMath.SummarizeByCategory(events);

        summary.Should().HaveCount(2);
        summary[0].Category.Should().Be(DbActionCategory.Metadata);
        summary[0].Count.Should().Be(2);
        summary[0].Max.Should().Be(20);
        summary[1].Errors.Should().Be(1);
    }

    [Fact]
    public void SummarizeByOperation_CapsAtTopN()
    {
        var events = Enumerable.Range(0, 20)
            .Select(i => Evt(i, operation: $"op{i}"))
            .ToList();
        events.Add(Evt(999, operation: "op0")); // op0 now has 2 events, should rank first

        var summary = AnalyserMath.SummarizeByOperation(events, top: 5);

        summary.Should().HaveCount(5);
        summary[0].Operation.Should().Be("op0");
        summary[0].Count.Should().Be(2);
    }
}
