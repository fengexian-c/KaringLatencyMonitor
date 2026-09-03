using KaringLatencyMonitor.Core.Models;

namespace KaringLatencyMonitor.App.Models;

public sealed record AppSettings(
    string BaseUrl,
    string TargetUrl,
    int TimeoutSeconds,
    int MaxConcurrency,
    int IntervalMinutes,
    bool AutoCollectionEnabled,
    string? SelectedGroupName)
{
    public static AppSettings Default { get; } = new(
        ControllerOptions.Default.BaseUrl,
        ControllerOptions.Default.TargetUrl,
        ControllerOptions.Default.TimeoutSeconds,
        ControllerOptions.Default.MaxConcurrency,
        ControllerOptions.Default.IntervalMinutes,
        true,
        null);

    public ControllerOptions ToControllerOptions(string secret) => new ControllerOptions(
        BaseUrl,
        secret,
        TargetUrl,
        TimeoutSeconds,
        MaxConcurrency,
        IntervalMinutes).Normalize();
}
