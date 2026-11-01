using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using TBox.Models;

namespace TBox.Services;

/// <summary>Launches items with the same semantics as double-clicking them in Explorer.</summary>
public static class Launcher
{
    public readonly record struct LaunchResult(bool Ok, string? Error)
    {
        public static LaunchResult Success => new(true, null);
        public static LaunchResult Canceled => new(false, null);
    }

    public static LaunchResult Open(AppItem item)
    {
        if (item.IsFolder)
            return OpenFolder(item.Path);

        var psi = new ProcessStartInfo
        {
            FileName = item.Path,
            UseShellExecute = true
        };

        if (!string.IsNullOrWhiteSpace(item.Arguments)) psi.Arguments = item.Arguments;
        var workDir = item.EffectiveWorkDir;
        if (!string.IsNullOrEmpty(workDir)) psi.WorkingDirectory = workDir;

        return Start(psi);
    }

    public static LaunchResult OpenAsAdmin(AppItem item)
    {
        // A shortcut launched with "runas" elevates the shortcut host, not always the target,
        // so prefer the resolved executable when we have one.
        var exe = item.ResolvedTarget;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) exe = item.Path;

        if (Directory.Exists(exe)) return OpenFolder(exe);

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = true,
            Verb = "runas"
        };
        if (!string.IsNullOrWhiteSpace(item.Arguments)) psi.Arguments = item.Arguments;
        var workDir = item.EffectiveWorkDir;
        if (!string.IsNullOrEmpty(workDir)) psi.WorkingDirectory = workDir;

        return Start(psi);
    }

    public static void OpenLocation(AppItem item)
    {
        try
        {
            var target = item.DisplayTarget;
            if (Directory.Exists(target))
            {
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{target}\"", UseShellExecute = true });
            }
            else if (File.Exists(target))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{target}\"",
                    UseShellExecute = true
                });
            }
            else
            {
                var fallback = item.Path;
                if (Directory.Exists(fallback))
                    Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{fallback}\"", UseShellExecute = true });
            }
        }
        catch { /* explorer is best effort */ }
    }

    private static LaunchResult OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{path}\"", UseShellExecute = true });
            return LaunchResult.Success;
        }
        catch (Exception ex)
        {
            return new LaunchResult(false, ex.Message);
        }
    }

    private static LaunchResult Start(ProcessStartInfo psi)
    {
        try
        {
            Process.Start(psi);
            return LaunchResult.Success;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // User dismissed the UAC prompt — not an error worth a dialog.
            return LaunchResult.Canceled;
        }
        catch (Win32Exception ex)
        {
            return new LaunchResult(false, $"无法启动：{psi.FileName}\n{ex.Message}");
        }
        catch (Exception ex)
        {
            return new LaunchResult(false, $"无法启动：{psi.FileName}\n{ex.Message}");
        }
    }
}

/// <summary>HKCU Run entry for "start with Windows".</summary>
public static class StartupEntry
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TBox";

    public static string ExecutablePath =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;

    public static bool IsEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return;
            if (enabled)
                key.SetValue(ValueName, $"\"{ExecutablePath}\"");
            else if (key.GetValue(ValueName) is not null)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { /* registry may be locked down; the checkbox just won't stick */ }
    }
}
