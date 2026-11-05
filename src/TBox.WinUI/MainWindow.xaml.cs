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
    private readonly BoxManager _manager;
    private readonly BoxData _box;
    private TBoxData Data => _store.Data;

    internal BoxData Box => _box;
    private BoxSettings Settings => _box.Settings;

    private ItemGroup? _currentGroup;
    private string _filter = string.Empty;
    private IntPtr _hwnd;
    private bool _initialized;

    // Header drag / corner resize
    private bool _dragging;
    private bool _resizing;
    private POINT _dragCursorStart;
    private RECT _dragWindowStart;

    private AppWindow _appWindow = null!;
    private bool _suppressAutoHide;

    private enum PanelMode { Desktop, Flyout, Hidden }
    private PanelMode _mode = PanelMode.Desktop;

    internal MainWindow(BoxManager manager, BoxData box)
    {
        _manager = manager;
        _box = box;

        InitializeComponent();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        // AppWindow becomes null once the window is re-parented into the desktop layer, so keep it.
        _appWindow = AppWindow;
        if (!AppEnv.Opaque) SystemBackdrop = new DesktopAcrylicBackdrop();

        AppLogo.Load();
        HeaderLogo.Source = AppLogo.Image;

        // The manager loaded the store and resolved the theme before creating any window, so the
        // first frame already has the right data, size and palette.
        Root.RequestedTheme = AppTheme.Current;
        App.Log($"[win] box={Box.Name} groups={Box.Groups.Count} theme={AppTheme.Current}");

        ConfigureWindow();
        EnsureChrome();
        ApplyShape();

        Tabs.ItemsSource = Box.Groups;
        _currentGroup = Box.Groups.FirstOrDefault(g => g.Id == Settings.LastGroupId)
                        ?? Box.Groups.FirstOrDefault();
        Tabs.SelectedItem = _currentGroup;
        RefreshView();

        Activated += OnFirstActivated;
        Activated += OnWindowActivated;
        Closed += (_, _) =>
        {
            StopDockPolling();

            if (GetWindowRect(_hwnd, out var rect))
            {
                Box.Window.X = rect.Left;
                Box.Window.Y = rect.Top;
                // Auto-fit sizes are derived from the group; keep the preferred size the user set
                // so the feature can be switched off without the window staying shrunk.
                if (!AutoFitEnabled)
                {
                    Box.Window.Width = rect.Right - rect.Left;
                    Box.Window.Height = rect.Bottom - rect.Top;
                }
            }

            _capsule?.Close();
            _capsule = null;
            _store.SaveNow();
            WindowChrome.Forget(_hwnd);
            _manager.OnWindowClosed(this);
        };

        if (Content is FrameworkElement root)
        {
            root.KeyDown += OnRootKeyDown;
            root.Loaded += (_, _) => ApplyShape();
        }

        BoxName.Text = Box.Name;
        Title = $"TBox · {Box.Name}";
        UpdatePinButton();
        UpdateRaiseButton();

        ApplyDockOnStartup();
        if (!IsDocked)
        {
            _mode = PanelMode.Desktop;
            WindowChrome.SetKeepAtBottom(_hwnd, true);
            SetWindowPos(_hwnd, new IntPtr(1) /* HWND_BOTTOM */, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        _autoFitReady = true;
        _autoFitScale = Scale;
        ApplyAutoFit();

        App.Log($"[win] box={Box.Name} hwnd=0x{_hwnd:X}");
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

        var placement = Box.Window;
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

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;
        if (_initialized) return;
        _initialized = true;

        // Data, theme, tabs and placement were prepared before the first frame; activation only
        // re-asserts the chrome and starts following live DPI changes.
        App.Log($"[win] activated box={Box.Name}");

        EnsureChrome();
        ApplyShape();

        if (Root.XamlRoot is { } autoFitRoot)
            autoFitRoot.Changed += (_, _) => OnAutoFitScaleChanged(autoFitRoot.RasterizationScale);
    }

    internal void SetCollapsedStyle(CollapsedStyle style)
    {
        Settings.CollapsedStyle = style;
        _store.MarkDirty();

        if (!IsDocked) return;

        // Pinning is explicit: changing the future collapse style must not close the panel now.
        if (Settings.Pinned)
        {
            App.Log($"[box] {Box.Name} collapsed style -> {style} (pinned)");
            return;
        }

        _capsule?.Hide();
        ShowCollapsedBar(false);
        HideCollapsed(force: true);
        App.Log($"[box] {Box.Name} collapsed style -> {style}");
    }

    // =====================================================================
    // Data binding
    // =====================================================================

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Tabs.SelectedItem is not ItemGroup group) return;
        _currentGroup = group;
        Settings.LastGroupId = group.Id;
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

        UpdateCapsulePins();
        foreach (var group in Box.Groups) group.RefreshCount();

        // Not while filtering: resizing on every keystroke would make the search box jump around.
        if (_autoFitReady && _filter.Length == 0) ApplyAutoFit();

        _ = LoadIconsAsync(items.ToList());
    }

    private async Task LoadIconsAsync(IReadOnlyList<AppItem> items, bool force = false)
    {
        // The card's icon is 44 DIP; decoding at 44 × monitor scale means a 4K/200% card gets
        // exactly 88 physical pixels instead of a 256→88 compositor downscale.
        var targetPixels = Math.Max(16, Dpi.Px(44, Scale));

        foreach (var item in items)
        {
            // Windows on different monitors share AppItem.Icon. Keep the largest decode; a
            // lower-DPI window can downscale it, but never replace a 2x image with a 1x one.
            if (item.Icon is not null && (!force || targetPixels <= item.IconDecodePixels)) continue;

            try
            {
                var path = await Task.Run(() => ShellIcons.EnsureIcon(item));
                item.IconPath = path;
                item.Icon = await IconLoader.LoadAsync(path, targetPixels);
                if (item.Icon is not null) item.IconDecodePixels = targetPixels;
                item.IsMissing = !item.TargetExists();
            }
            catch (Exception ex)
            {
                App.Log("[icon] " + ex.Message);
            }
        }

        // A capsule created during dock startup may have been filled before the icons finished
        // loading; refresh it in place so it does not keep showing placeholders. Always use the
        // current group (not a filtered/reloaded list passed in by the caller).
        if (_capsule is { IsVisible: true })
            _capsule.Update(CapsuleSelection(_currentGroup), ResolveCapsuleOrientation());
    }

    /// <summary>Pinned tools in group order, capped at three; otherwise the first three.</summary>
    private static List<AppItem> CapsuleSelection(ItemGroup? group)
    {
        if (group is null) return new();

        var ids = (group.CapsuleItemIds ?? new List<string>()).ToHashSet(StringComparer.Ordinal);
        var pinned = group.Items.Where(item => ids.Contains(item.Id)).Take(3).ToList();
        return pinned.Count > 0 ? pinned : group.Items.Take(3).ToList();
    }

    private void UpdateCapsulePins()
    {
        foreach (var group in Box.Groups)
        {
            var pinned = (group.CapsuleItemIds ?? new List<string>()).ToHashSet(StringComparer.Ordinal);
            foreach (var item in group.Items)
                item.IsCapsulePinned = pinned.Contains(item.Id);
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
        SearchHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SearchPlaceholder.Visibility = show && SearchBox.Text.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        // The header is only 360 DIP wide at the minimum size; the pin gets its own slot while
        // search is closed and yields it back to the search field while search is open.
        PinButton.Visibility = show ? Visibility.Collapsed : Visibility.Visible;

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

    private void OnPinClick(object sender, RoutedEventArgs e) => SetPinned(!Settings.Pinned);

    /// <summary>Pinned panels ignore auto-collapse and flyout deactivation.</summary>
    internal void SetPinned(bool pinned)
    {
        if (Settings.Pinned == pinned)
        {
            UpdatePinButton();
            return;
        }

        Settings.Pinned = pinned;
        _store.MarkDirty();
        _leaveSince = DateTime.MinValue;
        UpdatePinButton();

        if (pinned)
        {
            if (!_appWindow.IsVisible)
            {
                if (IsDocked)
                {
                    _appWindow.Show();
                    ShowExpanded();
                }
                else
                {
                    ShowFlyout();
                }
            }
            else if (IsDocked && _collapsed)
            {
                ShowExpanded();
            }
        }
        else
        {
            // Unpinning restarts the normal 400ms leave timer; an open flyout hides on its next
            // deactivation, while a docked panel collapses when the cursor is outside.
            _leaveSince = DateTime.UtcNow;
        }

        App.Log($"[pin] {Box.Name} pinned={pinned}");
    }

    private void UpdatePinButton()
    {
        var pinned = Settings.Pinned;
        PinButton.Foreground = (Brush)Application.Current.Resources[
            pinned ? "AccentFillColorDefaultBrush" : "PanelFgSecondary"];
        ToolTipService.SetToolTip(PinButton, pinned ? "取消钉住" : "钉住");
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

        var capsuleMenu = new MenuFlyoutSubItem
        {
            Text = "胶囊",
            Icon = new FontIcon { Glyph = "\uE718" }
        };
        var group = Box.Groups.FirstOrDefault(g => g.Items.Contains(item));
        if (group is not null)
        {
            var isPinned = group.CapsuleItemIds?.Contains(item.Id) == true;
            var pin = new MenuFlyoutItem
            {
                Text = isPinned ? "取消固定到胶囊" : "固定到胶囊",
                Icon = new FontIcon { Glyph = "\uE718" }
            };
            pin.Click += async (_, _) => await ToggleCapsulePinAsync(item, group);
            capsuleMenu.Items.Add(pin);
        }

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
        if (group is not null) menu.Items.Add(capsuleMenu);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(remove);

        SetCollapseSuppressed(true);
        menu.Closed += (_, _) => SetCollapseSuppressed(false);

        menu.ShowAt(source, new FlyoutShowOptions { Position = e.GetPosition(source) });
        e.Handled = true;
    }

    private async Task ToggleCapsulePinAsync(AppItem item, ItemGroup group)
    {
        group.CapsuleItemIds ??= new List<string>();
        if (group.CapsuleItemIds.Remove(item.Id))
        {
            _store.MarkDirty();
            RefreshView();
            return;
        }

        if (group.CapsuleItemIds.Count >= 3)
        {
            await ShowMessageAsync("胶囊已满", "每个分组最多固定 3 个工具到胶囊。");
            return;
        }

        group.CapsuleItemIds.Add(item.Id);
        _store.MarkDirty();
        RefreshView();
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
        var targets = Box.Groups.Where(g => g != _currentGroup).ToList();
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

        var sourceGroup = Box.Groups.FirstOrDefault(g => g.Items.Contains(item));
        if (sourceGroup is null) return;

        sourceGroup.CapsuleItemIds?.Remove(item.Id);
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

        var group = Box.Groups.FirstOrDefault(g => g.Items.Contains(item));
        group?.CapsuleItemIds?.Remove(item.Id);
        group?.Items.Remove(item);
        ShellIcons.Invalidate(item.Id);
        _store.MarkDirty();
        RefreshView();
    }

    private async void OnNewGroupClick(object sender, RoutedEventArgs e)
    {
        var name = await PromptAsync("新建分组", "分组名称", "新分组");
        if (name is null) return;

        if (Box.Groups.Any(g => string.Equals(g.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            await ShowMessageAsync("分组已存在", $"已经有一个叫「{name}」的分组了。");
            return;
        }

        var group = new ItemGroup { Name = name };
        Box.Groups.Add(group);
        Tabs.SelectedItem = group;
        _store.MarkDirty();
    }

    private void OnTabRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement source) return;
        if (source.DataContext is not ItemGroup group) return;

        var rename = new MenuFlyoutItem { Text = "重命名分组…", Icon = new FontIcon { Glyph = "\uE8AC" } };
        rename.Click += async (_, _) => await RenameGroupAsync(group);

        var remove = new MenuFlyoutItem
        {
            Text = "删除分组…",
            Icon = new FontIcon { Glyph = "\uE74D" },
            IsEnabled = Box.Groups.Count > 1
        };
        remove.Click += async (_, _) => await DeleteGroupAsync(group);

        var menu = new MenuFlyout();
        menu.Items.Add(rename);
        menu.Items.Add(remove);

        SetCollapseSuppressed(true);
        menu.Closed += (_, _) => SetCollapseSuppressed(false);
        menu.ShowAt(source, new FlyoutShowOptions { Position = e.GetPosition(source) });
        e.Handled = true;
    }

    private async Task RenameGroupAsync(ItemGroup group)
    {
        var name = await PromptAsync("重命名分组", "分组名称", group.Name);
        if (name is null) return;

        if (Box.Groups.Any(g => g != group
                && string.Equals(g.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            await ShowMessageAsync("分组已存在", $"已经有一个叫「{name}」的分组了。");
            return;
        }

        group.Name = name;
        _store.MarkDirty();
    }

    private async Task DeleteGroupAsync(ItemGroup group)
    {
        if (Box.Groups.Count <= 1)
        {
            await ShowMessageAsync("不能删除", "至少保留一个分组。");
            return;
        }

        var count = group.Items.Count;
        var message = count == 0
            ? $"确定删除分组「{group.Name}」吗？"
            : $"确定删除分组「{group.Name}」吗？\n分组里的 {count} 个工具会一起移除，但不会删除磁盘上的文件。";
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        if (!await ShowDialogAsync("删除分组", text, 210, "删除", "取消")) return;

        var index = Box.Groups.IndexOf(group);
        Box.Groups.Remove(group);

        if (_currentGroup == group)
        {
            var next = Box.Groups[Math.Clamp(index, 0, Box.Groups.Count - 1)];
            _currentGroup = next;
            Settings.LastGroupId = next.Id;
            Tabs.SelectedItem = next;
            RefreshView();
        }

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
        // One click, one action: hide to tray. This must not change the dock/placement; the tray
        // menu's "放回桌面" is the explicit un-dock action. Keeping the dock means showing it
        // again from the tray expands at the edge and can auto-collapse to its capsule.
        if (!_appWindow.IsVisible)
        {
            ShowFlyout();
            return;
        }

        SavePlacement();
        _appWindow.Hide();
        _mode = PanelMode.Hidden;
        UpdateRaiseButton();
    }

    private void UpdateRaiseButton() =>
        ToolTipService.SetToolTip(RaiseButton, "隐藏到托盘");

    /// <summary>Hide/show like a system flyout: detached from the desktop, above all windows.</summary>
    public void ToggleFlyout()
    {
        if (!_appWindow.IsVisible)
        {
            ShowFlyout();
            return;
        }

        // Docked panels toggle between capsule and expanded.
        if (IsDocked)
        {
            if (_collapsed) ShowExpanded();
            else HideCollapsed(force: true);
            UpdateRaiseButton();
            return;
        }

        // Only a topmost flyout is "popped up" and should hide on the next tray/hotkey click.
        // A Desktop-mode panel is visible at the desktop layer but may be behind other windows;
        // treat the click as "show/raise" (and restore its saved position), not as "hide".
        if (_mode == PanelMode.Flyout)
        {
            SavePlacement();
            _appWindow.Hide();
            _mode = PanelMode.Hidden;
            UpdateRaiseButton();
            return;
        }

        ShowFlyout();
    }

    public void ShowFlyout() => ShowFlyout(rememberPosition: true);

    /// <summary>Explicit --flyout startup: the classic bottom-right popup, not the saved spot.</summary>
    public void ShowFlyoutAtTray() => ShowFlyout(rememberPosition: false);

    private void ShowFlyout(bool rememberPosition)
    {
        if (IsDocked)
        {
            _appWindow.Show();
            if (_collapsed) ShowExpanded();
            Activate();
            _mode = PanelMode.Desktop;
            UpdateRaiseButton();
            SearchBox.Focus(FocusState.Programmatic);
            App.Log("[win] docked flyout expanded");
            return;
        }

        WindowChrome.SetKeepAtBottom(_hwnd, false);
        if (_appWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = true;

        if (rememberPosition) RestoreFlyoutPosition();
        else PositionNearTray();
        _appWindow.Show();
        EnsureChrome();
        Activate();
        _mode = PanelMode.Flyout;
        UpdateRaiseButton();
        SearchBox.Focus(FocusState.Programmatic);
        App.Log($"[win] flyout shown ({(rememberPosition ? "saved" : "bottom-right")})");
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

    /// <summary>Reopens an undocked panel where the user left it, clamped to the current display.</summary>
    private void RestoreFlyoutPosition()
    {
        var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest)?.WorkArea;
        if (area is null)
        {
            PositionNearTray();
            return;
        }

        var size = _appWindow.Size;
        var right = area.Value.X + area.Value.Width;
        var bottom = area.Value.Y + area.Value.Height;
        var x = Box.Window.X is double savedX
            ? (int)savedX
            : right - size.Width - 12;
        var y = Box.Window.Y is double savedY
            ? (int)savedY
            : bottom - size.Height - 12;

        x = Math.Clamp(x, area.Value.X, Math.Max(area.Value.X, right - size.Width));
        y = Math.Clamp(y, area.Value.Y, Math.Max(area.Value.Y, bottom - size.Height));
        _appWindow.Move(new PointInt32(x, y));
    }

    public void SinkToDesktop()
    {
        // A desktop panel is already where this action puts it; don't snap it back to the last
        // saved placement just because the header button was clicked.
        var wasDesktop = !IsDocked && _mode == PanelMode.Desktop;

        // "Back to desktop" also releases the edge dock.
        if (IsDocked)
        {
            if (_collapsed)
            {
                // Reveal the panel in place first. ShowExpanded() would slide it in using the dock
                // geometry after the dock side is cleared, which looks wrong for top docking.
                _collapsed = false;
                _capsule?.Hide();
                ShowCollapsedBar(false);
                Panel.Visibility = Visibility.Visible;
                var (width, height) = ExpandedSizePx();
                PlaceDocked(width, height);
            }

            Settings.Dock = DockSide.None;
            Settings.DockWidth = Math.Max(Dpi.Px(360, Dpi.ScaleFor(_hwnd)), Box.Window.Width);
            _store.MarkDirty();
            StopDockPolling();
            SetDockedTopmost(false);
        }

        if (_appWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = false;

        if (IsDocked)
        {
            GetWindowRect(_hwnd, out var rect);
            PlaceDocked(rect.Right - rect.Left, rect.Bottom - rect.Top);
        }
        else if (!wasDesktop)
        {
            var scale = Dpi.ScaleFor(_hwnd);
            _appWindow.Move(new PointInt32(
                Box.Window.X is double x ? (int)x : Dpi.Px(200, scale),
                Box.Window.Y is double y ? (int)y : Dpi.Px(160, scale)));
        }

        WindowChrome.SetKeepAtBottom(_hwnd, true);
        _appWindow.Show();
        SetWindowPos(_hwnd, new IntPtr(1), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        EnsureChrome();
        _mode = PanelMode.Desktop;
        UpdateRaiseButton();
        App.Log("[win] sunk to desktop (bottom-most)");
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
            EnsureChrome();

        // Only a real pointer activation means the user picked this box. Programmatic startup
        // activation can land on an unrelated visible strip (e.g. a right-docked box); persisting
        // that would overwrite the restored active box.
        if (args.WindowActivationState == WindowActivationState.PointerActivated)
            _manager.SetActive(this);

        if (Settings.Pinned) return;
        if (_suppressAutoHide) return;
        if (args.WindowActivationState != WindowActivationState.Deactivated) return;
        if (_mode != PanelMode.Flyout) return;

        _appWindow.Hide();
        _mode = PanelMode.Hidden;
        App.Log("[win] flyout auto-hidden");
    }

    private void OnHideClick(object sender, RoutedEventArgs e)
    {
        // X is "collapse": a docked panel becomes its capsule; a free panel goes to the tray.
        // The header's square-arrow button is the explicit hide-to-tray action.
        if (IsDocked)
        {
            HideCollapsed(force: true);
            return;
        }

        SavePlacement();
        _appWindow.Hide();
        _mode = PanelMode.Hidden;
        UpdateRaiseButton();
    }

    /// <summary>Persists the free-floating panel's current spot before hiding it.</summary>
    private void SavePlacement()
    {
        if (IsDocked) return;
        if (!GetWindowRect(_hwnd, out var rect)) return;

        Box.Window.X = rect.Left;
        Box.Window.Y = rect.Top;
        if (!AutoFitEnabled)
        {
            Box.Window.Width = rect.Right - rect.Left;
            Box.Window.Height = rect.Bottom - rect.Top;
        }

        _store.MarkDirty();
    }

    // =====================================================================
    // Box manager hooks
    // =====================================================================

    /// <summary>
    /// Reveals a docked window without activating it. Capsule boxes only need their separate
    /// capsule window, but a strip lives inside the parked panel and must be shown.
    /// </summary>
    internal void ShowParked()
    {
        if (!IsDocked) return;
        // SW_SHOWNOACTIVATE, not AppWindow.Show(): showing the parked windows must not steal
        // activation from the box that is actually being restored.
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        UpdateRaiseButton();
    }

    /// <summary>Initial show: a docked box stays collapsed (capsule visible); others appear on the desktop.</summary>
    internal void ShowStartup()
    {
        if (IsDocked)
        {
            _appWindow.Show();
            Activate();
            UpdateRaiseButton();
            return;
        }

        _appWindow.Show();
        EnsureChrome();
        Activate();
        UpdateRaiseButton();
    }

    /// <summary>User picked this box from the tray: expand a docked panel, otherwise show a flyout.</summary>
    internal void ShowForUser()
    {
        if (IsDocked)
        {
            _appWindow.Show();
            if (_collapsed) ShowExpanded();
            _mode = PanelMode.Desktop;
            Activate();
            UpdateRaiseButton();
            return;
        }

        ShowFlyout();
    }

    internal void OpenSettings() => OnSettingsClick(this, new RoutedEventArgs());

    internal void UpdateBoxName()
    {
        BoxName.Text = Box.Name;
        Title = $"TBox · {Box.Name}";
    }

    internal void CloseForDelete()
    {
        try
        {
            _capsule?.Close();
            _capsule = null;
            Close();
        }
        catch (Exception ex)
        {
            App.Log("[box] close failed: " + ex.Message);
        }
    }

    internal void ApplyThemeToCurrentWindow()
    {
        Root.RequestedTheme = AppTheme.Current;
        UpdatePinButton();
        _capsule?.ApplyTheme();
    }

    // =====================================================================
    // Settings / import (minimal for now)
    // =====================================================================

    private void ApplyTheme()
    {
        _store.MarkDirty();
        _manager.ApplyThemeAll();
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
            IsChecked = Settings.CollapsedStyle == CollapsedStyle.Capsule
        };
        var strip = new RadioButton
        {
            Content = "竖条：只留一条细边，悬停展开",
            GroupName = "collapsedStyle",
            IsChecked = Settings.CollapsedStyle == CollapsedStyle.Strip
        };

        var orientationBox = new ComboBox
        {
            Width = 210,
            ItemsSource = new[] { "自动（左右竖排 / 顶边横排）", "竖排", "横排" },
            SelectedIndex = (int)Settings.CapsuleOrientation,
            IsEnabled = capsule.IsChecked == true
        };
        var orientationRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 2, 0, 0)
        };
        orientationRow.Children.Add(new TextBlock
        {
            Text = "胶囊方向",
            FontSize = 12,
            Opacity = 0.75,
            VerticalAlignment = VerticalAlignment.Center
        });
        orientationRow.Children.Add(orientationBox);

        capsule.Checked += (_, _) => orientationBox.IsEnabled = true;
        strip.Checked += (_, _) => orientationBox.IsEnabled = false;

        var autoFit = new ToggleSwitch
        {
            IsOn = Settings.AutoFitWindow,
            Header = "随图标数量自适应窗口大小",
            OnContent = "开",
            OffContent = "关"
        };

        var maxRows = new ComboBox
        {
            Width = 88,
            ItemsSource = Enumerable.Range(1, 8).ToList(),
            SelectedItem = Math.Clamp(Settings.AutoFitMaxRows, 1, 8),
            IsEnabled = autoFit.IsOn
        };

        var maxRowsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 2, 0, 0)
        };
        maxRowsRow.Children.Add(new TextBlock
        {
            Text = "最多行数",
            FontSize = 12,
            Opacity = 0.75,
            VerticalAlignment = VerticalAlignment.Center
        });
        maxRowsRow.Children.Add(maxRows);

        var fitHint = new TextBlock
        {
            Text = "窗口最多显示这么多行；图标更多时先加宽窗口，再改为滚动。",
            FontSize = 11.5,
            Opacity = 0.55,
            TextWrapping = TextWrapping.Wrap
        };

        autoFit.Toggled += (_, _) =>
        {
            maxRows.IsEnabled = autoFit.IsOn;
            fitHint.Opacity = autoFit.IsOn ? 0.55 : 0.3;
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
            Text = _manager.HotkeyRegistered
                ? "Ctrl+Alt+T · 已注册"
                : "Ctrl+Alt+T · 被其它程序占用",
            FontSize = 12,
            Opacity = 0.7
        };

        var assemblyVersion = typeof(MainWindow).Assembly.GetName().Version;
        var versionText = assemblyVersion is null
            ? "TBox · WinUI 3"
            : $"TBox {assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build} · WinUI 3";

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Header("主题"));
        panel.Children.Add(themeSystem);
        panel.Children.Add(themeDark);
        panel.Children.Add(themeLight);
        panel.Children.Add(Header("收起形态"));
        panel.Children.Add(capsule);
        panel.Children.Add(strip);
        panel.Children.Add(orientationRow);
        panel.Children.Add(Header("窗口"));
        panel.Children.Add(autoFit);
        panel.Children.Add(maxRowsRow);
        panel.Children.Add(fitHint);
        panel.Children.Add(new Border { Height = 10 });
        panel.Children.Add(startup);
        panel.Children.Add(Header("快捷键"));
        panel.Children.Add(hotkey);
        panel.Children.Add(Header("数据目录"));
        panel.Children.Add(pathText);
        panel.Children.Add(openFolder);
        panel.Children.Add(Header("版本"));
        panel.Children.Add(new TextBlock { Text = versionText, FontSize = 12, Opacity = 0.7 });

        if (!await ShowDialogAsync($"设置 · {Box.Name}", panel, 760, "保存", "取消")) return;

        Data.Settings.Theme = themeDark.IsChecked == true ? ThemeMode.Dark
            : themeLight.IsChecked == true ? ThemeMode.Light
            : ThemeMode.System;
        ApplyTheme();

        var autoFitWasOn = Settings.AutoFitWindow;
        var maxRowsValue = maxRows.SelectedItem is int rows
            ? Math.Clamp(rows, 1, 8)
            : Settings.AutoFitMaxRows;
        var autoFitChanged = autoFit.IsOn != autoFitWasOn || maxRowsValue != Settings.AutoFitMaxRows;

        Settings.AutoFitWindow = autoFit.IsOn;
        Settings.AutoFitMaxRows = maxRowsValue;

        // Re-fit before a possible collapse, so the dock transition already uses the new height.
        if (!autoFit.IsOn && autoFitWasOn) RestorePreferredSize();
        else if (autoFit.IsOn && autoFitChanged) ApplyAutoFit();

        var orientationValue = orientationBox.SelectedIndex switch
        {
            1 => CapsuleOrientation.Vertical,
            2 => CapsuleOrientation.Horizontal,
            _ => CapsuleOrientation.Auto
        };
        var orientationChanged = orientationValue != Settings.CapsuleOrientation;
        Settings.CapsuleOrientation = orientationValue;

        var nextStyle = capsule.IsChecked == true ? CollapsedStyle.Capsule : CollapsedStyle.Strip;
        var styleChanged = nextStyle != Settings.CollapsedStyle;
        if (styleChanged) SetCollapsedStyle(nextStyle);
        else if (orientationChanged) ApplyCapsuleOrientation();

        Data.Settings.RunAtStartup = startup.IsOn;
        var startupOk = StartupEntry.Set(startup.IsOn);
        _store.MarkDirty();

        if (!startupOk)
            await ShowMessageAsync("开机启动设置失败",
                "无法写入当前用户的启动项，请检查注册表权限后重试。");
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var existing = Box.Groups.SelectMany(g => g.Items)
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
        Box.Window.Width = rect.Right - rect.Left;
        Box.Window.Height = rect.Bottom - rect.Top;
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
