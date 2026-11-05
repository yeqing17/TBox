using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TBox.Interop;
using TBox.Models;
using Windows.Graphics;
using static TBox.Interop.NativeMethods;

namespace TBox;

/// <summary>
/// Edge docking with a DeskBox-style capsule.
///
/// Docked + expanded: the panel sits flush against the edge, above normal windows.
/// Docked + collapsed: the panel is *hidden* and a small capsule window takes its place on the
/// edge. Nothing of the panel shows through, because it isn't rendered at all. Hovering the capsule
/// expands the panel; leaving the panel for 400ms brings the capsule back.
/// </summary>
public sealed partial class MainWindow
{
    // Distances below are DIPs (the design unit). Window geometry — DisplayArea.WorkArea,
    // GetWindowRect, GetCursorPos, AppWindow.MoveAndResize — is physical pixels, so these go through
    // Dip() before they meet a pixel.
    private const int SnapDistance = 16;
    private const int MinExpandedWidth = 360;
    private const int StripVisibleWidth = 16;
    private const int CapsuleGap = 8;

    private static readonly TimeSpan HoverExpandDelay = TimeSpan.FromMilliseconds(150);
    // The capsule holds clickable icons, so hovering it must not expand instantly.
    private static readonly TimeSpan CapsuleHoverDelay = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan LeaveCollapseDelay = TimeSpan.FromMilliseconds(400);

    private DateTime _hoverSince = DateTime.MinValue;
    private DateTime _leaveSince = DateTime.MinValue;
    private bool _collapsed;
    private bool _sliding;
    private bool _suppressCollapse;
    private CapsuleWindow? _capsule;

    private DockSide CurrentDock => Settings.Dock;
    private bool IsDocked => CurrentDock != DockSide.None;
    private bool UseCapsule => Settings.CollapsedStyle == CollapsedStyle.Capsule;

    /// <summary>DPI scale of the monitor the panel is on (1.0 = 100%, 2.0 = 200%…).</summary>
    private double Scale => Dpi.ScaleFor(_hwnd);

    /// <summary>A design DIP length as physical pixels on the panel's current monitor.</summary>
    private int Dip(double value) => Dpi.Px(value, Scale);

    private RectInt32 WorkArea()
        => DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest)?.WorkArea
           ?? new RectInt32(0, 0, 1920, 1080);

    /// <summary>X that leaves only a thin strip of the panel on screen (strip style).</summary>
    private int StripParkedX(int width)
    {
        var area = WorkArea();
        var strip = Dip(StripVisibleWidth);
        return CurrentDock switch
        {
            DockSide.Right => area.X + area.Width - strip,
            DockSide.Top => ExpandedX(width),
            _ => area.X - width + strip
        };
    }

    /// <summary>Y that leaves only a thin strip of the panel on screen (strip style).</summary>
    private int StripParkedY(int height)
    {
        var area = WorkArea();
        return CurrentDock == DockSide.Top
            ? area.Y - height + Dip(StripVisibleWidth)
            : ExpandedY();
    }

    private void ShowCollapsedBar(bool visible)
    {
        if (CollapsedBar is null) return;

        if (CurrentDock == DockSide.Top)
        {
            // The panel is parked above the screen, so its bottom 16px is the visible top bar.
            CollapsedBar.Width = double.NaN;
            CollapsedBar.Height = StripVisibleWidth;
            CollapsedBar.HorizontalAlignment = HorizontalAlignment.Stretch;
            CollapsedBar.VerticalAlignment = VerticalAlignment.Bottom;
            CollapsedBarIndicator.Width = 90;
            CollapsedBarIndicator.Height = 3;
        }
        else
        {
            CollapsedBar.Width = StripVisibleWidth;
            CollapsedBar.Height = double.NaN;
            CollapsedBar.VerticalAlignment = VerticalAlignment.Stretch;
            CollapsedBar.HorizontalAlignment = CurrentDock == DockSide.Left
                ? HorizontalAlignment.Right
                : HorizontalAlignment.Left;
            CollapsedBarIndicator.Width = 3;
            CollapsedBarIndicator.Height = 90;
        }

        CollapsedBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private int ParkedX(int width)
    {
        var area = WorkArea();
        return CurrentDock switch
        {
            DockSide.Right => area.X + area.Width + Dip(8),
            DockSide.Top => ExpandedX(width),
            _ => area.X - width - Dip(8)
        };
    }

    private int ParkedY(int height)
    {
        var area = WorkArea();
        return CurrentDock == DockSide.Top
            ? area.Y - height - Dip(8)
            : ExpandedY();
    }

    private int ExpandedX(int width)
    {
        var area = WorkArea();
        return CurrentDock switch
        {
            DockSide.Right => area.X + area.Width - width,
            DockSide.Top => Math.Clamp(
                (int)(Box.Window.X ?? area.X + (area.Width - width) / 2),
                area.X,
                Math.Max(area.X, area.X + area.Width - width)),
            _ => area.X
        };
    }

    private int ExpandedY()
    {
        var area = WorkArea();
        return CurrentDock == DockSide.Top
            ? area.Y
            : (int)(Box.Window.Y ?? area.Y + Dip(120));
    }

    private void MoveTo(int x, int y, int width, int height)
        => _appWindow.MoveAndResize(new RectInt32(x, y, width, height));

    /// <summary>Places the expanded panel against its docked edge, keeping the other axis.</summary>
    private void PlaceDocked(int width, int height)
    {
        var area = WorkArea();
        if (CurrentDock == DockSide.Top)
        {
            MoveTo(ExpandedX(width), area.Y, width, height);
            return;
        }

        var y = Math.Clamp(ExpandedY(), area.Y, Math.Max(area.Y, area.Y + area.Height - height));
        MoveTo(ExpandedX(width), y, width, height);
    }

    /// <summary>Which edge the panel would dock to right now (overshooting counts).</summary>
    private DockSide ComputeSnapSide(RECT rect)
    {
        var area = WorkArea();
        var rightEdge = area.X + area.Width;
        var snap = Dip(SnapDistance);

        var candidates = new List<(DockSide Side, double Distance)>();
        if (rect.Left <= area.X + snap) candidates.Add((DockSide.Left, Math.Abs(rect.Left - area.X)));
        if (rect.Right >= rightEdge - snap) candidates.Add((DockSide.Right, Math.Abs(rect.Right - rightEdge)));
        if (rect.Top <= area.Y + snap) candidates.Add((DockSide.Top, Math.Abs(rect.Top - area.Y)));

        if (candidates.Count == 0) return DockSide.None;

        // Nearest edge wins; ties prefer the top edge, then left over right.
        return candidates
            .OrderBy(c => c.Distance)
            .ThenBy(c => c.Side switch { DockSide.Top => 0, DockSide.Left => 1, _ => 2 })
            .First().Side;
    }

    private void UpdateDockHint()
    {
        if (DockHint is null) return;

        GetWindowRect(_hwnd, out var rect);
        var side = ComputeSnapSide(rect);

        if (side == DockSide.None)
        {
            DockHint.Visibility = Visibility.Collapsed;
            return;
        }

        if (side == DockSide.Top)
        {
            DockHint.Width = double.NaN;
            DockHint.Height = 3;
            DockHint.HorizontalAlignment = HorizontalAlignment.Stretch;
            DockHint.VerticalAlignment = VerticalAlignment.Top;
        }
        else
        {
            DockHint.Width = 3;
            DockHint.Height = double.NaN;
            DockHint.HorizontalAlignment = side == DockSide.Left
                ? HorizontalAlignment.Left
                : HorizontalAlignment.Right;
            DockHint.VerticalAlignment = VerticalAlignment.Stretch;
        }

        DockHint.Visibility = Visibility.Visible;
    }

    private void HideDockHint()
    {
        if (DockHint is not null) DockHint.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ drag end

    private void SnapOnDragEnd()
    {
        HideDockHint();

        GetWindowRect(_hwnd, out var rect);
        var area = WorkArea();
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;

        var side = ComputeSnapSide(rect);

        Settings.Dock = side;
        Box.Window.Y = side == DockSide.Top ? area.Y : rect.Top;
        // Auto-fit derives the size from the icon count on every update. Keep the user's preferred
        // width/height untouched so turning the feature off restores them. DockWidth is only the
        // auto-fit-off restore width for side docks, so it must not be overwritten with a fit size.
        if (!AutoFitEnabled)
        {
            Box.Window.Height = height;
            Box.Window.Width = width;
            if (side is DockSide.Left or DockSide.Right) Settings.DockWidth = width;
        }
        Box.Window.X = side switch
        {
            DockSide.Left => area.X,
            DockSide.Right => area.X + area.Width - width,
            DockSide.Top => Math.Clamp(rect.Left, area.X, Math.Max(area.X, area.X + area.Width - width)),
            _ => rect.Left
        };
        _store.MarkDirty();

        App.Log($"[dock] drag end side={side} rect={rect.Left},{rect.Top} {width}x{height}");

        if (side == DockSide.None)
        {
            StopDockPolling();
            ShowCollapsedVisual(false);
            return;
        }

        _collapsed = false;
        _mode = PanelMode.Desktop;
        SetDockedTopmost(true);
        PlaceDocked(width, height);
        StartDockPolling();
        _leaveSince = DateTime.UtcNow;
    }

    private void ApplyDockOnStartup(bool forceRight = false)
    {
        if (forceRight) Settings.Dock = DockSide.Right;
        if (!IsDocked) return;

        var area = WorkArea();
        var dockWidth = (int)Math.Max(Dip(MinExpandedWidth), Settings.DockWidth);
        Settings.DockWidth = dockWidth;

        var (width, height) = ExpandedSizePx();

        if (CurrentDock == DockSide.Top)
        {
            var x = Box.Window.X is double savedX
                ? (int)savedX
                : area.X + (area.Width - width) / 2;
            Box.Window.X = Math.Clamp(x, area.X, Math.Max(area.X, area.X + area.Width - width));
            Box.Window.Y = area.Y;
        }
        else
        {
            Box.Window.Y = ExpandedY();
        }

        SetDockedTopmost(true);
        PlaceDocked(width, height);

        StartDockPolling();
        if (Settings.Pinned)
        {
            // A pinned panel comes back expanded; the capsule is not created until it is unpinned.
            _collapsed = false;
            _capsule?.Hide();
            ShowCollapsedBar(false);
            Panel.Visibility = Visibility.Visible;
            PlaceDocked(width, height);
        }
        else
        {
            HideCollapsed(force: true, animate: false);
        }

        App.Log($"[dock] restored side={CurrentDock} width={width} x={Box.Window.X} y={Box.Window.Y} h={height} pinned={Settings.Pinned}");
    }

    private void SetDockedTopmost(bool docked)
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = docked;

        WindowChrome.SetKeepAtBottom(_hwnd, !docked);

        var insertAfter = new IntPtr(docked ? -1 /* HWND_TOPMOST */ : -2 /* HWND_NOTOPMOST */);
        SetWindowPos(_hwnd, insertAfter, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        if (!docked)
        {
            SetWindowPos(_hwnd, new IntPtr(1) /* HWND_BOTTOM */, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        App.Log($"[dock] topmost={docked}");
    }

    private void StartDockPolling()
    {
        _hoverSince = DateTime.MinValue;
        _leaveSince = DateTime.MinValue;
        _manager.DockPoll.Register(this);
    }

    private void StopDockPolling() => _manager.DockPoll.Unregister(this);

    // ------------------------------------------------------------------ state machine

    internal void PollDock()
    {
        if (!IsDocked || _dragging || _resizing) return;
        // Hidden-to-tray docked panels must stay hidden instead of being collapsed into a capsule.
        if (_mode == PanelMode.Hidden) return;

        GetCursorPos(out var cursor);

        if (_collapsed)
        {
            // Capsule: icons launch on click; hovering the expand tab (bottom for vertical,
            // right end for horizontal) expands. Strip: hovering the thin edge expands.
            if (UseCapsule)
            {
                if (_capsule is null || !_capsule.IsVisible)
                {
                    _hoverSince = DateTime.MinValue;
                    return;
                }

                var inTab = _capsule.HitTestExpandTab(cursor.X, cursor.Y);
                _capsule.SetExpandHover(inTab);

                if (!inTab)
                {
                    _hoverSince = DateTime.MinValue;
                    return;
                }

                if (_hoverSince == DateTime.MinValue) _hoverSince = DateTime.UtcNow;
                else if (DateTime.UtcNow - _hoverSince >= HoverExpandDelay)
                {
                    _hoverSince = DateTime.MinValue;
                    ShowExpanded();
                }

                return;
            }

            GetWindowRect(_hwnd, out var parked);
            var strip = Dip(StripVisibleWidth);
            bool inStrip;
            if (CurrentDock == DockSide.Top)
            {
                // The visible sliver is the bottom of the parked panel, at the top screen edge.
                inStrip = cursor.X >= parked.Left - 2 && cursor.X <= parked.Right + 2
                          && cursor.Y >= parked.Bottom - strip - 2 && cursor.Y <= parked.Bottom + 2;
            }
            else
            {
                var stripLeft = CurrentDock == DockSide.Right ? parked.Left : parked.Right - strip;
                var stripRight = CurrentDock == DockSide.Right ? parked.Left + strip : parked.Right;
                inStrip = cursor.X >= stripLeft - 2 && cursor.X <= stripRight + 2
                          && cursor.Y >= parked.Top - 2 && cursor.Y <= parked.Bottom + 2;
            }

            if (!inStrip)
            {
                _hoverSince = DateTime.MinValue;
                return;
            }

            if (_hoverSince == DateTime.MinValue) _hoverSince = DateTime.UtcNow;
            else if (DateTime.UtcNow - _hoverSince >= HoverExpandDelay)
            {
                _hoverSince = DateTime.MinValue;
                ShowExpanded();
            }

            return;
        }

        GetWindowRect(_hwnd, out var rect);
        var insidePanel = cursor.X >= rect.Left - 2 && cursor.X <= rect.Right + 2
                          && cursor.Y >= rect.Top - 2 && cursor.Y <= rect.Bottom + 2;

        if (HoldsOpen())
        {
            _leaveSince = DateTime.MinValue;
            return;
        }

        // A file dragged in from Explorer leaves the panel with the left button held. Keep the
        // panel expanded until the drop/button release; otherwise the 400ms leave timer collapses
        // it before the cursor reaches the drop target.
        if (insidePanel || NativeMethods.IsLeftButtonDown())
        {
            _leaveSince = DateTime.MinValue;
            return;
        }

        if (_leaveSince == DateTime.MinValue) _leaveSince = DateTime.UtcNow;
        else if (DateTime.UtcNow - _leaveSince >= LeaveCollapseDelay)
        {
            _leaveSince = DateTime.MinValue;
            HideCollapsed();
        }
    }

    private bool HoldsOpen()
    {
        if (Settings.Pinned || _suppressCollapse || _dragging || _resizing) return true;
        return Content.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is TextBox;
    }

    private CapsuleWindow EnsureCapsule()
    {
        if (_capsule is null)
        {
            _capsule = new CapsuleWindow
            {
                ExpandRequested = ShowExpanded,
                ItemActivated = item => Launch(item, asAdmin: false)
            };
            App.Log("[dock] capsule window created");
        }

        return _capsule;
    }

    /// <summary>Auto uses a vertical capsule at the sides and a horizontal one at the top.</summary>
    private CapsuleOrientation ResolveCapsuleOrientation()
    {
        var setting = Settings.CapsuleOrientation;
        if (setting != CapsuleOrientation.Auto) return setting;
        return CurrentDock == DockSide.Top ? CapsuleOrientation.Horizontal : CapsuleOrientation.Vertical;
    }

    /// <summary>Rebuilds a visible capsule after the direction setting changes.</summary>
    private void ApplyCapsuleOrientation()
    {
        if (!IsDocked || !_collapsed || !UseCapsule) return;
        HideCollapsed(force: true, animate: false);
    }

    private void ShowExpanded()
    {
        if (!_collapsed) return;

        // Expanding from a capsule/strip is a user action even though the window Show/Activate
        // below is programmatic; make this the active box for the tray/hotkey.
        _manager.SetActive(this);
        _collapsed = false;
        // A docked panel is back in desktop mode even if it was previously a flyout/hidden.
        // Otherwise OnWindowActivated auto-hides it and PollDock skips its hover/collapse logic.
        _mode = PanelMode.Desktop;

        var (width, height) = ExpandedSizePx();
        var x = ExpandedX(width);
        var y = ExpandedY();

        if (UseCapsule) _capsule?.Hide();
        else ShowCollapsedBar(false);

        // The window never left the screen — it was parked off-edge — so bringing it back is a
        // move plus a content swap, not a fresh show that would flash.
        Panel.Visibility = Visibility.Visible;
        _appWindow.Show();
        EnsureChrome();
        Activate();
        SetDockedTopmost(true);

        var fromX = UseCapsule ? ParkedX(width) : StripParkedX(width);
        var fromY = CurrentDock == DockSide.Top
            ? (UseCapsule ? ParkedY(height) : StripParkedY(height))
            : y;
        _ = SlideToAsync(fromX, fromY, x, y, width, height);
        UpdateRaiseButton();
        App.Log($"[dock] expanded ({Settings.CollapsedStyle})");
    }

    private async void HideCollapsed(bool force = false, bool animate = true)
    {
        if ((_collapsed && !force) || (!force && HoldsOpen())) return;

        _collapsed = true;
        // The docked capsule must keep its hover/expand polling alive even if the panel was
        // previously a flyout or hidden-to-tray.
        _mode = PanelMode.Desktop;

        var area = WorkArea();
        var (width, height) = ExpandedSizePx();
        var x = ExpandedX(width);
        var y = ExpandedY();

        if (UseCapsule)
        {
            var capsule = EnsureCapsule();
            var orientation = ResolveCapsuleOrientation();
            var items = CapsuleSelection(_currentGroup);
            capsule.Update(items, orientation);

            // The capsule is a window sized in physical pixels; hand it the panel's DPI scale first.
            capsule.SetScale(Scale);

            var toX = ParkedX(width);
            var toY = CurrentDock == DockSide.Top ? ParkedY(height) : y;
            if (animate) await SlideToAsync(x, y, toX, toY, width, height);

            Panel.Visibility = Visibility.Collapsed;
            ShowCollapsedBar(false);
            MoveTo(toX, toY, width, height);

            var gap = Dip(CapsuleGap);
            int capsuleX, capsuleY;
            if (CurrentDock == DockSide.Top)
            {
                capsuleX = x + (width - capsule.PixelWidth) / 2;
                capsuleX = Math.Clamp(capsuleX, area.X,
                    Math.Max(area.X, area.X + area.Width - capsule.PixelWidth));
                capsuleY = area.Y + gap;
            }
            else
            {
                capsuleX = CurrentDock == DockSide.Right
                    ? area.X + area.Width - capsule.PixelWidth - gap
                    : area.X + gap;
                capsuleY = y + (height - capsule.PixelHeight) / 2;
                capsuleY = Math.Clamp(capsuleY, area.Y,
                    Math.Max(area.Y, area.Y + area.Height - capsule.PixelHeight));
            }

            capsule.ShowAt(capsuleX, capsuleY);
            App.Log($"[dock] collapsed capsule at {capsuleX},{capsuleY} size={capsule.PixelWidth}x{capsule.PixelHeight} orientation={orientation} scale={Scale:0.##}");
            return;
        }

        // Strip style: leave a thin slice of the panel on the edge and let it slide out on hover.
        _capsule?.Hide();
        var stripToX = StripParkedX(width);
        var stripToY = StripParkedY(height);
        if (animate) await SlideToAsync(x, y, stripToX, stripToY, width, height);

        Panel.Visibility = Visibility.Collapsed;
        ShowCollapsedBar(true);
        MoveTo(stripToX, stripToY, width, height);
        App.Log($"[dock] collapsed strip at {stripToX},{stripToY}");
    }

    /// <summary>Eases the window between two points; moving does not re-lay out XAML, so it stays smooth.</summary>
    private async Task SlideToAsync(int fromX, int fromY, int toX, int toY, int width, int height, bool hideContentAtEnd = false)
    {
        if (_sliding) return;
        _sliding = true;

        try
        {
            // One move per 60Hz frame (10 × 16ms = 160ms) instead of 18 × 8ms. The old cadence
            // issued more window moves than the compositor can present, which stutters on 4K.
            const int frames = 10;
            const int frameMs = 16;

            for (var i = 1; i <= frames; i++)
            {
                var t = (double)i / frames;
                var eased = 1 - Math.Pow(1 - t, 3);
                var x = (int)Math.Round(fromX + (toX - fromX) * eased);
                var y = (int)Math.Round(fromY + (toY - fromY) * eased);
                MoveTo(x, y, width, height);
                await Task.Delay(frameMs);
            }

            MoveTo(toX, toY, width, height);
        }
        finally
        {
            _sliding = false;
        }

        if (hideContentAtEnd) Panel.Visibility = Visibility.Collapsed;
    }

    private void ShowCollapsedVisual(bool collapsed)
    {
        if (collapsed)
        {
            HideCollapsed(force: true, animate: false);
        }
        else
        {
            _collapsed = false;
            _mode = PanelMode.Desktop;
            _capsule?.Hide();
            _appWindow.Show();
        }
    }

    // ------------------------------------------------------------------ flyout helpers

    internal void SetCollapseSuppressed(bool value) => _suppressCollapse = value;

    private void OnCollapsedBarPressed(object sender, PointerRoutedEventArgs e)
    {
        ShowExpanded();
        e.Handled = true;
    }
}
