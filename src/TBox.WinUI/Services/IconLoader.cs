using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;

namespace TBox.Services;

/// <summary>Turns a cached PNG into an image WinUI can render.</summary>
public static class IconLoader
{
    public static async Task<BitmapImage?> LoadAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
