using Microsoft.UI.Xaml;
using Microsoft.Win32;
using TBox.Models;

namespace TBox;

/// <summary>Resolves the effective light/dark theme (the app follows the system by default).</summary>
internal static class AppTheme
{
    public static ElementTheme Current { get; private set; } = ElementTheme.Dark;

    public static void Resolve(ThemeMode mode)
    {
        Current = mode switch
        {
            ThemeMode.Light => ElementTheme.Light,
            ThemeMode.Dark => ElementTheme.Dark,
            _ => IsSystemDark() ? ElementTheme.Dark : ElementTheme.Light
        };
    }

    public static bool IsDark => Current == ElementTheme.Dark;

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return true;
        }
    }
}
