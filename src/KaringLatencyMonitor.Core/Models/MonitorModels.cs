namespace KaringLatencyMonitor.Core.Models;

public sealed record ControllerOptions(
    string BaseUrl,
    string Secret,
    string TargetUrl,
    int TimeoutSeconds,
    int MaxConcurrency,
    int IntervalMinutes)
{
    public static ControllerOptions Default { get; } = new(
        "http://127.0.0.1:3057",
        string.Empty,
        "https://www.gstatic.com/generate_204",
        15,
        5,
        10);

    public ControllerOptions Normalize()
    {
        var baseUrl = string.IsNullOrWhiteSpace(BaseUrl)
            ? Default.BaseUrl
            : BaseUrl.Trim().TrimEnd('/');
        var targetUrl = string.IsNullOrWhiteSpace(TargetUrl)
            ? Default.TargetUrl
            : TargetUrl.Trim();

        return this with
        {
            BaseUrl = baseUrl,
            TargetUrl = targetUrl,
            TimeoutSeconds = Math.Clamp(TimeoutSeconds, 1, 60),
            MaxConcurrency = Math.Clamp(MaxConcurrency, 1, 20),
            IntervalMinutes = Math.Clamp(IntervalMinutes, 1, 1440)
        };
    }
}

public sealed record NodeGroupDescriptor(
    string Name,
    string Type,
    string? CurrentTag,
    IReadOnlyList<string> Nodes,
    bool SelectNewNodesByDefault = true);

public enum ProbeOutcomeKind
{
    Success,
    NodeFailure,
    ControllerUnavailable
}

public sealed record ProbeOutcome(
    string Tag,
    long MeasuredAtMs,
    int? DelayMs,
    ProbeOutcomeKind Kind,
    string? Error,
    int RequestCostMs)
{
    public bool IsSuccess => Kind == ProbeOutcomeKind.Success;

    public bool ShouldPersist => Kind != ProbeOutcomeKind.ControllerUnavailable;
}

public enum ProbeRunStatus
{
    Running,
    Complete,
    Partial,
    ControllerOffline,
    Cancelled,
    Failed
}

public sealed record CollectionResult(
    long RunId,
    ProbeRunStatus Status,
    int ExpectedCount,
    int SuccessCount,
    int FailureCount,
    string? Error)
{
    public static CollectionResult Empty(string? error = null) =>
        new(0, ProbeRunStatus.Failed, 0, 0, 0, error);
}

public enum HeatmapCellState
{
    NoData,
    ControllerOffline,
    Failed,
    Success
}

public enum LatencyBand
{
    None,
    Green,
    LightGreen,
    Yellow,
    Orange,
    Red,
    Failed
}

public static class LatencyBands
{
    public static LatencyBand FromDelay(double delayMs) => delayMs switch
    {
        < 100 => LatencyBand.Green,
        < 200 => LatencyBand.LightGreen,
        < 300 => LatencyBand.Yellow,
        < 400 => LatencyBand.Orange,
        _ => LatencyBand.Red
    };
}

public enum AvailabilityBand
{
    None,
    Green,
    Yellow,
    Red,
    Failed
}

public static class AvailabilityBands
{
    public static AvailabilityBand FromCounts(int attempts, int successes)
    {
        if (attempts <= 0)
        {
            return AvailabilityBand.None;
        }

        if (successes <= 0)
        {
            return AvailabilityBand.Failed;
        }

        if (successes >= attempts)
        {
            return AvailabilityBand.Green;
        }

        var percentage = 100.0 * successes / attempts;
        return percentage >= 50
            ? AvailabilityBand.Yellow
            : AvailabilityBand.Red;
    }
}

public sealed record HeatmapCell(
    int Index,
    long StartAtMs,
    long EndAtMs,
    HeatmapCellState State,
    LatencyBand Band,
    double? AverageDelayMs,
    int? MaximumDelayMs,
    int Attempts,
    int Successes,
    bool ControllerWasOffline)
{
    public double? AvailabilityPercent =>
        Attempts == 0 ? null : 100.0 * Successes / Attempts;
}

public sealed record PeriodStatistics(
    double? AverageDelayMs,
    int Attempts,
    int Successes)
{
    public double? AvailabilityPercent =>
        Attempts == 0 ? null : 100.0 * Successes / Attempts;
}

public enum DashboardSortKey
{
    Default,
    Hours24Delay,
    Hours24Availability,
    Days7Delay,
    Days7Availability,
    Days30Delay,
    Days30Availability
}

public sealed record DashboardSortPreference(
    DashboardSortKey Key,
    bool Descending)
{
    public static DashboardSortPreference Default { get; } =
        new(DashboardSortKey.Default, false);
}

public sealed record NodeStatisticsRow(
    string Tag,
    int Ordinal,
    bool IsPresent,
    IReadOnlyList<HeatmapCell> HourCells,
    PeriodStatistics Hours24,
    PeriodStatistics Days7,
    PeriodStatistics Days30);

public sealed record DashboardSnapshot(
    string GroupName,
    long AnchorAtMs,
    IReadOnlyList<NodeStatisticsRow> Rows)
{
    public static DashboardSnapshot Empty(string groupName, long anchorAtMs) =>
        new(groupName, anchorAtMs, Array.Empty<NodeStatisticsRow>());
}

public sealed record SelectableNode(
    string Tag,
    int Ordinal,
    bool IsPresent,
    bool IsSelected);
