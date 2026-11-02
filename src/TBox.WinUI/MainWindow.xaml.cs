using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using TBox.Interop;
using TBox.Models;
using TBox.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using static TBox.Interop.NativeMethods;

namespace TBox;

public sealed partial class MainWindow : Window
{
    private readonly DataStore _store = DataStore.Instance;
    private TBoxData Data => _store.Data;

    private ItemGroup? _currentGroup;
    private string _filter = string.Empty;
    private IntPtr _hwnd;
    private bool _initialized;

    // Header drag / corner resize
    private bool _dragging;
    private bool _resizing;
    private POINT _dragCursorStart;
    private RECT _dragWindowStart;

    private ShellHost? _shell;
    private AppWindow _appWindow = null!;
    private bool _suppressAutoHide;

    private enum PanelMode { Desktop, Flyout, Hidden }
    private PanelMode _mode = PanelMode.Desktop;

    public MainWindow()
    {
        InitializeComponent();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        // AppWindow becomes null once the window is re-parented into the desktop layer, so keep it.
        _appWindow = AppWindow;
        SystemBackdrop = new DesktopAcrylicBackdrop();
        ConfigureWindow();
        Activated += OnFirstActivated;
        Activated += OnWindowActivated;
        Closed += (_, _) =>
        {
            if (GetWindowRect(_hwnd, out var rect))
            {
                Data.Window.X = rect.Left;
                Data.Window.Y = rect.Top;
                Data.Window.Width = rect.Right - rect.Left;
                Data.Window.Height = rect.Bottom - rect.Top;
            }

            _store.SaveNow();
            _shell?.Dispose();
        };

        if (Content is FrameworkElement root) root.KeyDown += OnRootKeyDown;

        InitDocking();
    }

    /// <summary>
    /// Re-assert the borderless chrome. WinUI restores the default presenter (and its caption
    /// strip) when the window is hidden and shown again, so this runs on every show.
    /// </summary>
    private void EnsureChrome()
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = true;
        }

        DispatcherQueue.TryEnqueue(() => DispatcherQueue.TryEnqueue(ApplyShape));
    }

    /// <summary>
    /// Native chrome: client area fills the window and DWM provides the antialiased rounded
    /// corners. No window region is used — hard clipping is what produced the jagged corners.
    /// </summary>
    private bool _chromeApplied;

    private void ApplyShape()
    {
        try
        {
            WindowChrome.RemoveNonClientFrame(_hwnd, isPanel: true);

            // DWM attributes + SWP_FRAMECHANGED are expensive and cause visible flicker if they
            // run on every resize, so they are applied once.
            if (!_chromeApplied)
            {
                DwmChrome.ApplyBorderless(_hwnd);
                _chromeApplied = true;
            }
        }
        catch (Exception ex)
        {
            App.Log("[shape] failed: " + ex.Message);
        }
    }

    private void ConfigureWindow()
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = true;
        }

        var placement = DataStore.Instance.Data.Window;
        // Saved placement is physical pixels; the fallbacks are design DIPs and have to be scaled.
        var scale = Dpi.ScaleFor(_hwnd);
        var width = placement.Width > 320 ? (int)placement.Width : Dpi.Px(760, scale);
        var height = placement.Height > 240 ? (int)placement.Height : Dpi.Px(520, scale);
        var x = placement.X is double px ? (int)px : Dpi.Px(200, scale);
        var y = placement.Y is double py ? (int)py : Dpi.Px(160, scale);
        _appWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    // =====================================================================
    // Startup
    // =====================================================================

    private async void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;
        if (_initialized) return;
        _initialized = true;

        await Task.Delay(400);

        _store.Load();
        AppTheme.Resolve(Data.Settings.Theme);
        Root.RequestedTheme = AppTheme.Current;
        App.Log($"[win] store loaded, groups={Data.Groups.Count} theme={AppTheme.Current}");

        Tabs.ItemsSource = Data.Groups;
        _currentGroup = Data.Groups.FirstOrDefault(g => g.Id == Data.Settings.LastGroupId)
                        ?? Data.Groups.FirstOrDefault();
        Tabs.SelectedItem = _currentGroup;

        RefreshView();

        WindowChrome.KeepAtBottom = true;
        _mode = PanelMode.Desktop;
        SetWindowPos(_hwnd, new IntPtr(1) /* HWND_BOTTOM */, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        App.Log($"[win] hwnd=0x{_hwnd:X}");

        if (_store.LoadWarning is { Length: > 0 } warning)
            _ = ShowMessageAsync("数据读取失败", warning);

        InitShellHost();
        EnsureChrome();

        await Task.Delay(600);
        ApplyShape();

        var cmdArgs = Environment.GetCommandLineArgs();
        ApplyDockOnStartup(forceRight: cmdArgs.Any(a => a.Equals("--dock-right", StringComparison.OrdinalIgnoreCase)));

        if (cmdArgs.Any(a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase)))
        {
            await Task.Delay(800);
            OnSettingsClick(this, new RoutedEventArgs());
        }

        if (cmdArgs.Any(a => a.Equals("--flyout", StringComparison.OrdinalIgnoreCase)))
        {
            ShowFlyout();
        }


        Root.Loaded += (_, _) => ApplyShape();
    }

    private void InitShellHost()
    {
        try
        {
            _shell = new ShellHost();
            _shell.HotkeyPressed += ToggleFlyout;
            _shell.TrayLeftClick += ToggleFlyout;
            _shell.TrayRightClick += ShowTrayMenu;

            var hotkey = _shell.RegisterHotkey();
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "tbox.ico");
            var tray = _shell.AddTrayIcon(iconPath, "TBox 桌面工具箱");
            App.Log($"[shell] hotkey={hotkey} tray={tray}");
        }
        catch (Exception ex)
        {
            App.Log("[shell] init failed: " + ex);
        }
    }

    private void SetCollapsedStyle(CollapsedStyle style)
    {
        Data.Settings.CollapsedStyle = style;
        _store.MarkDirty();

        if (!IsDocked) return;

        _capsule?.Hide();
        ShowCollapsedBar(false);
        HideCollapsed(force: true);
        App.Log($"[dock] collapsed style -> {style}");
    }

    private void ShowTrayMenu()
    {
        if (_shell is null) return;

        var choice = _shell.ShowMenu(new[]
        {
            new ShellHost.MenuItemSpec("弹出面板", 1),
            new ShellHost.MenuItemSpec("放回桌面", 2),
            new ShellHost.MenuItemSpec("", 0, Separator: true),
            new ShellHost.MenuItemSpec("收起样式：胶囊（图标）", 5, Checked: Data.Settings.CollapsedStyle == CollapsedStyle.Capsule),
            new ShellHost.MenuItemSpec("收起样式：竖条（极简）", 6, Checked: Data.Settings.CollapsedStyle == CollapsedStyle.Strip),
            new ShellHost.MenuItemSpec("", 0, Separator: true),
            new ShellHost.MenuItemSpec("设置", 3),
            new ShellHost.MenuItemSpec("退出", 4)
        });

        switch (choice)
        {
            case 1:
                ShowFlyout();
                break;
            case 2:
                SinkToDesktop();
                break;
            case 5:
                SetCollapsedStyle(CollapsedStyle.Capsule);
                break;
            case 6:
                SetCollapsedStyle(CollapsedStyle.Strip);
                break;
            case 3:
                OnSettingsClick(this, new RoutedEventArgs());
                break;
            case 4:
                _store.SaveNow();
                _shell?.Dispose();
                Application.Current.Exit();
                break;
        }
    }

    // =====================================================================
    // Data binding
    // =====================================================================

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Tabs.SelectedItem is not ItemGroup group) return;
        _currentGroup = group;
        Data.Settings.LastGroupId = group.Id;
        _store.MarkDirty();
        RefreshView();
    }

    private void RefreshView()
    {
        var items = _currentGroup?.Items ?? new System.Collections.ObjectModel.ObservableCollection<AppItem>();

        if (_filter.Length == 0)
        {
            ItemsView.ItemsSource = items;
        }
        else
        {
            ItemsView.ItemsSource = items.Where(i => i.Matches(_filter)).ToList();
        }

        var visible = ItemsView.ItemsSource is System.Collections.IEnumerable list ? list.Cast<object>().Count() : 0;
        EmptyState.Visibility = visible == 0 ? Visibility.Visible : Visibility.Collapsed;

        var total = _currentGroup?.Items.Count ?? 0;
        var missing = _currentGroup?.Items.Count(i => i.IsMissing) ?? 0;
        StatusText.Text = missing > 0
            ? $"{total} 个工具 · {missing} 个路径已失效"
            : $"{total} 个工具";

        foreach (var group in Data.Groups) group.RefreshCount();
        _ = LoadIconsAsync(items.ToList());
    }

    private async Task LoadIconsAsync(IReadOnlyList<AppItem> items)
    {
        foreach (var item in items)
        {
            if (item.Icon is not null) continue;

            try
            {
                var path = await Task.Run(() => ShellIcons.EnsureIcon(item));
                item.IconPath = path;
                item.Icon = await IconLoader.LoadAsync(path);
                item.IsMissing = !item.TargetExists();
            }
            catch (Exception ex)
            {
                App.Log("[icon] " + ex.Message);
            }
        }
    }

    // =====================================================================
    // Items
    // =====================================================================

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AppItem item) Launch(item, asAdmin: false);
    }

    private void OnItemsReordered(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        // GridView reorders the bound ObservableCollection itself; just persist.
        _store.MarkDirty();
    }

    private void OnSearchToggleClick(object sender, RoutedEventArgs e) => SetSearchVisible(SearchBox.Visibility != Visibility.Visible);

    private void SetSearchVisible(bool show)
    {
        SearchBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SearchPlaceholder.Visibility = show && SearchBox.Text.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        SearchToggle.Foreground = (Brush)Application.Current.Resources[
            show ? "AccentFillColorDefaultBrush" : "PanelFgSecondary"];

        if (show)
        {
            SearchBox.Focus(FocusState.Programmatic);
            SearchBox.SelectAll();
        }
        else if (SearchBox.Text.Length > 0)
        {
            SearchBox.Text = string.Empty;
        }

        SearchPlaceholder.Visibility = SearchBox.Visibility == Visibility.Visible && SearchBox.Text.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _filter = SearchBox.Text.Trim();
        RefreshView();
    }

    private void Launch(AppItem item, bool asAdmin)
    {
        var result = asAdmin ? Launcher.OpenAsAdmin(item) : Launcher.Open(item);
        if (!result.Ok && result.Error is { Length: > 0 })
            _ = ShowMessageAsync("无法打开", result.Error);
    }

    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var dropped = await e.DataView.GetStorageItemsAsync();
        AddPaths(dropped.Select(i => i.Path));
    }

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "加入盒子";
            e.DragUIOverride.IsCaptionVisible = true;
        }
    }

    private async void OnAddClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.Desktop,
            ViewMode = PickerViewMode.List
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);

        _suppressAutoHide = true;
        var files = await picker.PickMultipleFilesAsync();
        _suppressAutoHide = false;
        if (files is { Count: > 0 }) AddPaths(files.Select(f => f.Path));
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        if (_currentGroup is null) return;

        var added = 0;
        foreach (var raw in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Shortcuts.IsSupported(raw)) continue;
                var full = Path.GetFullPath(raw);
                if (_currentGroup.Items.Any(i => string.Equals(i.Path, full, StringComparison.OrdinalIgnoreCase)))
                    continue;

                _currentGroup.Items.Add(Shortcuts.CreateItem(full));
                added++;
            }
            catch (Exception ex)
            {
                App.Log("[add] " + ex.Message);
            }
        }

        if (added > 0)
        {
            _store.MarkDirty();
            RefreshView();
        }
    }

    // =====================================================================
    // Context menu
    // =====================================================================

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement source) return;
        if (source.DataContext is not AppItem item) return;

        var menu = new MenuFlyout();

        var open = new MenuFlyoutItem { Text = "打开", Icon = new FontIcon { Glyph = "\uE768" } };
        open.Click += (_, _) => Launch(item, false);

        var admin = new MenuFlyoutItem { Text = "以管理员身份运行", Icon = new FontIcon { Glyph = "\uE7EF" } };
        admin.Click += (_, _) => Launch(item, true);

        var locate = new MenuFlyoutItem { Text = "打开文件所在位置", Icon = new FontIcon { Glyph = "\uE8DA" } };
        locate.Click += (_, _) => Launcher.OpenLocation(item);

        var rename = new MenuFlyoutItem { Text = "重命名", Icon = new FontIcon { Glyph = "\uE8AC" } };
        rename.Click += async (_, _) => await RenameItemAsync(item);

        var editInfo = new MenuFlyoutItem { Text = "编辑描述/关键字…", Icon = new FontIcon { Glyph = "\uE70F" } };
        editInfo.Click += async (_, _) => await EditInfoAsync(item);

        var editPath = new MenuFlyoutItem { Text = "编辑路径…", Icon = new FontIcon { Glyph = "\uE8B7" } };
        editPath.Click += async (_, _) => await EditPathAsync(item);

        var move = new MenuFlyoutItem { Text = "移动到分组", Icon = new FontIcon { Glyph = "\uE8A7" } };
        move.Click += async (_, _) => await MoveItemAsync(item);

        var remove = new MenuFlyoutItem { Text = "从盒子移除", Icon = new FontIcon { Glyph = "\uE74D" } };
        remove.Click += async (_, _) => await RemoveItemAsync(item);

        menu.Items.Add(open);
        menu.Items.Add(admin);
        menu.Items.Add(locate);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(rename);
        menu.Items.Add(editInfo);
        menu.Items.Add(editPath);
        menu.Items.Add(move);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(remove);

        SetCollapseSuppressed(true);
        menu.Closed += (_, _) => SetCollapseSuppressed(false);

        menu.ShowAt(source, new FlyoutShowOptions { Position = e.GetPosition(source) });
        e.Handled = true;
    }

    private async Task RenameItemAsync(AppItem item)
    {
        var box = new TextBox { Text = item.Name, SelectionStart = item.Name.Length };
        if (!await ShowDialogAsync("重命名", box, 180)) return;

        var name = box.Text.Trim();
        if (name.Length == 0) return;

        item.Name = name;
        _store.MarkDirty();
        RefreshView();
    }

    private async Task EditInfoAsync(AppItem item)
    {
        var name = new TextBox { Text = item.Name, PlaceholderText = "显示名称" };
        var description = new TextBox
        {
            Text = item.Description ?? string.Empty,
            PlaceholderText = "这个工具是干什么的？例如：串口调试、抓 Modbus 报文",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 84
        };
        var keywords = new TextBox
        {
            Text = item.Keywords ?? string.Empty,
            PlaceholderText = "空格或逗号分隔，例如：串口 调试 RS232 modbus"
        };

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = "名称", FontSize = 12, Opacity = 0.6 });
        panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = "描述", FontSize = 12, Opacity = 0.6, Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(description);
        panel.Children.Add(new TextBlock { Text = "关键字", FontSize = 12, Opacity = 0.6, Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(keywords);

        if (!await ShowDialogAsync("编辑工具信息", panel, 400, "保存", "取消")) return;

        var nextName = name.Text.Trim();
        if (nextName.Length > 0) item.Name = nextName;

        var nextDescription = description.Text.Trim();
        item.Description = nextDescription.Length == 0 ? null : nextDescription;

        var nextKeywords = keywords.Text.Trim();
        item.Keywords = nextKeywords.Length == 0 ? null : nextKeywords;

        _store.MarkDirty();
        RefreshView();
    }

    private async Task EditPathAsync(AppItem item)
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.List };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);

        _suppressAutoHide = true;
        var file = await picker.PickSingleFileAsync();
        _suppressAutoHide = false;
        if (file is null) return;

        var refreshed = Shortcuts.CreateItem(file.Path);
        item.Path = refreshed.Path;
        item.SourcePath = refreshed.SourcePath;
        item.ResolvedTarget = refreshed.ResolvedTarget;
        item.Arguments = refreshed.Arguments;
        item.WorkDir = refreshed.WorkDir;
        item.IsMissing = !item.TargetExists();

        ShellIcons.Invalidate(item.Id);
        item.Icon = null;
        _store.MarkDirty();
        RefreshView();
    }

    private async Task MoveItemAsync(AppItem item)
    {
        var targets = Data.Groups.Where(g => g != _currentGroup).ToList();
        if (targets.Count == 0)
        {
            await ShowMessageAsync("没有其它分组", "先新建一个分组再移动。");
            return;
        }

        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, Height = 220 };
        list.ItemsSource = targets.Select(g => g.Name).ToList();

        if (!await ShowDialogAsync("移动到分组", list, 350, "移动")) return;

        var index = list.SelectedIndex;
        if (index < 0) return;

        var sourceGroup = Data.Groups.FirstOrDefault(g => g.Items.Contains(item));
        if (sourceGroup is null) return;

        sourceGroup.Items.Remove(item);
        targets[index].Items.Add(item);
        _store.MarkDirty();
        RefreshView();
    }

    private async Task RemoveItemAsync(AppItem item)
    {
        var text = new TextBlock
        {
            Text = $"确定要从盒子中移除「{item.Name}」吗？\n只会移除记录，不会删除磁盘上的文件。",
            TextWrapping = TextWrapping.Wrap
        };

        if (!await ShowDialogAsync("移除工具", text, 210, "移除", "取消")) return;

        var group = Data.Groups.FirstOrDefault(g => g.Items.Contains(item));
        group?.Items.Remove(item);
        ShellIcons.Invalidate(item.Id);
        _store.MarkDirty();
        RefreshView();
    }

    private async void OnNewGroupClick(object sender, RoutedEventArgs e)
    {
        var name = await PromptAsync("新建分组", "分组名称", "新分组");
        if (name is null) return;

        if (Data.Groups.Any(g => string.Equals(g.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            await ShowMessageAsync("分组已存在", $"已经有一个叫「{name}」的分组了。");
            return;
        }

        var group = new ItemGroup { Name = name };
        Data.Groups.Add(group);
        Tabs.SelectedItem = group;
        _store.MarkDirty();
    }

    private async Task<string?> PromptAsync(string title, string placeholder, string initial = "")
    {
        var box = new TextBox { Text = initial, PlaceholderText = placeholder };
        if (!await ShowDialogAsync(title, box, 180)) return null;

        var text = box.Text.Trim();
        return text.Length == 0 ? null : text;
    }

    private async Task<bool> ShowDialogAsync(string title, UIElement content, double height,
        string primaryText = "确定", string? secondaryText = "取消")
    {
        SetCollapseSuppressed(true);
        try
        {
            var dialog = new DialogWindow(title, content, height, primaryText, secondaryText);
            return await dialog.ShowAsync(this);
        }
        finally
        {
            SetCollapseSuppressed(false);
        }
    }

    private Task<bool> ShowMessageAsync(string title, string message)
    {
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        return ShowDialogAsync(title, text, 190, "知道了", null);
    }

    private void OnRaiseClick(object sender, RoutedEventArgs e)
    {
        if (_mode == PanelMode.Desktop) ShowFlyout();
        else SinkToDesktop();
    }

    /// <summary>Hide/show like a system flyout: detached from the desktop, above all windows.</summary>
    public void ToggleFlyout()
    {
        if (_mode == PanelMode.Flyout && _appWindow.IsVisible)
        {
            _appWindow.Hide();
            _mode = PanelMode.Hidden;
            return;
        }

        ShowFlyout();
    }

    public void ShowFlyout()
    {
        if (IsDocked)
        {
            _appWindow.Show();
            if (_collapsed) ShowExpanded();
            Activate();
            _mode = PanelMode.Desktop;
            SearchBox.Focus(FocusState.Programmatic);
            App.Log("[win] docked flyout expanded");
            return;
        }

        WindowChrome.KeepAtBottom = false;
        if (_appWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = true;

        PositionNearTray();
        _appWindow.Show();
        EnsureChrome();
        Activate();
        _mode = PanelMode.Flyout;
        SearchBox.Focus(FocusState.Programmatic);
        App.Log("[win] flyout shown");
    }

    private void PositionNearTray()
    {
        var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest)?.WorkArea;
        if (area is null) return;

        // Client area now equals the window, so the window rect is what the user sees.
        var size = _appWindow.Size;
        var x = area.Value.X + area.Value.Width - size.Width - 12;
        var y = area.Value.Y + area.Value.Height - size.Height - 12;
        _appWindow.Move(new PointInt32(x, y));
    }

    public void SinkToDesktop()
    {
        // "Back to desktop" also releases the edge dock.
        if (IsDocked)
        {
            Data.Settings.Dock = DockSide.None;
            Data.Settings.DockWidth = Math.Max(Dpi.Px(360, Dpi.ScaleFor(_hwnd)), Data.Window.Width);
            _store.MarkDirty();
            StopDockPolling();
            if (_collapsed) ShowExpanded();
            SetDockedTopmost(false);
        }

        if (_appWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = false;

        if (IsDocked)
        {
            GetWindowRect(_hwnd, out var rect);
            PlaceAtEdge(rect.Right - rect.Left, rect.Top, rect.Bottom - rect.Top);
        }
        else
        {
            var scale = Dpi.ScaleFor(_hwnd);
            _appWindow.Move(new PointInt32(
                Data.Window.X is double x ? (int)x : Dpi.Px(200, scale),
                Data.Window.Y is double y ? (int)y : Dpi.Px(160, scale)));
        }

        WindowChrome.KeepAtBottom = true;
        _appWindow.Show();
        SetWindowPos(_hwnd, new IntPtr(1), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        EnsureChrome();
        _mode = PanelMode.Desktop;
        App.Log("[win] sunk to desktop (bottom-most)");
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated) EnsureChrome();

        if (_suppressAutoHide) return;
        if (args.WindowActivationState != WindowActivationState.Deactivated) return;
        if (_mode != PanelMode.Flyout) return;

        _appWindow.Hide();
        _mode = PanelMode.Hidden;
        App.Log("[win] flyout auto-hidden");
    }

    private void OnHideClick(object sender, RoutedEventArgs e)
    {
        _appWindow.Hide();
        _mode = PanelMode.Hidden;
    }

    // =====================================================================
    // Settings / import (minimal for now)
    // =====================================================================

    private void ApplyTheme()
    {
        AppTheme.Resolve(Data.Settings.Theme);
        Root.RequestedTheme = AppTheme.Current;
        _capsule?.ApplyTheme();
        _store.MarkDirty();
    }

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        TextBlock Header(string text) => new()
        {
            Text = text,
            FontSize = 12,
            Opacity = 0.6,
            Margin = new Thickness(0, 8, 0, 0)
        };

        var themeSystem = new RadioButton
        {
            Content = "跟随系统",
            GroupName = "theme",
            IsChecked = Data.Settings.Theme == ThemeMode.System
        };
        var themeDark = new RadioButton
        {
            Content = "深色",
            GroupName = "theme",
            IsChecked = Data.Settings.Theme == ThemeMode.Dark
        };
        var themeLight = new RadioButton
        {
            Content = "浅色",
            GroupName = "theme",
            IsChecked = Data.Settings.Theme == ThemeMode.Light
        };

        var capsule = new RadioButton
        {
            Content = "胶囊：显示 3 个常用工具的图标",
            GroupName = "collapsedStyle",
            IsChecked = Data.Settings.CollapsedStyle == CollapsedStyle.Capsule
        };
        var strip = new RadioButton
        {
            Content = "竖条：只留一条细边，悬停展开",
            GroupName = "collapsedStyle",
            IsChecked = Data.Settings.CollapsedStyle == CollapsedStyle.Strip
        };

        var startup = new ToggleSwitch
        {
            IsOn = StartupEntry.IsEnabled(),
            Header = "开机自动启动",
            OnContent = "开",
            OffContent = "关"
        };

        var pathText = new TextBlock
        {
            Text = DataStore.Instance.RootDir,
            FontSize = 12,
            Opacity = 0.7,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap
        };

        var openFolder = new Button { Content = "打开数据目录", Margin = new Thickness(0, 6, 0, 0) };
        openFolder.Click += (_, _) =>
            Launcher.OpenLocation(new AppItem { Path = DataStore.Instance.RootDir });

        var hotkey = new TextBlock
        {
            Text = _shell is { HotkeyRegistered: true }
                ? "Ctrl+Alt+T · 已注册"
                : "Ctrl+Alt+T · 被其它程序占用",
            FontSize = 12,
            Opacity = 0.7
        };

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Header("主题"));
        panel.Children.Add(themeSystem);
        panel.Children.Add(themeDark);
        panel.Children.Add(themeLight);
        panel.Children.Add(Header("收起形态"));
        panel.Children.Add(capsule);
        panel.Children.Add(strip);
        panel.Children.Add(new Border { Height = 10 });
        panel.Children.Add(startup);
        panel.Children.Add(Header("快捷键"));
        panel.Children.Add(hotkey);
        panel.Children.Add(Header("数据目录"));
        panel.Children.Add(pathText);
        panel.Children.Add(openFolder);
        panel.Children.Add(Header("版本"));
        panel.Children.Add(new TextBlock { Text = "TBox 2.0.0 · WinUI 3", FontSize = 12, Opacity = 0.7 });

        if (!await ShowDialogAsync("设置", panel, 520, "保存", "取消")) return;

        Data.Settings.Theme = themeDark.IsChecked == true ? ThemeMode.Dark
            : themeLight.IsChecked == true ? ThemeMode.Light
            : ThemeMode.System;
        ApplyTheme();

        var nextStyle = capsule.IsChecked == true ? CollapsedStyle.Capsule : CollapsedStyle.Strip;
        if (nextStyle != Data.Settings.CollapsedStyle) SetCollapsedStyle(nextStyle);

        Data.Settings.RunAtStartup = startup.IsOn;
        StartupEntry.Set(startup.IsOn);
        _store.MarkDirty();
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var existing = Data.Groups.SelectMany(g => g.Items)
            .Select(i => i.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = Shortcuts.DesktopShortcuts()
            .Where(p => !existing.Contains(p))
            .Take(60)
            .ToList();

        if (candidates.Count == 0)
        {
            await ShowMessageAsync("从桌面导入", "桌面上没有新的快捷方式。");
            return;
        }

        var panel = new StackPanel { Spacing = 4 };
        var boxes = new List<(string Path, CheckBox Box)>();
        foreach (var path in candidates)
        {
            var box = new CheckBox
            {
                Content = Path.GetFileNameWithoutExtension(path),
                IsChecked = true
            };
            boxes.Add((path, box));
            panel.Children.Add(box);
        }

        if (!await ShowDialogAsync("从桌面导入", panel, 500, "导入", "取消")) return;

        AddPaths(boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Path));
    }

    private void OnHeaderPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement element) return;
        if (IsInsideInteractiveChild(e.OriginalSource as DependencyObject)) return;

        _dragging = true;
        GetCursorPos(out _dragCursorStart);
        GetWindowRect(_hwnd, out _dragWindowStart);
        element.CapturePointer(e.Pointer);
        App.Log($"[drag] start {_dragWindowStart.Left},{_dragWindowStart.Top}");
        e.Handled = true;
    }

    private void OnHeaderPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        GetCursorPos(out var cursor);
        var dx = cursor.X - _dragCursorStart.X;
        var dy = cursor.Y - _dragCursorStart.Y;

        SetWindowPos(_hwnd, IntPtr.Zero,
            _dragWindowStart.Left + dx,
            _dragWindowStart.Top + dy,
            0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        UpdateDockHint();
        e.Handled = true;
    }

    private void OnHeaderPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;

        if (sender is UIElement element) element.ReleasePointerCapture(e.Pointer);
        SnapOnDragEnd();
        e.Handled = true;
    }

    /// <summary>True when the press landed on a button/textbox inside the header, so it isn't a drag.</summary>
    private static bool IsInsideInteractiveChild(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase or TextBox or TextBlock && source is not Grid) return true;
            if (source is Grid or StackPanel) return false;
            source = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    // ---- corner resize -------------------------------------------------

    private void OnGripPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement element) return;

        _resizing = true;
        GetCursorPos(out _dragCursorStart);
        GetWindowRect(_hwnd, out _dragWindowStart);
        element.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnGripPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizing) return;

        GetCursorPos(out var cursor);
        // Both the grip deltas and the minimum size are physical pixels here: the drag maths works on
        // the window rect, so the DIP minimum has to be converted first.
        var scale = Dpi.ScaleFor(_hwnd);
        var width = Math.Max(Dpi.Px(360, scale), _dragWindowStart.Right - _dragWindowStart.Left + (cursor.X - _dragCursorStart.X));
        var height = Math.Max(Dpi.Px(240, scale), _dragWindowStart.Bottom - _dragWindowStart.Top + (cursor.Y - _dragCursorStart.Y));

        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, width, height, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        e.Handled = true;
    }

    private void OnGripPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;

        if (sender is UIElement element) element.ReleasePointerCapture(e.Pointer);
        GetWindowRect(_hwnd, out var rect);
        Data.Window.Width = rect.Right - rect.Left;
        Data.Window.Height = rect.Bottom - rect.Top;
        _store.MarkDirty();
        ApplyShape();
        e.Handled = true;
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.F && IsControlDown())
        {
            SearchBox.Focus(FocusState.Programmatic);
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            if (SearchBox.Text.Length > 0) SearchBox.Text = string.Empty;
            else _appWindow.Hide();
            e.Handled = true;
        }
    }

    private static bool IsControlDown()
        => Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
}
