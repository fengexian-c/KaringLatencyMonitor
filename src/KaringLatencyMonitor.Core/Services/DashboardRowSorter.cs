using KaringLatencyMonitor.Core.Models;

namespace KaringLatencyMonitor.Core.Services;

public static class DashboardRowSorter
{
    public static IReadOnlyList<NodeStatisticsRow> Sort(
        IReadOnlyList<NodeStatisticsRow> rows,
        DashboardSortPreference preference)
    {
        if (preference.Key == DashboardSortKey.Default || rows.Count < 2)
        {
            return rows.ToArray();
        }

        var indexed = rows.Select((row, defaultIndex) => new IndexedRow(row, defaultIndex));
        var ranked = indexed.OrderBy(item =>
            MissingRank(Period(item.Row, preference.Key), preference.Key));
        IOrderedEnumerable<IndexedRow> ordered = preference.Descending
            ? ranked.ThenByDescending(item => SortValue(item.Row, preference.Key) ?? 0)
            : ranked.ThenBy(item => SortValue(item.Row, preference.Key) ?? 0);

        return ordered
            .ThenBy(item => item.DefaultIndex)
            .Select(item => item.Row)
            .ToArray();
    }

    private static PeriodStatistics Period(NodeStatisticsRow row, DashboardSortKey key) => key switch
    {
        DashboardSortKey.Hours24Delay or DashboardSortKey.Hours24Availability => row.Hours24,
        DashboardSortKey.Days7Delay or DashboardSortKey.Days7Availability => row.Days7,
        DashboardSortKey.Days30Delay or DashboardSortKey.Days30Availability => row.Days30,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null)
    };

    private static double? SortValue(NodeStatisticsRow row, DashboardSortKey key)
    {
        var statistics = Period(row, key);
        return IsAvailabilityKey(key)
            ? statistics.AvailabilityPercent
            : statistics.AverageDelayMs;
    }

    private static int MissingRank(PeriodStatistics statistics, DashboardSortKey key)
    {
        if (IsAvailabilityKey(key))
        {
            return statistics.Attempts > 0 ? 0 : 1;
        }

        return statistics switch
        {
            { AverageDelayMs: not null } => 0,
            { Attempts: > 0 } => 1,
            _ => 2
        };
    }

    private static bool IsAvailabilityKey(DashboardSortKey key) => key is
        DashboardSortKey.Hours24Availability or
        DashboardSortKey.Days7Availability or
        DashboardSortKey.Days30Availability;

    private sealed record IndexedRow(NodeStatisticsRow Row, int DefaultIndex);
}
