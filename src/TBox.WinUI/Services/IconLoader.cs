using System.Collections.Concurrent;
using Microsoft.UI.Xaml.Media.Imaging;

namespace TBox.Services;

/// <summary>
/// Turns a cached PNG into an image WinUI can render. Four box windows share the decoded
/// BitmapImage (keyed by path + mtime), so the same shortcut is decoded once instead of once per box.
/// </summary>
public static class IconLoader
{
    private static readonly ConcurrentDictionary<string, Task<BitmapImage?>> Cache = new();

    public static Task<BitmapImage?> LoadAsync(string? path, int targetPixels = 0)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Task.FromResult<BitmapImage?>(null);

        string key;
        try
        {
            // Decode at the physical display size (path+mtime+target): a 256px shell icon
            // downscaled by the compositor to 44 DIP aliases on 4K/200%; letting WIC decode
            // directly to 88 or 132 px keeps the card icons crisp without a second memory copy.
            key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks + "|" + Math.Max(0, targetPixels);
        }
        catch
        {
            return Task.FromResult<BitmapImage?>(null);
        }

        return Cache.GetOrAdd(key, _ => LoadCoreAsync(path, targetPixels));
    }

    private static async Task<BitmapImage?> LoadCoreAsync(string path, int targetPixels)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var bitmap = new BitmapImage();
            if (targetPixels > 0)
                bitmap.DecodePixelWidth = targetPixels;
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
