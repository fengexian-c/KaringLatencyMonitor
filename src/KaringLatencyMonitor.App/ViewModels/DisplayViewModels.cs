using CommunityToolkit.Mvvm.ComponentModel;
using KaringLatencyMonitor.Core.Models;

namespace KaringLatencyMonitor.App.ViewModels;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial record GroupOptionViewModel(string Name, string Type, int NodeCount)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Type)
        ? $"{Name} ({NodeCount})"
        : $"{Name} · {Type} ({NodeCount})";
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class NodeSelectionViewModel : ObservableObject
{
    private bool _isSelected;

    public NodeSelectionViewModel(SelectableNode node)
    {
        Tag = node.Tag;
        Ordinal = node.Ordinal;
        IsPresent = node.IsPresent;
        _isSelected = node.IsSelected;
    }

    public string Tag { get; }

    public int Ordinal { get; }

    public bool IsPresent { get; }

    public double PresenceOpacity => IsPresent ? 1.0 : 0.52;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string PresenceText => IsPresent ? string.Empty : "已移出当前组";
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class HeatmapCellDisplay
{
    public HeatmapCellDisplay(HeatmapCell cell)
    {
        FillArgb = HeatmapPalette.GetFillArgb(cell);
        BorderArgb = HeatmapPalette.NoDataBorderArgb;
        HasBorder = cell.State is HeatmapCellState.NoData or HeatmapCellState.ControllerOffline;
        var availabilityBand = AvailabilityBands.FromCounts(cell.Attempts, cell.Successes);
        HasAvailabilityDot = availabilityBand != AvailabilityBand.None;
        AvailabilityDotArgb = HeatmapPalette.GetAvailabilityDotArgb(availabilityBand);
        TooltipText = BuildTooltip(cell);
    }

    public uint FillArgb { get; }

    public uint BorderArgb { get; }

    public bool HasBorder { get; }

    public bool HasAvailabilityDot { get; }

    public uint AvailabilityDotArgb { get; }

    public string TooltipText { get; }

    private static string BuildTooltip(HeatmapCell cell)
    {
        var start = DateTimeOffset.FromUnixTimeMilliseconds(cell.StartAtMs).ToLocalTime();
        var end = DateTimeOffset.FromUnixTimeMilliseconds(cell.EndAtMs).ToLocalTime();
        var interval = $"{start:MM-dd HH:mm}–{end:HH:mm}";

        return cell.State switch
        {
            HeatmapCellState.ControllerOffline =>
                $"{interval}\nKaring 控制器离线，没有节点采样",
            HeatmapCellState.NoData =>
                $"{interval}\n没有采样数据",
            HeatmapCellState.Failed =>
                $"{interval}\n尝试 {cell.Attempts} 次，全部失败或超时"
                + OfflineSuffix(cell),
            _ =>
                $"{interval}\n平均 {cell.AverageDelayMs:0} ms，最大 {cell.MaximumDelayMs} ms"
                + $"\n成功 {cell.Successes}/{cell.Attempts}，可用率 {cell.AvailabilityPercent:0.0}%"
                + OfflineSuffix(cell)
        };
    }

    private static string OfflineSuffix(HeatmapCell cell) =>
        cell.ControllerWasOffline ? "\n该小时内控制器曾离线" : string.Empty;
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class PeriodStatisticsDisplay
{
    public PeriodStatisticsDisplay(PeriodStatistics statistics)
    {
        DelayText = statistics switch
        {
            { AverageDelayMs: not null } => $"{statistics.AverageDelayMs:0}",
            { Attempts: > 0 } => "失败",
            _ => "—"
        };
        AvailabilityText = statistics.AvailabilityPercent is null
            ? "—"
            : $"{statistics.AvailabilityPercent:0.0}%";
        TooltipText = statistics.Attempts == 0
            ? "没有节点探测记录"
            : $"成功 {statistics.Successes}/{statistics.Attempts}"
              + (statistics.AverageDelayMs is null
                  ? "，没有成功的延迟样本"
                  : $"，成功样本平均 {statistics.AverageDelayMs:0.0} ms");
    }

    public string DelayText { get; }

    public string AvailabilityText { get; }

    public string TooltipText { get; }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class NodeStatisticsRowDisplay
{
    public NodeStatisticsRowDisplay(NodeStatisticsRow row)
    {
        Tag = row.Tag;
        IsPresent = row.IsPresent;
        PresenceOpacity = row.IsPresent ? 1.0 : 0.52;
        HourCells = row.HourCells.Select(cell => new HeatmapCellDisplay(cell)).ToArray();
        Hours24 = new PeriodStatisticsDisplay(row.Hours24);
        Days7 = new PeriodStatisticsDisplay(row.Days7);
        Days30 = new PeriodStatisticsDisplay(row.Days30);
    }

    public string Tag { get; }

    public bool IsPresent { get; }

    public double PresenceOpacity { get; }

    public IReadOnlyList<HeatmapCellDisplay> HourCells { get; }

    public PeriodStatisticsDisplay Hours24 { get; }

    public PeriodStatisticsDisplay Days7 { get; }

    public PeriodStatisticsDisplay Days30 { get; }
}

internal static class HeatmapPalette
{
    private const uint GreenArgb = 0xFF22C55E;
    private const uint LightGreenArgb = 0xFF84CC16;
    private const uint YellowArgb = 0xFFEAB308;
    private const uint OrangeArgb = 0xFFF97316;
    private const uint RedArgb = 0xFFEF4444;
    private const uint FailedArgb = 0xFF9CA3AF;
    private const uint NoDataArgb = 0xFFFFFFFF;

    public const uint NoDataBorderArgb = 0xFFE5E7EB;

    public static uint GetFillArgb(HeatmapCell cell) => cell.State switch
    {
        HeatmapCellState.NoData or HeatmapCellState.ControllerOffline => NoDataArgb,
        HeatmapCellState.Failed => FailedArgb,
        _ => cell.Band switch
        {
            LatencyBand.Green => GreenArgb,
            LatencyBand.LightGreen => LightGreenArgb,
            LatencyBand.Yellow => YellowArgb,
            LatencyBand.Orange => OrangeArgb,
            LatencyBand.Red => RedArgb,
            _ => NoDataArgb
        }
    };

    public static uint GetAvailabilityDotArgb(AvailabilityBand band) => band switch
    {
        AvailabilityBand.Green => GreenArgb,
        AvailabilityBand.Yellow => YellowArgb,
        AvailabilityBand.Red => RedArgb,
        AvailabilityBand.Failed => FailedArgb,
        _ => 0
    };
}
