using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TBox.Interop;
using TBox.Models;
using Windows.Graphics;
using Windows.UI;

namespace TBox;

/// <summary>
/// The collapsed presentation of a docked panel: a small rounded capsule that shows the first few
/// tools of the current group as quick-launch icons. Clicking an icon launches it; clicking the
/// capsule background expands the full panel.
/// </summary>
internal sealed class CapsuleWindow
{
    // Every length below is a XAML DIP. The window itself is sized in physical pixels, so they are
    // multiplied by the monitor's scale factor (SetScale) before they reach AppWindow.
    public const int Width = 56;
    private const int IconSize = 34;
    private const int IconGap = 6;
    private const int PaddingY = 10;
    private const int MaxItems = 3;
    public const int ExpandTabHeight = 38;
    private const int ExpandStripHeight = 32;

    private readonly Window _window;
    private readonly StackPanel _stack;
    private readonly Border _mark;
    private readonly Border _expandStrip;
    private bool _hovered;
    private readonly Border? _root;
    private double _scale = 1.0;


    public IntPtr Handle { get; }
    public Action? ExpandRequested { get; set; }
    public Action<AppItem>? ItemActivated { get; set; }

    /// <summary>Height the content needs, in DIPs.</summary>
    public int ContentHeight { get; private set; } = 120;

    /// <summary>Window width in physical pixels at the current DPI scale.</summary>
    public int PixelWidth => Dpi.Px(Width, _scale);

    /// <summary>Window height in physical pixels at the current DPI scale.</summary>
    public int PixelHeight => Dpi.Px(ContentHeight, _scale);

    /// <summary>
    /// Height of the bottom expand tab in physical pixels. Hit testing compares it against
    /// GetWindowRect / GetCursorPos, which are physical, so it must not stay in DIPs.
    /// </summary>
    public int ExpandTabPixels => Dpi.Px(ExpandTabHeight, _scale);

    /// <summary>
    /// Set the monitor's DPI scale before reading <see cref="PixelWidth"/>/<see cref="PixelHeight"/>.
    /// If the capsule is already on screen the window is resized in place, so a live display-scale
    /// change does not leave the content clipped until the next collapse.
    /// </summary>
    public void SetScale(double scale)
    {
        if (scale <= 0) scale = 1.0;
        if (Math.Abs(scale - _scale) < 0.001) return;

        _scale = scale;
        if (!IsVisible) return;

        var pos = _window.AppWindow.Position;
        _window.AppWindow.MoveAndResize(new RectInt32(pos.X, pos.Y, PixelWidth, PixelHeight));
    }

    public CapsuleWindow()
    {
        _window = new Window { Title = "TBox" };

        _stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = IconGap,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        _mark = new Border
        {
            Width = IconSize,
            Height = IconSize,
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromArgb(255, 0, 120, 212)),
            Child = new FontIcon
            {
                Glyph = "\uE7B8",
                FontSize = 16,
                Foreground = new SolidColorBrush(Colors.White)
            }
        };

        _expandStrip = new Border
        {
            Height = ExpandStripHeight,
            Background = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(5, 6, 5, 0),
            Child = new FontIcon
            {
                Glyph = "",
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255))
            }
        };
        ToolTipService.SetToolTip(_expandStrip, "展开面板");

        _expandStrip.PointerPressed += (_, e) =>
        {
            ExpandRequested?.Invoke();
            e.Handled = true;
        };
        // The hover highlight is driven by the main window's timer (cursor position polling), not
        // by PointerEntered/Exited: those can miss the exit and leave the tab stuck highlighted.

        var separator = new Border
        {
            Height = 1,
            Margin = new Thickness(10, 6, 10, 0),
            Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255))
        };

        var content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 0,
            Children = { _stack, separator, _expandStrip }
        };

        var root = new Border
        {
            CornerRadius = new CornerRadius(28),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0, PaddingY, 0, 6),
            Child = content,
            RequestedTheme = AppTheme.Current
        };
        root.Background = new SolidColorBrush(AppTheme.IsDark
            ? Color.FromArgb(235, 30, 30, 30)
            : Color.FromArgb(242, 250, 250, 250));
        root.BorderBrush = new SolidColorBrush(AppTheme.IsDark
            ? Color.FromArgb(36, 255, 255, 255)
            : Color.FromArgb(28, 0, 0, 0));
        _root = root;

        _window.Content = root;
        if (!AppEnv.Opaque) _window.SystemBackdrop = new DesktopAcrylicBackdrop();

        // Follow live DPI changes (display-scale slider, moving to a monitor with another scale):
        // the XAML content rescales by itself, but the window's pixel size does not.
        root.Loaded += (_, _) =>
        {
            if (root.XamlRoot is { } xamlRoot)
                xamlRoot.Changed += (sender, _) => SetScale(sender.RasterizationScale);
        };

        Handle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        _scale = Dpi.ScaleFor(Handle);

        if (_window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
        }

        WindowChrome.RemoveNonClientFrame(Handle);
        DwmChrome.ApplyBorderless(Handle);
        _hovered = true;
        SetExpandHover(false);
        _window.AppWindow.Hide();
    }

    /// <summary>Rebuilds the quick-launch icons; falls back to the app mark when the box is empty.</summary>
    public void Update(IReadOnlyList<AppItem> items)
    {
        _stack.Children.Clear();

        var shown = items.Take(MaxItems).ToList();
        if (shown.Count == 0)
        {
            _stack.Children.Add(_mark);
            ContentHeight = IconSize + PaddingY * 2 + 32 + ExpandStripHeight;
            return;
        }

        foreach (var item in shown)
        {
            var tile = new Border
            {
                Width = IconSize + 4,
                Height = IconSize + 4,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                Child = item.Icon is not null
                    ? new Image { Source = item.Icon, Width = IconSize, Height = IconSize, Stretch = Stretch.Uniform }
                    : new FontIcon { Glyph = "\uE7C3", FontSize = 16, Opacity = 0.6 }
            };

            ToolTipService.SetToolTip(tile, item.Name);

            tile.PointerPressed += (_, e) =>
            {
                ItemActivated?.Invoke(item);
                e.Handled = true;
            };

            // Hover feedback so the capsule feels alive.
            tile.PointerEntered += (_, _) => tile.Background = new SolidColorBrush(
                AppTheme.IsDark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(24, 0, 0, 0));
            tile.PointerExited += (_, _) => tile.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

            _stack.Children.Add(tile);
        }

        ContentHeight = PaddingY * 2 + shown.Count * (IconSize + 4) + (shown.Count - 1) * IconGap + ExpandStripHeight + 14;
    }

    public void ShowAt(int x, int y)
    {
        // PixelWidth/PixelHeight, not Width/ContentHeight: the window is in physical pixels while the
        // content inside it is laid out in DIPs.
        _window.AppWindow.MoveAndResize(new RectInt32(x, y, PixelWidth, PixelHeight));
        _window.AppWindow.Show();

        NativeMethods.SetWindowPos(Handle, new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>Sets the expand tab's hover look. Called from the cursor poll, so it cannot stick.</summary>
    public void SetExpandHover(bool hovered)
    {
        if (_hovered == hovered) return;
        _hovered = hovered;

        var idleBg = AppTheme.IsDark ? Color.FromArgb(38, 255, 255, 255) : Color.FromArgb(26, 0, 0, 0);
        var idleFg = AppTheme.IsDark ? Color.FromArgb(235, 255, 255, 255) : Color.FromArgb(230, 0, 0, 0);

        _expandStrip.Background = new SolidColorBrush(hovered
            ? Color.FromArgb(90, 0, 120, 212)
            : idleBg);
        _expandStrip.Child = new FontIcon
        {
            Glyph = "",
            FontSize = 14,
            Foreground = new SolidColorBrush(hovered ? Colors.White : idleFg)
        };
    }

    /// <summary>Re-applies the palette after the user switches theme.</summary>
    public void ApplyTheme()
    {
        if (_window.Content is FrameworkElement root) root.RequestedTheme = AppTheme.Current;

        if (_root is not null)
        {
            _root.Background = new SolidColorBrush(AppTheme.IsDark
                ? Color.FromArgb(235, 30, 30, 30)
                : Color.FromArgb(242, 250, 250, 250));
            _root.BorderBrush = new SolidColorBrush(AppTheme.IsDark
                ? Color.FromArgb(36, 255, 255, 255)
                : Color.FromArgb(28, 0, 0, 0));
        }

        _hovered = true;
        SetExpandHover(false);
    }

    public void Hide()
    {
        SetExpandHover(false);
        _window.AppWindow.Hide();
    }

    public bool IsVisible => _window.AppWindow.IsVisible;

    public RectInt32 GetRect()
    {
        NativeMethods.GetWindowRect(Handle, out var rect);
        return new RectInt32(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }
}
