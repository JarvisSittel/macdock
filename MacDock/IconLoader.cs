using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using ManagedShell.Common.Helpers;
using ManagedShell.UWPInterop;
using MsIconSize = ManagedShell.Common.Enums.IconSize;
using IOPath = System.IO.Path;

namespace MacDock;

/// <summary>Loads large, crisp icons. Magnified dock icons need 256px sources, which many exes lack.</summary>
static class IconLoader
{
    static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Icon overrides: drop e.g. "chrome.png" into %APPDATA%\MacDock\icons.</summary>
    public static ImageSource Override(params string[] names)
    {
        foreach (var raw in names)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var name = string.Concat(raw.Split(IOPath.GetInvalidFileNameChars()));
            foreach (var ext in new[] { ".png", ".ico" })
            {
                var file = IOPath.Combine(DockSettings.IconsDir, name + ext);
                if (!File.Exists(file)) continue;
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(file);
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }
                catch (Exception e) { Log.Error("icon override " + file, e); }
            }
        }
        return null;
    }

    public static ImageSource ForPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (Cache.TryGetValue(path, out var cached)) return cached;

        ImageSource result = null;
        try
        {
            var jumbo = IconImageConverter.GetImageFromAssociatedIcon(path, MsIconSize.Jumbo) as BitmapSource;
            // Exes without a 256px icon come back as a 32/48px icon in the corner of a 256px canvas.
            if (jumbo != null && ContentFraction(jumbo) >= 0.5)
                result = jumbo;
            else
                result = IconImageConverter.GetImageFromAssociatedIcon(path, MsIconSize.ExtraLarge) ?? jumbo;
            result?.Freeze();
        }
        catch (Exception e) { Log.Error("icon " + path, e); }

        Cache[path] = result;
        return result;
    }

    public static ImageSource ForAppId(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return null;
        var key = "aumid:" + aumid;
        if (Cache.TryGetValue(key, out var cached)) return cached;
        ImageSource result = null;
        try
        {
            var app = StoreAppHelper.AppList.GetAppByAumid(aumid);
            result = app?.GetIconImageSource(MsIconSize.Jumbo);
            result?.Freeze();
        }
        catch (Exception e) { Log.Error("store icon " + aumid, e); }
        Cache[key] = result;
        return result;
    }

    public static string StoreAppName(string aumid)
    {
        try { return StoreAppHelper.AppList.GetAppByAumid(aumid)?.DisplayName; }
        catch { return null; }
    }

    /// <summary>The Recycle Bin icon Windows is currently using (respects custom desktop icons).</summary>
    public static ImageSource RecycleBin(bool full)
    {
        const string clsid = "{645FF040-5081-101B-9F08-00AA002F954E}";
        string location = null;
        foreach (var (hive, path) in new[]
                 {
                     (Registry.CurrentUser, $@"Software\Microsoft\Windows\CurrentVersion\Explorer\CLSID\{clsid}\DefaultIcon"),
                     (Registry.ClassesRoot, $@"CLSID\{clsid}\DefaultIcon"),
                 })
        {
            using var key = hive.OpenSubKey(path);
            location = key?.GetValue(full ? "Full" : "Empty") as string;
            if (!string.IsNullOrEmpty(location)) break;
        }
        location ??= full ? @"%SystemRoot%\System32\imageres.dll,-54" : @"%SystemRoot%\System32\imageres.dll,-55";

        try
        {
            int comma = location.LastIndexOf(',');
            string file = Environment.ExpandEnvironmentVariables((comma > 0 ? location[..comma] : location).Trim('"'));
            int index = comma > 0 && int.TryParse(location[(comma + 1)..], out var i) ? i : 0;

            var icons = new IntPtr[1];
            var ids = new uint[1];
            if (PrivateExtractIcons(file, index, 256, 256, icons, ids, 1, 0) > 0 && icons[0] != IntPtr.Zero)
            {
                try
                {
                    var img = Imaging.CreateBitmapSourceFromHIcon(icons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    img.Freeze();
                    return img;
                }
                finally { DestroyIcon(icons[0]); }
            }
        }
        catch (Exception e) { Log.Error("recycle bin icon " + location, e); }
        return null;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, uint[] ids, uint count, uint flags);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);

    /// <summary>Fraction of the bitmap's width actually covered by visible pixels.</summary>
    static double ContentFraction(BitmapSource src)
    {
        var bmp = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        var px = new byte[w * h * 4];
        bmp.CopyPixels(px, w * 4, 0);
        int minX = w, maxX = -1, minY = h, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (px[(y * w + x) * 4 + 3] > 16)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
        if (maxX < 0) return 0;
        return Math.Max(maxX - minX + 1, maxY - minY + 1) / (double)w;
    }
}
