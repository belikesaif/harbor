using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static Harbor.Native;

namespace Harbor;

/// <summary>High-resolution shell icons, display names and shortcut resolution (all cached).</summary>
internal static class Shell
{
    static readonly Dictionary<string, BitmapSource> iconCache = new(StringComparer.OrdinalIgnoreCase);
    static Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    public static BitmapSource GetIcon(string parsingName, int px = 128, bool noCache = false)
    {
        if (string.IsNullOrEmpty(parsingName)) return null;
        string key = parsingName + "|" + px;
        if (!noCache && iconCache.TryGetValue(key, out var cached)) return cached;
        BitmapSource result = null;
        try
        {
            if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref IID_IShellItem, out object item) == 0)
            {
                var factory = (IShellItemImageFactory)item;
                // SIIGBF_ICONONLY = 0x4
                if (factory.GetImage(new SIZE { cx = px, cy = px }, 0x4, out IntPtr hbm) == 0 && hbm != IntPtr.Zero)
                {
                    result = FromHBitmap(hbm);
                    DeleteObject(hbm);
                }
                Marshal.ReleaseComObject(item);
            }
        }
        catch (Exception ex) { Log.Write("icon " + parsingName + ": " + ex.Message); }
        iconCache[key] = result;
        return result;
    }

    static BitmapSource FromHBitmap(IntPtr hbm)
    {
        if (GetObject(hbm, Marshal.SizeOf<DIBSECTION>(), out var ds) == 0 || ds.dsBm.bmBits == IntPtr.Zero)
        {
            var bs = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bs.Freeze(); return bs;
        }
        int w = ds.dsBm.bmWidth, h = ds.dsBm.bmHeight, stride = ds.dsBm.bmWidthBytes;
        var buf = new byte[stride * h];
        Marshal.Copy(ds.dsBm.bmBits, buf, 0, buf.Length);
        if (ds.dsBmih.biHeight > 0) // bottom-up DIB → flip rows
        {
            var row = new byte[stride];
            for (int y = 0; y < h / 2; y++)
            {
                Buffer.BlockCopy(buf, y * stride, row, 0, stride);
                Buffer.BlockCopy(buf, (h - 1 - y) * stride, buf, y * stride, stride);
                Buffer.BlockCopy(row, 0, buf, (h - 1 - y) * stride, stride);
            }
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, buf, stride);
        bmp.Freeze();
        return bmp;
    }

    public static string GetDisplayName(string parsingName)
    {
        try
        {
            if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref IID_IShellItem, out object item) == 0)
            {
                var si = (IShellItem)item;
                string name = null;
                if (si.GetDisplayName(0 /* SIGDN_NORMALDISPLAY */, out IntPtr p) == 0)
                {
                    name = Marshal.PtrToStringUni(p);
                    Marshal.FreeCoTaskMem(p);
                }
                Marshal.ReleaseComObject(item);
                return name;
            }
        }
        catch { }
        return null;
    }

    public static (string target, string args) ResolveShortcut(string lnk)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            dynamic sh = Activator.CreateInstance(t);
            dynamic sc = sh.CreateShortcut(lnk);
            string target = sc.TargetPath, args = sc.Arguments;
            Marshal.ReleaseComObject(sc); Marshal.ReleaseComObject(sh);
            return (target ?? "", args ?? "");
        }
        catch { return ("", ""); }
    }

    public static void Launch(string path, string args = null)
    {
        try
        {
            if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || path.StartsWith("::"))
                Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(path, args ?? "") { UseShellExecute = true, WorkingDirectory = SafeDir(path) });
        }
        catch (Exception ex) { Log.Write("launch " + path + ": " + ex.Message); }
    }

    static string SafeDir(string path)
    {
        try { var d = System.IO.Path.GetDirectoryName(path); return System.IO.Directory.Exists(d) ? d : ""; } catch { return ""; }
    }

    public static void ShowInExplorer(string path)
    {
        try { Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { }
    }
}

internal static class Log
{
    static readonly string file = System.IO.Path.Combine(Config.Dir, "harbor.log");
    public static void Write(string msg)
    {
        try { System.IO.File.AppendAllText(file, DateTime.Now.ToString("s") + "  " + msg + Environment.NewLine); } catch { }
    }
}
