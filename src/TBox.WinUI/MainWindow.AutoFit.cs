using TBox.Interop;
using TBox.Models;

namespace TBox;

/// <summary>
/// Content-driven window sizing.
///
/// The panel used to keep whatever size the user last dragged it to, so a group with three tools
/// sat in a mostly empty 760×540 window. With auto-fit on, the window is sized from the group's
/// item count: just enough columns and rows for the icon grid, capped by a configurable row limit
/// and by the user's preferred width.
///
/// Sizes here are DIPs because the grid metrics in MainWindow.xaml are DIPs; they go through
/// <see cref="Dpi.Px"/> before reaching AppWindow, which works in physical pixels.
/// </summary>
public sealed partial class MainWindow
{
    // Must match the GridView metrics in MainWindow.xaml: ItemsWrapGrid cells are 96×96 and the
    // GridView padding is 10,2,6,8. The fixed rows are header 46 + tabs 42 + status 28.
    private const int FitTile = 96;
    private const int FitGridPaddingX = 16;
    private const int FitChromeHeight = 126;
    private const int FitEmptyWidth = 360;
    private const int FitEmptyHeight = 320;
    private const int FitPreferredColumns = 4;
    private const int FitMaxColumns = 8;
    private const int FitMaxRowsLimit = 8;
    private const int FitScreenMargin = 16;

    private bool _autoFitReady;
    private double _autoFitScale = -1;

    private bool AutoFitEnabled => Settings.AutoFitWindow;

    private int AutoFitRowLimit => Math.Clamp(Settings.AutoFitMaxRows, 1, FitMaxRowsLimit);

    /// <summary>Width a docked panel restores to when auto-fit is off.</summary>
    private int DockWidthPx => Math.Max(Dip(MinExpandedWidth), (int)Settings.DockWidth);

    private readonly record struct FitSize(int WidthPx, int HeightPx, int Columns, int Rows, int VisibleRows);

    /// <summary>
    /// The size that shows <paramref name="count"/> icons without empty rows. Columns start at four
    /// for a compact layout and grow while that would bring the row count under the limit; rows
    /// beyond the limit stay scrollable inside the GridView.
    /// </summary>
    private FitSize ComputeFitSize(int count)
    {
        var scale = Scale;
        var area = WorkArea();
        var areaWidthDip = Dpi.Dip(area.Width, scale) - FitScreenMargin * 2;
        var maxColsByScreen = Math.Max(1, (int)Math.Floor((areaWidthDip - FitGridPaddingX) / FitTile));

        // Both floating and docked panels widen with the icon count, up to a comfortable toolbar
        // width (8 columns). A docked panel is still placed flush against the edge; dockWidth only
        // matters as the restore size when auto-fit is off.
        var maxCols = Math.Max(1, Math.Min(FitMaxColumns, maxColsByScreen));

        var areaHeightDip = Dpi.Dip(area.Height, scale) - FitScreenMargin * 2;
        var maxRowsByScreen = Math.Max(1, (int)Math.Floor((areaHeightDip - FitChromeHeight) / FitTile));
        var rowLimit = Math.Clamp(Math.Min(AutoFitRowLimit, maxRowsByScreen), 1, FitMaxRowsLimit);

        if (count <= 0)
        {
            // The empty state (icon + hint + two buttons) needs a little more than one icon row.
            return new FitSize(
                Dpi.Px(Math.Max(MinExpandedWidth, Math.Min(FitEmptyWidth, areaWidthDip)), scale),
                Dpi.Px(Math.Min(FitEmptyHeight, areaHeightDip), scale),
                0, 0, 0);
        }

        var cols = Math.Max(1, Math.Min(Math.Min(count, FitPreferredColumns), maxCols));
        var rows = (count + cols - 1) / cols;

        // Honour the row cap by widening first; only scroll after the width is used up. This is
        // what makes "max rows = 1" a real single-row toolbar instead of a three-icon sliver.
        while (rows > rowLimit && cols < maxCols)
        {
            cols++;
            rows = (count + cols - 1) / cols;
        }

        var visibleRows = Math.Min(rows, rowLimit);
        var widthDip = Math.Max(MinExpandedWidth, Math.Min(areaWidthDip, cols * FitTile + FitGridPaddingX));
        // Exact row height: no sliver of the next row peeking under the status bar.
        var heightDip = Math.Min(areaHeightDip, FitChromeHeight + visibleRows * FitTile);

        return new FitSize(Dpi.Px(widthDip, scale), Dpi.Px(heightDip, scale), cols, rows, visibleRows);
    }

    /// <summary>
    /// Size the expanded docked panel should use. Auto-fit may be narrower than the dock width;
    /// the panel is still placed flush against the edge, so the free space is simply not covered.
    /// </summary>
    private (int Width, int Height) ExpandedSizePx()
    {
        if (AutoFitEnabled)
        {
            var fit = ComputeFitSize(_currentGroup?.Items.Count ?? 0);
            return (fit.WidthPx, fit.HeightPx);
        }

        var height = (int)(Box.Window.Height > 200 ? Box.Window.Height : Dip(540));
        // Side docks restore DockWidth; top and floating layouts restore the window width.
        var width = IsDocked && CurrentDock != DockSide.Top
            ? DockWidthPx
            : Math.Max(Dip(MinExpandedWidth), (int)Box.Window.Width);
        return (width, height);
    }

    /// <summary>
    /// Applies the current group's fit size. Actual geometry is not written back to
    /// Box.Window.Width/Height: those stay the user's preferred size, so turning auto-fit off
    /// restores the window instead of leaving it stuck at a shrunk size.
    /// </summary>
    private void ApplyAutoFit()
    {
        if (!AutoFitEnabled || !_autoFitReady || _sliding) return;

        // Docked and collapsed: the panel is parked off-screen. Its height is recomputed on expand.
        if (IsDocked && _collapsed) return;

        var count = _currentGroup?.Items.Count ?? 0;
        var fit = ComputeFitSize(count);
        var width = fit.WidthPx;
        var height = fit.HeightPx;

        ResizePanel(width, height);

        // The flyout is anchored to the bottom-right corner, so it must be re-parked after resizing.
        if (_mode == PanelMode.Flyout) PositionNearTray();

        App.Log($"[fit] items={count} cols={fit.Columns} rows={fit.VisibleRows}/{fit.Rows} " +
                $"maxRows={AutoFitRowLimit} -> {width}x{height}px scale={Scale:0.##}");
    }

    /// <summary>
    /// Re-fits when the window moves to a monitor with another scale. XAML rescales its content by
    /// itself, but the physical window size does not follow unless it is set again.
    /// </summary>
    private void OnAutoFitScaleChanged(double scale)
    {
        // Sizes are in DIPs, so a plain resize (same scale) must not trigger another fit.
        if (scale <= 0 || Math.Abs(scale - _autoFitScale) < 0.001) return;

        _autoFitScale = scale;
        ApplyAutoFit();

        // Re-decode icons at the new monitor's physical size (the shared AppItem image was
        // decoded for the old scale). Touch every group, not only the visible one.
        var items = Box.Groups.SelectMany(g => g.Items).Distinct().ToList();
        if (items.Count > 0) _ = LoadIconsAsync(items, force: true);
    }

    /// <summary>Restores the user's preferred size when auto-fit is switched off.</summary>
    private void RestorePreferredSize()
    {
        // A collapsed docked panel is parked off-screen; the next expand applies the preferred height.
        if (IsDocked && _collapsed) return;

        var (width, height) = ExpandedSizePx();

        ResizePanel(width, height);

        if (_mode == PanelMode.Flyout) PositionNearTray();

        App.Log($"[fit] disabled, restored preferred size {width}x{height}px");
    }

    /// <summary>
    /// Applies a panel size while keeping the window inside the work area. Auto-fit can make the
    /// window noticeably wider or shorter than the position it was last dragged to.
    /// </summary>
    private void ResizePanel(int width, int height)
    {
        if (IsDocked)
        {
            PlaceDocked(width, height);
            return;
        }

        var area = WorkArea();
        var pos = _appWindow.Position;
        var x = Math.Clamp(pos.X, area.X, Math.Max(area.X, area.X + area.Width - width));
        var y = Math.Clamp(pos.Y, area.Y, Math.Max(area.Y, area.Y + area.Height - height));
        MoveTo(x, y, width, height);
    }
}
