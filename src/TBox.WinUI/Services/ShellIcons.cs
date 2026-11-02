using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using TBox.Models;

namespace TBox.Services;

/// <summary>
/// Pulls real Windows shell icons (up to 256px) and caches them as PNG per item id.
/// Images are saved to disk because WinUI's BitmapImage decodes from a stream/URI.
/// </summary>
public static class ShellIcons
{
    private const uint SHGFI_ICON = 0x00000100;
    private const uint SHGFI_LARGEICON = 0x00000000;
    private const uint SHGFI_SYSICONINDEX = 0x00004000;

    private const int SHIL_LARGE = 0x0;
    private const int SHIL_EXTRALARGE = 0x2;
    private const int SHIL_JUMBO = 0x4;
    private const int ILD_TRANSPARENT = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, out int pi);
        [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, out int pi);
        [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
        [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
        [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, out int pi);
        [PreserveSig] int Draw(IntPtr pimldp);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static readonly Guid IidImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    /// <summary>Returns the cached PNG path, extracting it first when needed.</summary>
    public static string? EnsureIcon(AppItem item)
    {
        var cachePath = DataStore.Instance.IconPathFor(item);

        if (File.Exists(cachePath) && IsCacheFresh(cachePath, item))
            return cachePath;

        if (!item.TargetExists())
            return File.Exists(cachePath) ? cachePath : null;

        var hIcon = ExtractHIcon(item.Path);
        if (hIcon == IntPtr.Zero && !string.Equals(item.Path, item.DisplayTarget, StringComparison.OrdinalIgnoreCase))
            hIcon = ExtractHIcon(item.DisplayTarget);

        if (hIcon == IntPtr.Zero)
            return File.Exists(cachePath) ? cachePath : null;

        var saved = TrySavePng(hIcon, cachePath);
        DestroyIcon(hIcon);
        return saved ? cachePath : (File.Exists(cachePath) ? cachePath : null);
    }

    public static void Invalidate(string itemId)
    {
        try
        {
            var path = Path.Combine(DataStore.Instance.IconsDir, itemId + ".png");
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private static bool IsCacheFresh(string cachePath, AppItem item)
    {
        try
        {
            var cacheTime = File.GetLastWriteTimeUtc(cachePath);
            var check = item.DisplayTarget;
            if (!File.Exists(check) && !Directory.Exists(check)) return true;
            return File.GetLastWriteTimeUtc(check) <= cacheTime;
        }
        catch
        {
            return true;
        }
    }

    private static IntPtr ExtractHIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return IntPtr.Zero;

        try
        {
            var shfi = new SHFILEINFO();
            var result = SHGetFileInfo(path, 0, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(),
                SHGFI_SYSICONINDEX | SHGFI_LARGEICON);
            // Index 0 is a valid (generic) icon; only a failed SHGetFileInfo disqualifies.
            if (result != IntPtr.Zero && shfi.iIcon >= 0)
            {
                foreach (var listType in new[] { SHIL_JUMBO, SHIL_EXTRALARGE, SHIL_LARGE })
                {
                    var iid = IidImageList;
                    if (SHGetImageList(listType, ref iid, out var list) != 0 || list is null) continue;
                    try
                    {
                        if (list.GetIcon(shfi.iIcon, ILD_TRANSPARENT, out var hIcon) == 0 && hIcon != IntPtr.Zero)
                            return hIcon;
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(list);
                    }
                }
            }
        }
        catch { }

        try
        {
            var fallback = new SHFILEINFO();
            SHGetFileInfo(path, 0, ref fallback, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
            return fallback.hIcon;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static bool TrySavePng(IntPtr hIcon, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var icon = Icon.FromHandle(hIcon);
            using var bitmap = icon.ToBitmap();
            var temp = path + ".tmp";
            bitmap.Save(temp, ImageFormat.Png);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
