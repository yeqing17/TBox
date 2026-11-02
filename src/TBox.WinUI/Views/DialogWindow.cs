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

    public DialogWindow(string title, UIElement content, double height,
        string primaryText = "确定", string? secondaryText = "取消")
    {
        _window = new Window { Title = title };

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };

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

        Grid.SetRow(titleText, 0);
        Grid.SetRow(body, 1);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(titleText);
        grid.Children.Add(body);
        grid.Children.Add(buttons);

        var root = new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(22, 20, 22, 18),
            Child = grid,
            RequestedTheme = AppTheme.Current
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
            Open.Remove(this);
            if (!_completed) _completion.TrySetResult(false);
        };

        Open.Add(this);
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
        CenterOn(owner);
        _window.AppWindow.Show();
        _window.Activate();

        NativeMethods.SetWindowPos(WinRT.Interop.WindowNative.GetWindowHandle(_window),
            new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

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
        _completion.TrySetResult(result);
        _window.Close();
    }
}
