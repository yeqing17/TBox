using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TBox.Interop;
using TBox.Models;
using TBox.Services;

namespace TBox;

/// <summary>
/// Owns the shared services and one panel window per box. The first version supports four boxes;
/// theme, startup, tray and hotkey are global, while groups/placement/dock/capsule settings are
/// per box.
/// </summary>
internal sealed class BoxManager
{
    public const int MaxBoxes = 4;

    private readonly DataStore _store = DataStore.Instance;
    private readonly Dictionary<string, MainWindow> _windows = new();
    private readonly DockPoll _dockPoll;
    private ShellHost? _shell;
    private MainWindow? _active;
    private bool _suppressActive;

    public TBoxData Data => _store.Data;
    public DockPoll DockPoll => _dockPoll;
    public bool HotkeyRegistered => _shell?.HotkeyRegistered ?? false;

    public BoxManager()
    {
        // One load/theme resolve for the whole process; windows never load the store themselves.
        _store.Load();
        AppTheme.Resolve(Data.Settings.Theme);
        _dockPoll = new DockPoll();
    }

    /// <summary>The boxes the UI exposes (older files with more than four entries keep the extras on disk).</summary>
    public IReadOnlyList<BoxData> Boxes => Data.Boxes.Take(MaxBoxes).ToList();

    public async void Start()
    {
        try
        {
            InitShell();

            var args = Environment.GetCommandLineArgs();
            var boxes = Boxes;
            var activeBox = boxes.FirstOrDefault(b => b.Id == Data.Settings.ActiveBoxId) ?? boxes.FirstOrDefault();
            if (activeBox is null) return;

            if (Data.Boxes.Count > MaxBoxes)
                App.Log($"[box] only the first {MaxBoxes} of {Data.Boxes.Count} boxes are loaded");

            // CLI dock flags apply only to the active box.
            if (args.Any(a => a.Equals("--dock-top", StringComparison.OrdinalIgnoreCase)))
                activeBox.Settings.Dock = DockSide.Top;
            if (args.Any(a => a.Equals("--dock-right", StringComparison.OrdinalIgnoreCase)))
                activeBox.Settings.Dock = DockSide.Right;

            _suppressActive = true;
            foreach (var box in boxes) CreateWindow(box);

            // Docked boxes park off-screen; showing them without activation makes strip-style
            // boxes visible at startup while the active box still decides the foreground window.
            foreach (var window in _windows.Values) window.ShowParked();

            // WinUI still queues Activated events for the parked windows; let them drain before
            // the intended active box claims the foreground and the persisted ActiveBoxId.
            await Task.Delay(120);
            _suppressActive = false;

            _windows.TryGetValue(activeBox.Id, out var activeWindow);
            if (activeWindow is not null)
            {
                _active = activeWindow;
                SetActive(activeWindow);
            }

            var flyout = args.Any(a => a.Equals("--flyout", StringComparison.OrdinalIgnoreCase));
            if (activeWindow is not null)
            {
                if (flyout) activeWindow.ShowFlyoutAtTray();
                else activeWindow.ShowStartup();
            }

            await Task.Delay(120);
            if (activeWindow is not null) SetActive(activeWindow);

            if (_store.LoadWarning is { Length: > 0 } warning)
                await ShowMessageAsync("数据读取失败", warning);

            if (args.Any(a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase)))
            {
                await Task.Delay(300);
                _active?.OpenSettings();
            }
        }
        catch (Exception ex)
        {
            App.Log("[box] start failed: " + ex);
        }
    }

    private void InitShell()
    {
        try
        {
            _shell = new ShellHost();
            _shell.HotkeyPressed += ToggleActiveBox;
            _shell.TrayLeftClick += ToggleActiveBox;
            _shell.TrayRightClick += ShowTrayMenu;

            var hotkey = _shell.RegisterHotkey();
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "tbox.ico");
            var tray = _shell.AddTrayIcon(iconPath, "TBox 桌面工具箱");
            App.Log($"[box] shell hwnd=0x{_shell.Handle:X} hotkey={hotkey} tray={tray} boxes={Data.Boxes.Count}");
        }
        catch (Exception ex)
        {
            App.Log("[box] shell init failed: " + ex);
        }
    }

    private MainWindow CreateWindow(BoxData box)
    {
        var window = new MainWindow(this, box);
        _windows[box.Id] = window;
        return window;
    }

    private MainWindow? GetOrCreateWindow(BoxData? box)
    {
        if (box is null) return null;
        if (_windows.TryGetValue(box.Id, out var existing)) return existing;
        if (!Boxes.Contains(box)) return null;

        var window = new MainWindow(this, box);
        _windows[box.Id] = window;
        return window;
    }

    public void SetActive(MainWindow window)
    {
        if (_suppressActive) return;

        var changed = _active != window || Data.Settings.ActiveBoxId != window.Box.Id;
        _active = window;
        if (Data.Settings.ActiveBoxId == window.Box.Id) return;

        Data.Settings.ActiveBoxId = window.Box.Id;
        _store.MarkDirty();
        if (changed) App.Log($"[box] active={window.Box.Name}");
    }

    public void OnWindowClosed(MainWindow window)
    {
        _windows.Remove(window.Box.Id);
        if (_active == window) _active = null;
    }

    public void ApplyThemeAll()
    {
        AppTheme.Resolve(Data.Settings.Theme);
        foreach (var window in _windows.Values)
            window.ApplyThemeToCurrentWindow();
        _store.MarkDirty();
    }

    private void ToggleActiveBox()
    {
        var box = _active?.Box ?? Boxes.FirstOrDefault();
        var window = _active ?? GetOrCreateWindow(box);
        if (window is null) return;

        SetActive(window);
        window.ToggleFlyout();
    }

    private void ShowBox(BoxData box)
    {
        var window = GetOrCreateWindow(box);
        if (window is null) return;

        SetActive(window);
        window.ShowForUser();
    }

    private void ShowTrayMenu()
    {
        if (_shell is null) return;

        var boxes = Boxes;
        var activeId = _active?.Box.Id ?? Data.Settings.ActiveBoxId;
        var items = new List<ShellHost.MenuItemSpec>();

        items.Add(new("盒子（勾选 = 当前活动）", 0, Enabled: false));
        for (var i = 0; i < boxes.Count; i++)
            items.Add(new($"盒子：{boxes[i].Name}", 100 + i, Checked: boxes[i].Id == activeId));

        items.Add(new("", 0, Separator: true));
        items.Add(new("新建盒子…", 10, Enabled: Data.Boxes.Count < MaxBoxes));
        items.Add(new("重命名当前盒子…", 11, Enabled: _active is not null));
        items.Add(new("删除当前盒子…", 12, Enabled: _active is not null && Data.Boxes.Count > 1));
        items.Add(new("", 0, Separator: true));
        items.Add(new("当前盒子放回桌面", 13, Enabled: _active is not null));
        items.Add(new("当前盒子收起样式：胶囊", 14,
            Checked: _active?.Box.Settings.CollapsedStyle == CollapsedStyle.Capsule, Enabled: _active is not null));
        items.Add(new("当前盒子收起样式：竖条", 15,
            Checked: _active?.Box.Settings.CollapsedStyle == CollapsedStyle.Strip, Enabled: _active is not null));
        items.Add(new("当前盒子钉住（不自动收起）", 16,
            Checked: _active?.Box.Settings.Pinned == true, Enabled: _active is not null));
        items.Add(new("", 0, Separator: true));
        items.Add(new("设置…", 20, Enabled: _active is not null));
        items.Add(new("退出", 30));

        var choice = _shell.ShowMenu(items);
        if (choice >= 100)
        {
            var index = choice - 100;
            if (index >= 0 && index < boxes.Count) ShowBox(boxes[index]);
            return;
        }

        switch (choice)
        {
            case 10:
                _ = CreateBoxAsync();
                break;
            case 11:
                _ = RenameActiveAsync();
                break;
            case 12:
                _ = DeleteActiveAsync();
                break;
            case 13:
                _active?.SinkToDesktop();
                break;
            case 14:
                _active?.SetCollapsedStyle(CollapsedStyle.Capsule);
                break;
            case 15:
                _active?.SetCollapsedStyle(CollapsedStyle.Strip);
                break;
            case 16:
                if (_active is { } pinWindow)
                    pinWindow.SetPinned(!pinWindow.Box.Settings.Pinned);
                break;
            case 20:
                _active?.OpenSettings();
                break;
            case 30:
                Exit();
                break;
        }
    }

    private async Task CreateBoxAsync()
    {
        if (Data.Boxes.Count >= MaxBoxes)
        {
            await ShowMessageAsync("盒子上限", $"最多支持 {MaxBoxes} 个盒子。");
            return;
        }

        var name = await PromptAsync("新建盒子", "盒子名称", UniqueBoxName());
        if (name is null) return;

        if (Data.Boxes.Any(b => string.Equals(b.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            await ShowMessageAsync("盒子已存在", $"已经有一个叫「{name}」的盒子了。");
            return;
        }

        var box = DataStore.CreateDefaultBox(name);
        var index = Data.Boxes.Count;
        box.Window = new WindowPlacement
        {
            X = 200 + index * 40,
            Y = 160 + index * 40,
            Width = 760,
            Height = 540
        };

        Data.Boxes.Add(box);
        Data.Settings.ActiveBoxId = box.Id;

        var window = GetOrCreateWindow(box);
        if (window is null)
        {
            Data.Boxes.Remove(box);
            return;
        }

        SetActive(window);
        window.ShowForUser();
        _store.MarkDirty();
    }

    private async Task RenameActiveAsync()
    {
        var window = _active;
        var box = window?.Box;
        if (window is null || box is null) return;

        var name = await PromptAsync("重命名盒子", "盒子名称", box.Name);
        if (name is null) return;

        if (Data.Boxes.Any(b => b != box
                && string.Equals(b.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            await ShowMessageAsync("盒子已存在", $"已经有一个叫「{name}」的盒子了。");
            return;
        }

        box.Name = name;
        window.UpdateBoxName();
        _store.MarkDirty();
    }

    private async Task DeleteActiveAsync()
    {
        var window = _active;
        var box = window?.Box;
        if (window is null || box is null) return;

        if (Data.Boxes.Count <= 1)
        {
            await ShowMessageAsync("不能删除", "至少保留一个盒子。");
            return;
        }

        if (!await ConfirmAsync("删除盒子",
                $"确定要删除「{box.Name}」吗？\n盒子里的工具记录会一起移除，但不会删除磁盘上的文件。"))
            return;

        Data.Boxes.Remove(box);
        _windows.Remove(box.Id);
        if (_active == window) _active = null;
        window.CloseForDelete();
        _store.MarkDirty();

        var next = Boxes.FirstOrDefault(b => b.Id != box.Id) ?? Boxes.FirstOrDefault();
        if (next is null) return;

        var nextWindow = GetOrCreateWindow(next);
        if (nextWindow is null) return;

        SetActive(nextWindow);
        nextWindow.ShowForUser();
    }

    private string UniqueBoxName()
    {
        var names = Data.Boxes.Select(b => b.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!names.Contains("新盒子")) return "新盒子";

        for (var i = 2; ; i++)
        {
            var candidate = $"新盒子 {i}";
            if (!names.Contains(candidate)) return candidate;
        }
    }

    private async Task<string?> PromptAsync(string title, string label, string initial)
    {
        var input = new TextBox
        {
            Text = initial,
            PlaceholderText = label,
            SelectionStart = initial.Length
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(input);

        SuppressAll(true);
        try
        {
            var dialog = new DialogWindow(title, panel, 180);
            if (!await dialog.ShowAsync(_active)) return null;
        }
        finally
        {
            SuppressAll(false);
        }

        var text = input.Text.Trim();
        return text.Length == 0 ? null : text;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };

        SuppressAll(true);
        try
        {
            var dialog = new DialogWindow(title, text, 190, "知道了", null);
            await dialog.ShowAsync(_active);
        }
        finally
        {
            SuppressAll(false);
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };

        SuppressAll(true);
        try
        {
            var dialog = new DialogWindow(title, text, 210, "删除", "取消");
            return await dialog.ShowAsync(_active);
        }
        finally
        {
            SuppressAll(false);
        }
    }

    private void SuppressAll(bool suppressed)
    {
        foreach (var window in _windows.Values)
            window.SetCollapseSuppressed(suppressed);
    }

    private void Exit()
    {
        App.Log("[box] exit");
        _store.SaveNow();
        _shell?.Dispose();
        _shell = null;

        // Application.Exit() closes the WinUI windows but can leave the process alive when it is
        // invoked from the native tray-menu callback (the mutex then blocks every later launch).
        // The data is already flushed and the tray/hotkey owner destroyed, so terminate directly.
        Environment.Exit(0);
    }
}
