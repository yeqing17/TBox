using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Dispatching;
using TBox.Models;

namespace TBox.Services;

/// <summary>
/// Owns %APPDATA%\TBox. Writes are atomic (temp file + replace) and debounced so
/// rapid UI changes don't hammer the disk.
/// </summary>
public sealed class DataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly DispatcherQueueTimer _saveTimer;
    private bool _dirty;

    public static DataStore Instance { get; } = new();

    public string RootDir { get; }
    public string IconsDir { get; }
    public string DataFile { get; }
    public TBoxData Data { get; private set; } = new();

    /// <summary>True once the file has been read; guards against saving the empty default over real data.</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>Set for data written by a newer schema; the file is never overwritten.</summary>
    public bool IsReadOnly { get; private set; }

    public string? LoadWarning { get; private set; }
    public string? LastSaveError { get; private set; }

    private DataStore()
    {
        var customRoot = Environment.GetEnvironmentVariable("TBOX_DATA");
        RootDir = string.IsNullOrWhiteSpace(customRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TBox")
            : Path.GetFullPath(customRoot);
        IconsDir = Path.Combine(RootDir, "icons");
        DataFile = Path.Combine(RootDir, "data.json");

        _saveTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) =>
        {
            if (_dirty) SaveNow();
        };
    }

    public void Load()
    {
        Directory.CreateDirectory(RootDir);
        Directory.CreateDirectory(IconsDir);

        if (!File.Exists(DataFile))
        {
            Data = CreateDefault();
            IsLoaded = true;
            SaveNow();
            return;
        }

        try
        {
            var json = File.ReadAllText(DataFile, Encoding.UTF8);

            var version = 1;
            var hasBoxes = false;
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.TryGetProperty("version", out var versionElement)
                    && versionElement.TryGetInt32(out var parsed))
                    version = parsed;
                hasBoxes = doc.RootElement.TryGetProperty("boxes", out var boxes)
                           && boxes.ValueKind == JsonValueKind.Array;
            }

            if (version > TBoxData.CurrentVersion)
            {
                IsReadOnly = true;
                LoadWarning = $"数据文件版本为 {version}，当前程序最高支持 {TBoxData.CurrentVersion}。\n" +
                              "为避免覆盖新版数据，本次以只读方式启动，修改不会保存。";
                Data = CreateDefault();
                IsLoaded = true;
                return;
            }

            if (version < TBoxData.CurrentVersion && !hasBoxes)
            {
                var legacy = JsonSerializer.Deserialize<LegacyTBoxData>(json, JsonOptions);
                Data = legacy is null ? CreateDefault() : Migrate(legacy);
                Normalize();
                IsLoaded = true;
                // Persist v2 immediately; SaveNow copies the old v1 file to data.bak.json first.
                SaveNow();
                return;
            }

            Data = JsonSerializer.Deserialize<TBoxData>(json, JsonOptions) ?? CreateDefault();
            Normalize();
            IsLoaded = true;
        }
        catch (Exception ex)
        {
            var backup = Path.Combine(RootDir, $"data.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            try { File.Copy(DataFile, backup, overwrite: true); } catch { }
            LoadWarning = $"数据文件无法读取（{ex.Message}）。\n已备份为 {backup}，将使用默认配置启动。";
            Data = CreateDefault();
            IsLoaded = true;
            SaveNow();
        }
    }

    private static TBoxData CreateDefault()
    {
        var data = new TBoxData();
        var box = CreateDefaultBox("工具箱");
        data.Boxes.Add(box);
        data.Settings.ActiveBoxId = box.Id;
        return data;
    }

    /// <summary>Creates a box with one empty group; the manager may override its placement.</summary>
    public static BoxData CreateDefaultBox(string name)
    {
        var box = new BoxData
        {
            Name = string.IsNullOrWhiteSpace(name) ? "工具箱" : name.Trim()
        };
        var group = new ItemGroup { Name = "我的工具" };
        box.Groups.Add(group);
        box.Settings.LastGroupId = group.Id;
        return box;
    }

    private static TBoxData Migrate(LegacyTBoxData legacy)
    {
        var box = new BoxData
        {
            Name = "工具箱",
            Settings = new BoxSettings
            {
                LastGroupId = legacy.Settings?.LastGroupId,
                Dock = legacy.Settings?.Dock ?? DockSide.None,
                DockWidth = legacy.Settings?.DockWidth ?? 420,
                CollapsedStyle = legacy.Settings?.CollapsedStyle ?? CollapsedStyle.Capsule,
                CapsuleOrientation = legacy.Settings?.CapsuleOrientation ?? CapsuleOrientation.Auto,
                AutoFitWindow = legacy.Settings?.AutoFitWindow ?? true,
                AutoFitMaxRows = legacy.Settings?.AutoFitMaxRows ?? 4
            },
            Window = legacy.Window ?? new WindowPlacement(),
            Groups = legacy.Groups ?? new()
        };

        var data = new TBoxData
        {
            Settings = new AppSettings
            {
                Theme = legacy.Settings?.Theme ?? ThemeMode.System,
                MinimizeToTray = legacy.Settings?.MinimizeToTray ?? true,
                RunAtStartup = legacy.Settings?.RunAtStartup ?? false,
                ActiveBoxId = box.Id
            }
        };
        data.Boxes.Add(box);
        return data;
    }

    private void Normalize()
    {
        Data.Version = TBoxData.CurrentVersion;
        Data.Settings ??= new AppSettings();
        Data.Boxes ??= new();

        if (Data.Boxes.Count == 0)
            Data.Boxes.Add(CreateDefaultBox("工具箱"));

        var seenBoxIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var box in Data.Boxes)
        {
            if (box is null) continue;

            if (string.IsNullOrWhiteSpace(box.Id) || !seenBoxIds.Add(box.Id))
                box.Id = ItemGroup.NewId();
            seenBoxIds.Add(box.Id);

            if (string.IsNullOrWhiteSpace(box.Name)) box.Name = "工具箱";
            box.Settings ??= new BoxSettings();
            box.Window ??= new WindowPlacement();
            box.Groups ??= new();

            box.Settings.Dock = Enum.IsDefined(typeof(DockSide), box.Settings.Dock)
                ? box.Settings.Dock : DockSide.None;
            box.Settings.CollapsedStyle = Enum.IsDefined(typeof(CollapsedStyle), box.Settings.CollapsedStyle)
                ? box.Settings.CollapsedStyle : CollapsedStyle.Capsule;
            box.Settings.CapsuleOrientation = Enum.IsDefined(typeof(CapsuleOrientation), box.Settings.CapsuleOrientation)
                ? box.Settings.CapsuleOrientation : CapsuleOrientation.Auto;
            box.Settings.AutoFitMaxRows = Math.Clamp(box.Settings.AutoFitMaxRows, 1, 8);

            foreach (var group in box.Groups)
            {
                if (string.IsNullOrWhiteSpace(group.Id)) group.Id = ItemGroup.NewId();
                if (string.IsNullOrWhiteSpace(group.Name)) group.Name = "未命名分组";
                if (group.Items is null) group.Items = new();
                foreach (var item in group.Items)
                {
                    if (string.IsNullOrWhiteSpace(item.Id)) item.Id = ItemGroup.NewId();
                    if (string.IsNullOrWhiteSpace(item.Name))
                        item.Name = Path.GetFileNameWithoutExtension(item.Path);
                }
            }

            if (box.Groups.Count == 0)
            {
                var group = new ItemGroup { Name = "我的工具" };
                box.Groups.Add(group);
                box.Settings.LastGroupId = group.Id;
            }

            if (box.Groups.All(g => g.Id != box.Settings.LastGroupId))
                box.Settings.LastGroupId = box.Groups[0].Id;
        }

        // Drop null entries a hand-edited file might contain.
        for (var i = Data.Boxes.Count - 1; i >= 0; i--)
            if (Data.Boxes[i] is null) Data.Boxes.RemoveAt(i);

        if (Data.Boxes.Count == 0)
            Data.Boxes.Add(CreateDefaultBox("工具箱"));

        if (string.IsNullOrWhiteSpace(Data.Settings.ActiveBoxId)
            || Data.Boxes.All(b => b.Id != Data.Settings.ActiveBoxId))
            Data.Settings.ActiveBoxId = Data.Boxes[0].Id;
    }

    public void MarkDirty()
    {
        if (!IsLoaded || IsReadOnly) return;
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        // Never let an un-loaded (or second-instance) store overwrite real data with the empty default.
        if (!IsLoaded || IsReadOnly) return;

        _dirty = false;
        _saveTimer.Stop();

        try
        {
            Directory.CreateDirectory(RootDir);
            var json = JsonSerializer.Serialize(Data, JsonOptions);
            var temp = DataFile + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (File.Exists(DataFile))
            {
                try { File.Copy(DataFile, Path.Combine(RootDir, "data.bak.json"), overwrite: true); }
                catch { }
            }

            File.Move(temp, DataFile, overwrite: true);
            LastSaveError = null;
        }
        catch (Exception ex)
        {
            LastSaveError = ex.Message;
        }
    }

    public string IconPathFor(AppItem item) => Path.Combine(IconsDir, item.Id + ".png");

    // ---------------------------------------------------------------- v1 compatibility

    /// <summary>Shape written by TBox 2.2.x and earlier.</summary>
    private sealed class LegacyTBoxData
    {
        public int Version { get; set; } = 1;
        public LegacyAppSettings? Settings { get; set; }
        public WindowPlacement? Window { get; set; }
        public System.Collections.ObjectModel.ObservableCollection<ItemGroup>? Groups { get; set; }
    }

    private sealed class LegacyAppSettings
    {
        public ThemeMode Theme { get; set; } = ThemeMode.System;
        public bool TopMost { get; set; }
        public bool MinimizeToTray { get; set; } = true;
        public bool RunAtStartup { get; set; }
        public string? LastGroupId { get; set; }
        public DockSide Dock { get; set; } = DockSide.None;
        public double DockWidth { get; set; } = 420;
        public CollapsedStyle CollapsedStyle { get; set; } = CollapsedStyle.Capsule;
        public CapsuleOrientation CapsuleOrientation { get; set; } = CapsuleOrientation.Auto;
        public bool AutoFitWindow { get; set; } = true;
        public int AutoFitMaxRows { get; set; } = 4;
    }
}
