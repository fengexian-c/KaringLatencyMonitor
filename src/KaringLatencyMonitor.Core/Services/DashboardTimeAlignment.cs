namespace KaringLatencyMonitor.Core.Services;

public static class DashboardTimeAlignment
{
    public const long FiveMinutesMs = 5L * 60 * 1000;

    public static long CeilToFiveMinutes(long unixTimeMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(unixTimeMs);
        var remainder = unixTimeMs % FiveMinutesMs;
        return remainder == 0
            ? unixTimeMs
            : checked(unixTimeMs + FiveMinutesMs - remainder);
    }
}
