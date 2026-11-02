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
    private const int DockPollMs = 50;
    private const int MinExpandedWidth = 360;
    private const int StripVisibleWidth = 16;
    private const int CapsuleGap = 8;

    private static readonly TimeSpan HoverExpandDelay = TimeSpan.FromMilliseconds(150);
    // The capsule holds clickable icons, so hovering it must not expand instantly.
    private static readonly TimeSpan CapsuleHoverDelay = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan LeaveCollapseDelay = TimeSpan.FromMilliseconds(400);

    private DispatcherQueueTimer? _dockTimer;
    private DateTime _hoverSince = DateTime.MinValue;
    private DateTime _leaveSince = DateTime.MinValue;
    private bool _collapsed;
    private bool _sliding;
    private bool _suppressCollapse;
    private CapsuleWindow? _capsule;

    private DockSide CurrentDock => Data.Settings.Dock;
    private bool IsDocked => CurrentDock != DockSide.None;
    private bool UseCapsule => Data.Settings.CollapsedStyle == CollapsedStyle.Capsule;

    /// <summary>DPI scale of the monitor the panel is on (1.0 = 100%, 2.0 = 200%…).</summary>
    private double Scale => Dpi.ScaleFor(_hwnd);

    /// <summary>A design DIP length as physical pixels on the panel's current monitor.</summary>
    private int Dip(double value) => Dpi.Px(value, Scale);

    private void InitDocking()
    {
        _dockTimer = DispatcherQueue.CreateTimer();
        _dockTimer.Interval = TimeSpan.FromMilliseconds(DockPollMs);
        _dockTimer.IsRepeating = true;
        _dockTimer.Tick += (_, _) => OnDockTick();
    }

    private RectInt32 WorkArea()
        => DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest)?.WorkArea
           ?? new RectInt32(0, 0, 1920, 1080);

    /// <summary>X that leaves only a thin strip of the panel on screen (strip style).</summary>
    private int StripParkedX(int width)
    {
        var area = WorkArea();
        var strip = Dip(StripVisibleWidth);
        return CurrentDock == DockSide.Right
            ? area.X + area.Width - strip
            : area.X - width + strip;
    }

    private void ShowCollapsedBar(bool visible)
    {
        if (CollapsedBar is null) return;

        CollapsedBar.HorizontalAlignment = CurrentDock == DockSide.Left
            ? HorizontalAlignment.Right
            : HorizontalAlignment.Left;
        CollapsedBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private int ParkedX(int width)
    {
        var area = WorkArea();
        return CurrentDock == DockSide.Right
            ? area.X + area.Width + 8
            : area.X - width - 8;
    }

    private int ExpandedX(int width)
    {
        var area = WorkArea();
        return CurrentDock == DockSide.Right ? area.X + area.Width - width : area.X;
    }

    private void MoveTo(int x, int y, int width, int height)
        => _appWindow.MoveAndResize(new RectInt32(x, y, width, height));

    private void PlaceAtEdge(int width, int y, int height)
        => MoveTo(ExpandedX(width), y, width, height);

    /// <summary>Which edge the panel would dock to right now (overshooting counts).</summary>
    private DockSide ComputeSnapSide(RECT rect)
    {
        var area = WorkArea();
        var rightEdge = area.X + area.Width;
        var snap = Dip(SnapDistance);

        var nearLeft = rect.Left <= area.X + snap;
        var nearRight = rect.Right >= rightEdge - snap;

        if (nearLeft && nearRight)
            return Math.Abs(rect.Left - area.X) <= Math.Abs(rect.Right - rightEdge) ? DockSide.Left : DockSide.Right;

        if (nearLeft) return DockSide.Left;
        if (nearRight) return DockSide.Right;
        return DockSide.None;
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

        DockHint.HorizontalAlignment = side == DockSide.Left
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
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

        Data.Settings.Dock = side;
        Data.Window.Y = rect.Top;
        // Auto-fit derives the size from the icon count on every update. Keep the user's preferred
        // width/height untouched so turning the feature off restores them. DockWidth is only the
        // auto-fit-off restore width, so it must not be overwritten with a content-fit size.
        if (!AutoFitEnabled)
        {
            Data.Window.Height = height;
            Data.Window.Width = width;
            if (side != DockSide.None) Data.Settings.DockWidth = width;
        }
        Data.Window.X = side switch
        {
            DockSide.Left => area.X,
            DockSide.Right => area.X + area.Width - width,
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
        SetDockedTopmost(true);
        PlaceAtEdge(width, rect.Top, height);
        StartDockPolling();
        _leaveSince = DateTime.UtcNow;
    }

    private void ApplyDockOnStartup(bool forceRight = false)
    {
        if (forceRight) Data.Settings.Dock = DockSide.Right;
        if (!IsDocked) return;

        var dockWidth = (int)Math.Max(Dip(MinExpandedWidth), Data.Settings.DockWidth);
        Data.Settings.DockWidth = dockWidth;
        var (width, height) = ExpandedSizePx();
        var y = (int)(Data.Window.Y ?? Dip(120));

        SetDockedTopmost(true);
        PlaceAtEdge(width, y, height);

        StartDockPolling();
        HideCollapsed(force: true, animate: false);
        App.Log($"[dock] restored side={CurrentDock} width={width} y={y} h={height}");
    }

    private void SetDockedTopmost(bool docked)
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = docked;

        WindowChrome.KeepAtBottom = !docked;

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
        if (_dockTimer is null || _dockTimer.IsRunning) return;
        _hoverSince = DateTime.MinValue;
        _leaveSince = DateTime.MinValue;
        _dockTimer.Start();
    }

    private void StopDockPolling() => _dockTimer?.Stop();

    // ------------------------------------------------------------------ state machine

    private void OnDockTick()
    {
        if (!IsDocked || _dragging || _resizing) return;

        GetCursorPos(out var cursor);

        if (_collapsed)
        {
            // Capsule: icons launch on click; hovering the expand tab at the bottom expands.
            // Strip: hovering the thin edge expands.
            if (UseCapsule)
            {
                if (_capsule is null || !_capsule.IsVisible)
                {
                    _hoverSince = DateTime.MinValue;
                    return;
                }

                var cap = _capsule.GetRect();
                // GetRect/cursor are physical pixels, so the tab band must be physical too.
                var tabTop = cap.Y + cap.Height - _capsule.ExpandTabPixels;
                var inTab = cursor.X >= cap.X - 2 && cursor.X <= cap.X + cap.Width + 2
                            && cursor.Y >= tabTop && cursor.Y <= cap.Y + cap.Height + 2;

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
            var stripLeft = CurrentDock == DockSide.Right ? parked.Left : parked.Right - strip;
            var stripRight = CurrentDock == DockSide.Right ? parked.Left + strip : parked.Right;
            var inStrip = cursor.X >= stripLeft - 2 && cursor.X <= stripRight + 2
                          && cursor.Y >= parked.Top - 2 && cursor.Y <= parked.Bottom + 2;

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

        if (insidePanel)
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
        if (_suppressCollapse || _dragging || _resizing) return true;
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

    private void ShowExpanded()
    {
        if (!_collapsed) return;

        _collapsed = false;

        var (width, height) = ExpandedSizePx();
        int y = (int)(Data.Window.Y ?? Dip(120));

        if (UseCapsule) _capsule?.Hide();
        else ShowCollapsedBar(false);

        // The window never left the screen — it was parked off-edge — so bringing it back is a
        // move plus a content swap, not a fresh show that would flash.
        Panel.Visibility = Visibility.Visible;
        _appWindow.Show();
        EnsureChrome();
        Activate();
        SetDockedTopmost(true);

        var from = UseCapsule ? ParkedX(width) : StripParkedX(width);
        _ = SlideToAsync(from, ExpandedX(width), y, width, height);
        App.Log($"[dock] expanded ({Data.Settings.CollapsedStyle})");
    }

    private async void HideCollapsed(bool force = false, bool animate = true)
    {
        if ((_collapsed && !force) || (!force && HoldsOpen())) return;

        _collapsed = true;

        var area = WorkArea();
        var (width, height) = ExpandedSizePx();
        int y = (int)(Data.Window.Y ?? area.Y + Dip(120));

        if (UseCapsule)
        {
            var capsule = EnsureCapsule();
            capsule.Update((_currentGroup?.Items ?? new System.Collections.ObjectModel.ObservableCollection<AppItem>()).ToList());

            // The capsule is a window sized in physical pixels; hand it the panel's DPI scale first.
            capsule.SetScale(Scale);

            if (animate) await SlideToAsync(ExpandedX(width), ParkedX(width), y, width, height);

            Panel.Visibility = Visibility.Collapsed;
            ShowCollapsedBar(false);
            MoveTo(ParkedX(width), y, width, height);

            var gap = Dip(CapsuleGap);
            var capsuleX = CurrentDock == DockSide.Right
                ? area.X + area.Width - capsule.PixelWidth - gap
                : area.X + gap;
            var capsuleY = y + (height - capsule.PixelHeight) / 2;
            capsuleY = Math.Clamp(capsuleY, area.Y,
                Math.Max(area.Y, area.Y + area.Height - capsule.PixelHeight));

            capsule.ShowAt(capsuleX, capsuleY);
            App.Log($"[dock] collapsed capsule at {capsuleX},{capsuleY} size={capsule.PixelWidth}x{capsule.PixelHeight} scale={Scale:0.##}");
            return;
        }

        // Strip style: leave a thin slice of the panel on the edge and let it slide out on hover.
        _capsule?.Hide();
        if (animate) await SlideToAsync(ExpandedX(width), StripParkedX(width), y, width, height);

        Panel.Visibility = Visibility.Collapsed;
        ShowCollapsedBar(true);
        MoveTo(StripParkedX(width), y, width, height);
        App.Log($"[dock] collapsed strip at {StripParkedX(width)},{y}");
    }

    /// <summary>Eases the window horizontally; moving does not re-lay out XAML, so it stays smooth.</summary>
    private async Task SlideToAsync(int from, int to, int y, int width, int height, bool hideContentAtEnd = false)
    {
        if (_sliding) return;
        _sliding = true;

        try
        {
            const int frames = 18;
            const int frameMs = 8;

            for (var i = 1; i <= frames; i++)
            {
                var t = (double)i / frames;
                var eased = 1 - Math.Pow(1 - t, 3);
                MoveTo((int)Math.Round(from + (to - from) * eased), y, width, height);
                await Task.Delay(frameMs);
            }

            MoveTo(to, y, width, height);
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
            _capsule?.Hide();
            _appWindow.Show();
        }
    }

    // ------------------------------------------------------------------ flyout helpers

    private void SetCollapseSuppressed(bool value) => _suppressCollapse = value;

    private void OnCollapsedBarPressed(object sender, PointerRoutedEventArgs e)
    {
        ShowExpanded();
        e.Handled = true;
    }
}
