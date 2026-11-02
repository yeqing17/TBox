using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace TBox.Models;

public enum DockSide
{
    None = 0,
    Left = 1,
    Right = 2
}

public enum CollapsedStyle
{
    /// <summary>Small capsule with quick-launch icons.</summary>
    Capsule = 0,

    /// <summary>Thin strip on the edge, expands on hover.</summary>
    Strip = 1
}

public enum ThemeMode
{
    System = 0,
    Light = 1,
    Dark = 2
}

/// <summary>Root object persisted to %APPDATA%\TBox\data.json.</summary>
public sealed class TBoxData
{
    public int Version { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public WindowPlacement Window { get; set; } = new();
    public ObservableCollection<ItemGroup> Groups { get; set; } = new();
}

public sealed class AppSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public bool TopMost { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool RunAtStartup { get; set; }
    public string? LastGroupId { get; set; }

    /// <summary>Edge the panel is docked to; None means free-floating.</summary>
    public DockSide Dock { get; set; } = DockSide.None;

    /// <summary>Width restored when a docked panel expands.</summary>
    public double DockWidth { get; set; } = 420;

    /// <summary>How a docked panel presents itself while collapsed.</summary>
    public CollapsedStyle CollapsedStyle { get; set; } = CollapsedStyle.Capsule;

    /// <summary>Resize the panel to the current group's icon count instead of a fixed window size.</summary>
    public bool AutoFitWindow { get; set; } = true;

    /// <summary>Row cap for auto-fit; beyond it the window stops growing and the grid scrolls.</summary>
    public int AutoFitMaxRows { get; set; } = 4;
}

public sealed class WindowPlacement
{
    public double? X { get; set; }
    public double? Y { get; set; }
    public double Width { get; set; } = 760;
    public double Height { get; set; } = 540;
    public bool Maximized { get; set; }
}

public sealed class ItemGroup : INotifyPropertyChanged
{
    private string _name = "未命名分组";

    public string Id { get; set; } = NewId();

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            Raise();
        }
    }

    public ObservableCollection<AppItem> Items { get; set; } = new();

    /// <summary>Item count shown on the tab; kept in sync by the view.</summary>
    [JsonIgnore]
    public int Count => Items.Count;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void RefreshCount() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));

    public static string NewId() => Guid.NewGuid().ToString("N")[..10];
}

public sealed class AppItem : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private ImageSource? _icon;
    private bool _isMissing;

    public string Id { get; set; } = ItemGroup.NewId();
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            Raise();
        }
    }

    /// <summary>Path handed to ShellExecute — may be a .lnk, .exe, .bat, .url or a folder.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Original path the user dropped in (differs from Path only for resolved shortcuts kept as-is).</summary>
    public string? SourcePath { get; set; }

    /// <summary>Free text explaining what the tool is for; matched by search.</summary>
    public string? Description { get; set; }

    /// <summary>Space/comma separated tags; matched by search.</summary>
    public string? Keywords { get; set; }

    public string? Arguments { get; set; }
    public string? WorkDir { get; set; }

    /// <summary>Real target behind a .lnk, used for the missing-target check and tooltips.</summary>
    public string? ResolvedTarget { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.Now;

    [JsonIgnore]
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            Raise();
            Raise(nameof(PlaceholderVisibility));
        }
    }

    [JsonIgnore]
    public Visibility PlaceholderVisibility => _icon is null ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public bool IsMissing
    {
        get => _isMissing;
        set
        {
            if (_isMissing == value) return;
            _isMissing = value;
            Raise();
            Raise(nameof(MissingVisibility));
        }
    }

    [JsonIgnore]
    public Visibility MissingVisibility => _isMissing ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public string? IconPath { get; set; }

    [JsonIgnore]
    public bool IsFolder => Directory.Exists(Path);

    [JsonIgnore]
    public string DisplayTarget => ResolvedTarget is { Length: > 0 } ? ResolvedTarget : Path;

    [JsonIgnore]
    public string TooltipText
    {
        get
        {
            var parts = new List<string> { Name };
            if (!string.IsNullOrWhiteSpace(Description)) parts.Add(Description);
            if (!string.IsNullOrWhiteSpace(Keywords)) parts.Add("关键字：" + Keywords);
            parts.Add(DisplayTarget);
            return string.Join("\n", parts);
        }
    }

    /// <summary>
    /// True when every whitespace-separated token of the query appears in the name, description or
    /// keywords — so "串口 modbus" finds a tool tagged with both.
    /// </summary>
    public bool Matches(string term)
    {
        if (string.IsNullOrWhiteSpace(term)) return true;

        var haystack = string.Join(" ", new[] { Name, Description, Keywords }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

        var tokens = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.All(token => haystack.Contains(token, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>Working directory used when launching: the shortcut's recorded one, else the target's folder.</summary>
    [JsonIgnore]
    public string EffectiveWorkDir
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(WorkDir) && Directory.Exists(WorkDir)) return WorkDir;
            var dir = System.IO.Path.GetDirectoryName(DisplayTarget);
            return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? dir : string.Empty;
        }
    }

    public bool TargetExists()
    {
        try
        {
            var check = DisplayTarget;
            if (string.IsNullOrWhiteSpace(check)) return false;
            return File.Exists(check) || Directory.Exists(check);
        }
        catch
        {
            return false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void SetIcon(ImageSource? icon) => Icon = icon;
}
