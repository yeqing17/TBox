using System.IO;
using TBox.Models;

namespace TBox.Services;

/// <summary>Creates box items from shell paths and resolves .lnk metadata through WScript.Shell.</summary>
public static class Shortcuts
{
    public sealed record ShortcutInfo(
        string TargetPath,
        string Arguments,
        string WorkingDirectory,
        string IconLocation);

    private static readonly string[] ShortcutExtensions = { ".lnk", ".url" };

    /// <summary>Anything that exists can be pinned to the box (exe, lnk, bat, url, folder…).</summary>
    public static bool IsSupported(string path) => Directory.Exists(path) || File.Exists(path);

    public static AppItem CreateItem(string path)
    {
        path = Path.GetFullPath(path);
        var item = new AppItem
        {
            Name = Directory.Exists(path)
                ? new DirectoryInfo(path).Name
                : Path.GetFileNameWithoutExtension(path),
            Path = path,
            SourcePath = path,
            AddedAt = DateTime.Now
        };

        if (string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var info = Resolve(path);
            if (info is not null)
            {
                item.ResolvedTarget = info.TargetPath;
                item.Arguments = string.IsNullOrWhiteSpace(info.Arguments) ? null : info.Arguments;
                item.WorkDir = string.IsNullOrWhiteSpace(info.WorkingDirectory) ? null : info.WorkingDirectory;

                if (string.IsNullOrWhiteSpace(item.WorkDir) && !string.IsNullOrEmpty(info.TargetPath))
                {
                    var dir = Path.GetDirectoryName(info.TargetPath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) item.WorkDir = dir;
                }
            }
        }
        else if (!Directory.Exists(path))
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) item.WorkDir = dir;
            item.ResolvedTarget = path;
        }

        item.IsMissing = !item.TargetExists();
        return item;
    }

    /// <summary>Reads target/args/workdir out of a .lnk via late-bound WScript.Shell (no interop assembly needed).</summary>
    public static ShortcutInfo? Resolve(string lnkPath)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return null;

            shell = Activator.CreateInstance(type);
            if (shell is null) return null;

            dynamic dynShell = shell;
            shortcut = dynShell.CreateShortcut(lnkPath);
            if (shortcut is null) return null;

            dynamic dynShortcut = shortcut;
            string target = (string)(dynShortcut.TargetPath ?? string.Empty);
            string args = (string)(dynShortcut.Arguments ?? string.Empty);
            string workDir = (string)(dynShortcut.WorkingDirectory ?? string.Empty);
            string iconLocation = (string)(dynShortcut.IconLocation ?? string.Empty);

            if (string.IsNullOrWhiteSpace(target)) return null;
            return new ShortcutInfo(target, args, workDir, iconLocation);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (shortcut is not null && System.Runtime.InteropServices.Marshal.IsComObject(shortcut))
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shortcut);
            if (shell is not null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }

    /// <summary>Shortcuts sitting on the current user's and the all-users desktop.</summary>
    public static IEnumerable<string> DesktopShortcuts()
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder)
                    .Where(p => ShortcutExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
                    .OrderBy(Path.GetFileName, StringComparer.CurrentCulture);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
                if (seen.Add(file)) yield return file;
        }
    }
}
