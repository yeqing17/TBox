using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TBox.Interop;
using TBox.Models;
using TBox.Services;
using Windows.Graphics;
using Windows.System;
using Windows.UI;

namespace TBox;

/// <summary>
/// Cross-box command palette. Search all loaded boxes/groups, launch with Enter, dismiss with Esc.
/// It is a separate top-level window so it works regardless of which box is active or visible.
/// </summary>
internal sealed class GlobalSearchWindow
{
    private const int WidthDip = 680;
    private const int HeightDip = 500;
    private const int ResultLimit = 50;

    private readonly BoxManager _manager;
    private readonly Window _window;
    private readonly Window? _owner;
    private readonly TextBox _searchBox;
    private readonly ListView _list;
    private readonly TextBlock _status;
    private readonly IntPtr _hwnd;
    private readonly double _scale;
    private bool _hiding;

    public event EventHandler? Closed;

    private sealed record Hit(AppItem Item, BoxData Box, ItemGroup Group, int Score)
    {
        public string Source => $"{Box.Name} · {Group.Name}";
    }

    public GlobalSearchWindow(BoxManager manager, Window? owner)
    {
        _manager = manager;
        _owner = owner;
        _window = new Window { Title = "TBox 全局搜索" };

        _searchBox = new TextBox
        {
            PlaceholderText = "搜索所有盒子的工具…",
            FontSize = 16,
            Height = 44,
            Padding = new Thickness(12, 8, 12, 8),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _searchBox.TextChanged += (_, _) => RefreshResults();
        _searchBox.KeyDown += OnSearchKeyDown;

        _list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 10, 0, 0)
        };
        _list.ItemClick += OnResultClick;

        _status = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.55,
            Margin = new Thickness(2, 8, 2, 0),
            Text = "↑↓ 选择 · Enter 启动 · Esc 关闭"
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_searchBox, 0);
        Grid.SetRow(_list, 1);
        Grid.SetRow(_status, 2);
        grid.Children.Add(_searchBox);
        grid.Children.Add(_list);
        grid.Children.Add(_status);

        var root = new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14),
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

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        _scale = Dpi.ScaleFor(_hwnd);

        if (_window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        WindowChrome.RemoveNonClientFrame(_hwnd);
        DwmChrome.ApplyBorderless(_hwnd);

        _window.Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
                Hide();
        };
        _window.Closed += (_, _) =>
        {
            WindowChrome.Forget(_hwnd);
            Closed?.Invoke(this, EventArgs.Empty);
        };
        _window.AppWindow.Hide();
    }

    public void Show()
    {
        if (!_window.AppWindow.IsVisible)
        {
            Position();
            _searchBox.Text = string.Empty;
            RefreshResults();
            _window.AppWindow.Show();
        }

        _window.Activate();
        NativeMethods.SetWindowPos(_hwnd, new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        _searchBox.Focus(FocusState.Programmatic);
        _searchBox.SelectAll();
    }

    private void Hide()
    {
        if (_hiding || !_window.AppWindow.IsVisible) return;
        _hiding = true;
        try
        {
            _window.AppWindow.Hide();
        }
        finally
        {
            _hiding = false;
        }
    }

    private void Position()
    {
        var area = DisplayArea.GetFromWindowId(
            _owner?.AppWindow.Id ?? _window.AppWindow.Id,
            DisplayAreaFallback.Nearest)?.WorkArea;
        if (area is null) return;

        var width = Dpi.Px(WidthDip, _scale);
        var height = Dpi.Px(HeightDip, _scale);
        var x = area.Value.X + (area.Value.Width - width) / 2;
        var y = area.Value.Y + (area.Value.Height - height) / 2;
        _window.AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void RefreshResults()
    {
        var query = _searchBox.Text.Trim();
        var hits = new List<Hit>();

        foreach (var box in _manager.Boxes)
        {
            foreach (var group in box.Groups)
            {
                foreach (var item in group.Items)
                {
                    if (query.Length > 0 && !item.Matches(query)) continue;
                    hits.Add(new Hit(item, box, group, Score(item, query)));
                }
            }
        }

        var ordered = hits
            .OrderBy(hit => hit.Score)
            .ThenBy(hit => hit.Box.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(hit => hit.Group.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(hit => hit.Item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(ResultLimit)
            .ToList();

        _list.Items.Clear();
        foreach (var hit in ordered)
            _list.Items.Add(BuildRow(hit));

        _status.Text = ordered.Count == 0
            ? "没有匹配的工具"
            : $"{ordered.Count} 个结果 · ↑↓ 选择 · Enter 启动 · Esc 关闭";

        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    private static int Score(AppItem item, string query)
    {
        if (query.Length == 0) return 0;
        if (item.Name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase)) return 0;
        if (item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)) return 1;
        if (item.Keywords?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true) return 2;
        if (item.Description?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true) return 3;
        return 4;
    }

    private FrameworkElement BuildRow(Hit hit)
    {
        var iconHost = new Grid { Width = 34, Height = 34, VerticalAlignment = VerticalAlignment.Center };
        var placeholder = new FontIcon { Glyph = "\uE7C3", FontSize = 16, Opacity = 0.5 };
        var image = new Image
        {
            Width = 28,
            Height = 28,
            Stretch = Stretch.Uniform,
            Source = hit.Item.Icon
        };
        iconHost.Children.Add(placeholder);
        iconHost.Children.Add(image);
        if (hit.Item.Icon is not null) placeholder.Visibility = Visibility.Collapsed;

        var name = new TextBlock
        {
            Text = hit.Item.Name,
            FontSize = 13.5,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var source = new TextBlock
        {
            Text = hit.Source,
            FontSize = 11,
            Opacity = 0.55,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(name);
        text.Children.Add(source);

        var row = new Grid
        {
            Tag = hit,
            MinHeight = 48,
            Padding = new Thickness(6, 3, 6, 3),
            ColumnSpacing = 10
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(iconHost, 0);
        Grid.SetColumn(text, 1);
        row.Children.Add(iconHost);
        row.Children.Add(text);

        ToolTipService.SetToolTip(row, hit.Item.Description ?? hit.Item.DisplayTarget);

        if (hit.Item.Icon is null)
            _ = LoadIconAsync(hit.Item, image, placeholder);

        return row;
    }

    private async Task LoadIconAsync(AppItem item, Image image, FontIcon placeholder)
    {
        try
        {
            var path = await Task.Run(() => ShellIcons.EnsureIcon(item));
            var source = await IconLoader.LoadAsync(path, Math.Max(16, Dpi.Px(32, _scale)));
            if (source is null) return;

            image.Source = source;
            placeholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            App.Log("[search] icon failed: " + ex.Message);
        }
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case VirtualKey.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                LaunchSelected();
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                Hide();
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (_list.Items.Count == 0) return;
        var next = Math.Clamp(_list.SelectedIndex + delta, 0, _list.Items.Count - 1);
        _list.SelectedIndex = next;
        _list.ScrollIntoView(_list.Items[next]);
    }

    private void OnResultClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FrameworkElement row && row.Tag is Hit hit)
            Launch(hit);
    }

    private void LaunchSelected()
    {
        if (_list.SelectedItem is FrameworkElement row && row.Tag is Hit hit)
            Launch(hit);
    }

    private void Launch(Hit hit)
    {
        var result = TBox.Services.Launcher.Open(hit.Item);
        if (!result.Ok)
        {
            _status.Text = result.Error is { Length: > 0 } error ? error : "启动失败";
            return;
        }

        Hide();
    }
}
