using Microsoft.UI.Xaml.Media.Imaging;

namespace TBox.Services;

/// <summary>
/// Loads the shared logo image once. The header mark and the empty-capsule mark both use it;
/// keeping one BitmapImage avoids decoding the same PNG twice.
/// </summary>
internal static class AppLogo
{
    // Keep the source stream alive: BitmapImage may still be reading from it when the
    // first frame is rendered.
    private static Stream? _stream;

    public static BitmapImage? Image { get; private set; }

    public static void Load()
    {
        if (Image is not null) return;

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "tbox-512.png");
            if (!File.Exists(path)) return;

            _stream = File.OpenRead(path);
            var bitmap = new BitmapImage();
            bitmap.SetSource(_stream.AsRandomAccessStream());
            Image = bitmap;
        }
        catch (Exception ex)
        {
            App.Log("[logo] " + ex.Message);
        }
    }
}
