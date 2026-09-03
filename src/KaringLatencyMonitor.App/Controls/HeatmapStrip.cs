using System.Runtime.InteropServices.WindowsRuntime;
using KaringLatencyMonitor.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace KaringLatencyMonitor.App.Controls;

/// <summary>
/// Renders all 24 hourly cells into one tiny bitmap. This replaces an
/// ItemsRepeater plus 24 Border elements for every visible node row.
/// </summary>
public sealed partial class HeatmapStrip : UserControl
{
    private const int CellWidth = 7;
    private const int CellHeight = 14;
    private const int CellSpacing = 3;
    private const int CellCornerRadius = 3;
    private const int AvailabilityDotDiameter = 5;
    private const int AvailabilityDotTop = CellHeight + 2;
    private const int BitmapHeight = AvailabilityDotTop + AvailabilityDotDiameter;
    private readonly ToolTip _toolTip = new();
    private IReadOnlyList<HeatmapCellDisplay> _cells = Array.Empty<HeatmapCellDisplay>();

    public HeatmapStrip()
    {
        InitializeComponent();
        Width = PixelWidth(24);
        Height = BitmapHeight;
        ToolTipService.SetToolTip(this, _toolTip);
        AutomationProperties.SetName(this, "最近24小时延迟状态");
        PointerMoved += OnPointerMoved;
        PointerExited += OnPointerExited;
    }

    public IReadOnlyList<HeatmapCellDisplay> Cells
    {
        get => _cells;
        set
        {
            _cells = value ?? Array.Empty<HeatmapCellDisplay>();
            RenderCells();
        }
    }

    private void RenderCells()
    {
        if (_cells.Count == 0)
        {
            BitmapImage.Source = null;
            return;
        }

        var width = PixelWidth(_cells.Count);
        var pixels = new byte[width * BitmapHeight * 4];
        for (var index = 0; index < _cells.Count; index++)
        {
            var startX = index * (CellWidth + CellSpacing);
            DrawCell(
                pixels,
                width,
                startX,
                _cells[index]);
            if (_cells[index].HasAvailabilityDot)
            {
                DrawAvailabilityDot(
                    pixels,
                    width,
                    startX + (CellWidth - AvailabilityDotDiameter) / 2,
                    AvailabilityDotTop,
                    _cells[index].AvailabilityDotArgb);
            }
        }

        var bitmap = new WriteableBitmap(width, BitmapHeight);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(pixels, 0, pixels.Length);
        }

        bitmap.Invalidate();
        Width = width;
        BitmapImage.Source = bitmap;
    }

    private static void DrawAvailabilityDot(
        byte[] pixels,
        int bitmapWidth,
        int startX,
        int startY,
        uint color)
    {
        const double center = (AvailabilityDotDiameter - 1) / 2.0;
        const double radius = AvailabilityDotDiameter / 2.0;
        var radiusSquared = radius * radius;
        for (var y = 0; y < AvailabilityDotDiameter; y++)
        {
            for (var x = 0; x < AvailabilityDotDiameter; x++)
            {
                var deltaX = x - center;
                var deltaY = y - center;
                if (deltaX * deltaX + deltaY * deltaY <= radiusSquared)
                {
                    SetPixel(pixels, bitmapWidth, startX + x, startY + y, color);
                }
            }
        }
    }

    private static void DrawCell(
        byte[] pixels,
        int bitmapWidth,
        int startX,
        HeatmapCellDisplay cell)
    {
        for (var y = 0; y < CellHeight; y++)
        {
            for (var x = 0; x < CellWidth; x++)
            {
                if (!IsInsideRoundedRectangle(
                        x,
                        y,
                        CellWidth,
                        CellHeight,
                        CellCornerRadius))
                {
                    continue;
                }

                var color = cell.FillArgb;
                if (cell.HasBorder
                    && !IsInsideRoundedRectangle(
                        x - 1,
                        y - 1,
                        CellWidth - 2,
                        CellHeight - 2,
                        CellCornerRadius - 1))
                {
                    color = cell.BorderArgb;
                }

                SetPixel(pixels, bitmapWidth, startX + x, y, color);
            }
        }
    }

    private static bool IsInsideRoundedRectangle(
        int x,
        int y,
        int width,
        int height,
        int radius)
    {
        if (x < 0 || y < 0 || x >= width || y >= height)
        {
            return false;
        }

        if ((x >= radius && x < width - radius)
            || (y >= radius && y < height - radius))
        {
            return true;
        }

        var centerX = x < radius ? radius - 0.5 : width - radius - 0.5;
        var centerY = y < radius ? radius - 0.5 : height - radius - 0.5;
        var deltaX = x + 0.5 - centerX;
        var deltaY = y + 0.5 - centerY;
        return deltaX * deltaX + deltaY * deltaY <= radius * radius;
    }

    private static void SetPixel(
        byte[] pixels,
        int bitmapWidth,
        int x,
        int y,
        uint argb)
    {
        var offset = (y * bitmapWidth + x) * 4;
        pixels[offset] = (byte)argb;
        pixels[offset + 1] = (byte)(argb >> 8);
        pixels[offset + 2] = (byte)(argb >> 16);
        pixels[offset + 3] = (byte)(argb >> 24);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_cells.Count == 0)
        {
            return;
        }

        var x = Math.Max(0, args.GetCurrentPoint(this).Position.X);
        var index = Math.Min(
            _cells.Count - 1,
            (int)(x / (CellWidth + CellSpacing)));
        _toolTip.Content = _cells[index].TooltipText;
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs args) =>
        _toolTip.Content = null;

    private static int PixelWidth(int cellCount) =>
        Math.Max(0, cellCount * CellWidth + Math.Max(0, cellCount - 1) * CellSpacing);
}
