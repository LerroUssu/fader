using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static Fader.Native.NativeMethods;

namespace Fader.Native;

/// <summary>
/// Resolves an app icon as a frozen WPF ImageSource. Order: the session's own icon path
/// (system sounds use "@%SystemRoot%\System32\AudioSrv.Dll,-202"), then the shell's high-res
/// icon for the executable (what Explorer shows), then the classic ExtractIconEx.
/// </summary>
public static class IconExtractor
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? ForApp(string? iconPath, string? exePath)
    {
        var key = (iconPath ?? string.Empty) + "|" + (exePath ?? string.Empty);
        lock (Cache) if (Cache.TryGetValue(key, out var cached)) return cached;

        ImageSource? img = null;
        try { img = FromIconPath(iconPath) ?? FromShell(exePath) ?? FromExtractIcon(exePath, 0); }
        catch (Exception ex) { Log.Error("icon " + key, ex); }
        if (img is { CanFreeze: true, IsFrozen: false }) img.Freeze();

        lock (Cache) Cache[key] = img;
        return img;
    }

    private static ImageSource? FromIconPath(string? iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath)) return null;
        var p = iconPath.StartsWith('@') ? iconPath[1..] : iconPath;
        p = Environment.ExpandEnvironmentVariables(p);
        int index = 0;
        int comma = p.LastIndexOf(',');
        if (comma > 0 && int.TryParse(p[(comma + 1)..], out var idx)) { index = idx; p = p[..comma]; }
        p = p.Trim('"');
        if (!File.Exists(p)) return null;

        var ext = Path.GetExtension(p).ToLowerInvariant();
        if (ext is ".png" or ".jpg" or ".jpeg" or ".ico")
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.UriSource = new Uri(p);
            bi.DecodePixelWidth = 64;
            bi.EndInit();
            return bi;
        }
        return FromExtractIcon(p, index);
    }

    private static ImageSource? FromExtractIcon(string? path, int index)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var count = ExtractIconEx(path, index, out var large, out var small, 1);
        try
        {
            if (count == 0 || large == IntPtr.Zero) return null;
            return Imaging.CreateBitmapSourceFromHIcon(large, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            if (large != IntPtr.Zero) DestroyIcon(large);
            if (small != IntPtr.Zero) DestroyIcon(small);
        }
    }

    private static ImageSource? FromShell(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(exePath, IntPtr.Zero, ref iid, out var factory) != 0 || factory == null) return null;
        IntPtr hbm = IntPtr.Zero;
        try
        {
            if (factory.GetImage(new SIZE(64, 64), SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hbm) != 0 || hbm == IntPtr.Zero) return null;
            return FromHBitmapWithAlpha(hbm);
        }
        finally
        {
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>Imaging.CreateBitmapSourceFromHBitmap drops the alpha channel; read the DIB bits directly instead.</summary>
    private static ImageSource? FromHBitmapWithAlpha(IntPtr hbm)
    {
        var ds = new DIBSECTION();
        int got = GetObject(hbm, Marshal.SizeOf<DIBSECTION>(), ref ds);
        if (got == 0 || ds.dsBm.bmBits == IntPtr.Zero || ds.dsBm.bmBitsPixel != 32)
            return Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

        int w = ds.dsBm.bmWidth, h = Math.Abs(ds.dsBm.bmHeight), stride = ds.dsBm.bmWidthBytes;
        var bytes = new byte[stride * h];
        Marshal.Copy(ds.dsBm.bmBits, bytes, 0, bytes.Length);

        if (ds.dsBmih.biHeight > 0) // bottom-up DIB: flip rows
        {
            var flipped = new byte[bytes.Length];
            for (int y = 0; y < h; y++) Buffer.BlockCopy(bytes, (h - 1 - y) * stride, flipped, y * stride, stride);
            bytes = flipped;
        }

        bool anyAlpha = false;
        for (int i = 3; i < bytes.Length; i += 4) if (bytes[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (int i = 3; i < bytes.Length; i += 4) bytes[i] = 255; // no alpha channel in use: treat as opaque

        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, bytes, stride);
    }
}
