using DbExplorer.Core.Models;

namespace DbExplorer.Core.Analysis;

public sealed record CategoryStat(DbActionCategory Category, int Count, int Errors, long P50, long P95, long Max);

public sealed record OperationStat(string Operation, int Count, int Errors, long P50, long P95);

public sealed record Bucket(string Label, int Total, int Errors);

/// <summary>
/// Pure statistics helpers for the Systems Analyser dashboard. Kept dependency-free so
/// percentile/bucketing/grouping logic is unit-testable without a live event store.
/// </summary>
public static class AnalyserMath
{
    /// <summary>
    /// Percentile via linear interpolation between the two closest ranks (the common
    /// "R-7"/NumPy-default method). <paramref name="sortedAscending"/> must already be sorted.
    /// </summary>
    public static long Percentile(IReadOnlyList<long> sortedAscending, double p)
    {
        if (sortedAscending.Count == 0) return 0;
        if (sortedAscending.Count == 1) return sortedAscending[0];

        var rank = p * (sortedAscending.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper) return sortedAscending[lower];

        var fraction = rank - lower;
        var interpolated = sortedAscending[lower] + (sortedAscending[upper] - sortedAscending[lower]) * fraction;
        return (long)Math.Round(interpolated, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Buckets events into fixed-width, contiguous time slots starting at <paramref name="start"/>,
    /// so the x-axis stays stable even when some minutes have zero events.
    /// </summary>
    public static IReadOnlyList<Bucket> Bucketize(
        IReadOnlyList<DbActionEvent> events, DateTimeOffset start, int bucketMinutes, int bucketCount)
    {
        var totals = new int[bucketCount];
        var errors = new int[bucketCount];
        foreach (var e in events)
        {
            var idx = (int)((e.Timestamp - start).TotalMinutes / bucketMinutes);
            if (idx < 0 || idx >= bucketCount) continue;
            totals[idx]++;
            if (!e.Success) errors[idx]++;
        }

        var buckets = new Bucket[bucketCount];
        for (var i = 0; i < bucketCount; i++)
        {
            var label = (start + TimeSpan.FromMinutes((double)i * bucketMinutes)).ToString("HH:mm");
            buckets[i] = new Bucket(label, totals[i], errors[i]);
        }
        return buckets;
    }

    public static IReadOnlyList<CategoryStat> SummarizeByCategory(IReadOnlyList<DbActionEvent> events) =>
        events
            .GroupBy(e => e.Category)
            .Select(g =>
            {
                var timed = g.Where(e => e.ElapsedMs >= 0).Select(e => e.ElapsedMs).OrderBy(v => v).ToList();
                return new CategoryStat(
                    g.Key, g.Count(), g.Count(e => !e.Success),
                    Percentile(timed, 0.50), Percentile(timed, 0.95),
                    timed.Count == 0 ? 0 : timed[^1]);
            })
            .OrderByDescending(x => x.Count)
            .ToList();

    /// <summary>Top-<paramref name="top"/> operations by event count, each with p50/p95 latency.</summary>
    public static IReadOnlyList<OperationStat> SummarizeByOperation(IReadOnlyList<DbActionEvent> events, int top = 15) =>
        events
            .GroupBy(e => e.Operation)
            .Select(g =>
            {
                var timed = g.Where(e => e.ElapsedMs >= 0).Select(e => e.ElapsedMs).OrderBy(v => v).ToList();
                return new OperationStat(
                    g.Key, g.Count(), g.Count(e => !e.Success),
                    Percentile(timed, 0.50), Percentile(timed, 0.95));
            })
            .OrderByDescending(x => x.Count)
            .Take(top)
            .ToList();
}
