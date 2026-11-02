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
        var group = new ItemGroup { Name = "我的工具" };
        data.Groups.Add(group);
        data.Settings.LastGroupId = group.Id;
        return data;
    }

    private void Normalize()
    {
        Data.Settings ??= new AppSettings();
        Data.Window ??= new WindowPlacement();
        Data.Groups ??= new();

        // Hand-edited JSON must not produce a zero-row or absurdly tall fit.
        Data.Settings.AutoFitMaxRows = Math.Clamp(Data.Settings.AutoFitMaxRows, 1, 8);

        foreach (var group in Data.Groups)
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

        if (Data.Groups.Count == 0)
        {
            var group = new ItemGroup { Name = "我的工具" };
            Data.Groups.Add(group);
            Data.Settings.LastGroupId = group.Id;
        }

        if (Data.Groups.All(g => g.Id != Data.Settings.LastGroupId))
            Data.Settings.LastGroupId = Data.Groups[0].Id;
    }

    public void MarkDirty()
    {
        if (!IsLoaded) return;
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        // Never let an un-loaded (or second-instance) store overwrite real data with the empty default.
        if (!IsLoaded) return;

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
}
