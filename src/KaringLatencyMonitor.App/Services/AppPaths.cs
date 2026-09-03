namespace KaringLatencyMonitor.App.Services;

public static class AppPaths
{
    public static string ApplicationDirectory { get; } =
        Path.GetFullPath(AppContext.BaseDirectory);

    public static string DataDirectory { get; } = Path.Combine(
        ApplicationDirectory,
        "data");

    public static string DatabasePath => Path.Combine(DataDirectory, "latency.db");

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
}
