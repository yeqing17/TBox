using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TBox.Interop;
using Windows.Graphics;
using Windows.UI;

namespace TBox;

/// <summary>
/// A themed dialog window. ContentDialog is tied to the panel's XAML tree, so a 362px-wide docked
/// panel squeezes it and a collapsed panel hosts it off-screen. Dialogs therefore live in their own
/// window, shown above everything and sized independently of the panel.
/// </summary>
internal sealed class DialogWindow
{
    // WinUI closes a window whose managed Window object is collected. Keeping the dialog rooted
    // until it closes is what stops it from vanishing immediately after Show().
    private static readonly List<DialogWindow> Open = new();

    private readonly Window _window;
    private readonly TaskCompletionSource<bool> _completion = new();
    private readonly double _contentHeight;
    private bool _completed;
    private bool _dragging;
    private NativeMethods.POINT _dragCursorStart;
    private PointInt32 _dragWindowStart;

    public DialogWindow(string title, UIElement content, double height,
        string primaryText = "确定", string? secondaryText = "取消")
    {
        _window = new Window { Title = title };

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var close = new Button
        {
            Content = new FontIcon { Glyph = "\uE8BB", FontSize = 14 },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (Application.Current.Resources.TryGetValue("PanelIconButton", out var iconStyle))
            close.Style = (Style)iconStyle;
        ToolTipService.SetToolTip(close, "关闭");
        close.Click += (_, _) => Complete(false);

        // Dialogs are borderless top-level windows, so they need their own title-bar drag target.
        // A transparent background makes the whole header hit-testable, except the close button.
        var header = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            Padding = new Thickness(0, 0, 0, 6)
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(titleText, 0);
        Grid.SetColumn(close, 1);
        header.Children.Add(titleText);
        header.Children.Add(close);
        header.PointerPressed += OnHeaderPointerPressed;
        header.PointerMoved += OnHeaderPointerMoved;
        header.PointerReleased += OnHeaderPointerReleased;
        header.PointerCanceled += OnHeaderPointerReleased;

        var body = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            Margin = new Thickness(0, 14, 0, 16)
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };

        if (secondaryText is not null)
        {
            var secondary = new Button { Content = secondaryText, MinWidth = 86 };
            secondary.Click += (_, _) => Complete(false);
            buttons.Children.Add(secondary);
        }

        var primary = new Button { Content = primaryText, MinWidth = 86 };
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accentStyle))
            primary.Style = (Style)accentStyle;
        primary.Click += (_, _) => Complete(true);
        buttons.Children.Add(primary);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(header, 0);
        Grid.SetRow(body, 1);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(header);
        grid.Children.Add(body);
        grid.Children.Add(buttons);

        var root = new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(22, 20, 22, 18),
            Child = grid,
            RequestedTheme = AppTheme.Current,
            UseLayoutRounding = true
        };
        root.Background = new SolidColorBrush(AppTheme.IsDark
            ? Color.FromArgb(246, 28, 28, 28)
            : Color.FromArgb(248, 249, 249, 249));
        root.BorderBrush = new SolidColorBrush(AppTheme.IsDark
            ? Color.FromArgb(36, 255, 255, 255)
            : Color.FromArgb(28, 0, 0, 0));

        _window.Content = root;
        if (!AppEnv.Opaque) _window.SystemBackdrop = new DesktopAcrylicBackdrop();

        // The dialog is deliberately window-sized (not content-sized), so its physical size has to
        // follow the monitor DPI — the XAML inside is laid out in DIPs.
        _contentHeight = height;
        root.Loaded += (_, _) =>
        {
            if (root.XamlRoot is { } xamlRoot)
                xamlRoot.Changed += (sender, _) => ApplySize(sender.RasterizationScale);
        };

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        if (_window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
        }

        WindowChrome.RemoveNonClientFrame(hwnd);
        DwmChrome.ApplyBorderless(hwnd);

        ApplySize(Dpi.ScaleFor(hwnd));
        _window.Closed += (_, _) =>
        {
            App.Log($"[dialog] closed '{title}' completed={_completed}");
            Open.Remove(this);
            WindowChrome.Forget(hwnd);
            if (!_completed) _completion.TrySetResult(false);
        };

        Open.Add(this);
    }

    private void OnHeaderPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is not UIElement element) return;
        if (IsInteractive(e.OriginalSource as DependencyObject)) return;

        _dragging = true;
        NativeMethods.GetCursorPos(out _dragCursorStart);
        _dragWindowStart = _window.AppWindow.Position;
        element.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private static bool IsInteractive(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase) return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void OnHeaderPointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        NativeMethods.GetCursorPos(out var cursor);
        _window.AppWindow.Move(new PointInt32(
            _dragWindowStart.X + cursor.X - _dragCursorStart.X,
            _dragWindowStart.Y + cursor.Y - _dragCursorStart.Y));
        e.Handled = true;
    }

    private void OnHeaderPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        if (sender is UIElement element) element.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    /// <summary>Resizes the window for a DPI scale, keeping its current spot once it is on screen.</summary>
    private void ApplySize(double scale)
    {
        var visible = _window.AppWindow.IsVisible;
        var pos = _window.AppWindow.Position;
        _window.AppWindow.MoveAndResize(new RectInt32(
            visible ? pos.X : 0,
            visible ? pos.Y : 0,
            Dpi.Px(480, scale),
            Dpi.Px(_contentHeight, scale)));
    }

    public Task<bool> ShowAsync(Window? owner)
    {
        try
        {
            App.Log($"[dialog] showing '{_window.Title}' owner={owner?.Title ?? "(none)"}");
            CenterOn(owner);
            _window.AppWindow.Show();
            _window.Activate();

            NativeMethods.SetWindowPos(WinRT.Interop.WindowNative.GetWindowHandle(_window),
                new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

            var r = _window.AppWindow.Position;
            App.Log($"[dialog] shown '{_window.Title}' at {r.X},{r.Y} {_window.AppWindow.Size.Width}x{_window.AppWindow.Size.Height}");
        }
        catch (Exception ex)
        {
            App.Log("[dialog] show failed: " + ex);
            throw;
        }

        return _completion.Task;
    }

    private void CenterOn(Window? owner)
    {
        var area = owner is null
            ? new RectInt32(0, 0, 1920, 1080)
            : DisplayArea.GetFromWindowId(owner.AppWindow.Id, DisplayAreaFallback.Nearest)!.WorkArea;

        var size = _window.AppWindow.Size;
        var x = area.X + (area.Width - size.Width) / 2;
        var y = area.Y + (area.Height - size.Height) / 2;
        _window.AppWindow.Move(new PointInt32(x, y));
    }

    private void Complete(bool result)
    {
        if (_completed) return;
        _completed = true;
        App.Log($"[dialog] complete '{_window.Title}' result={result}");
        _completion.TrySetResult(result);
        _window.Close();
    }
}
