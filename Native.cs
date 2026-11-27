using System.Runtime.InteropServices;
using System.Text;

namespace Harbor;

internal static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TRANSPARENT = 0x20, WS_EX_APPWINDOW = 0x40000;
    public const uint GW_OWNER = 4, GA_ROOT = 2;
    public const int SW_RESTORE = 9, SW_MINIMIZE = 6;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
    public const uint WM_CLOSE = 0x10;
    public const byte VK_LWIN = 0x5B, VK_MENU = 0x12;
    public const uint KEYEVENTF_KEYUP = 2;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);

    public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern int GetWindowTextLengthW(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    public delegate IntPtr LowLevelProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }
    [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookExW(int id, LowLevelProc proc, IntPtr hMod, uint thread);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string name);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessageW(string s);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] public static extern int GetObject(IntPtr h, int size, out DIBSECTION ds);

    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
    public const int DWMWA_CLOAKED = 14;

    [StructLayout(LayoutKind.Sequential)]
    public struct AccentPolicy { public int AccentState; public int AccentFlags; public uint GradientColor; public int AnimationId; }
    [StructLayout(LayoutKind.Sequential)]
    public struct WindowCompositionAttribData { public int Attribute; public IntPtr Data; public int SizeOfData; }
    [DllImport("user32.dll")] public static extern int SetWindowCompositionAttribute(IntPtr h, ref WindowCompositionAttribData d);

    public static void EnableAcrylic(IntPtr hwnd, uint abgr, int state = 4 /* acrylic */)
    {
        var accent = new AccentPolicy { AccentState = state, AccentFlags = 2, GradientColor = abgr };
        int size = Marshal.SizeOf(accent);
        IntPtr p = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, p, false);
            var data = new WindowCompositionAttribData { Attribute = 19, Data = p, SizeOfData = size };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder sb, ref int size);
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public delegate void WinEventDelegate(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventDelegate cb, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr h);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public RECT rc; public IntPtr lParam; }
    [DllImport("shell32.dll")] public static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA d);
    public const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABM_GETSTATE = 4, ABM_SETSTATE = 10;
    public const uint ABE_BOTTOM = 3, ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;

    [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong total, avail, totalPage, availPage, totalVirt, availVirt, availExt; }
    [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int LifeTime, FullLifeTime; }
    [DllImport("kernel32.dll")] public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

    [StructLayout(LayoutKind.Sequential)] public struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHQueryRecycleBinW(string root, ref SHQUERYRBINFO info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHEmptyRecycleBinW(IntPtr h, string root, uint flags);

    // ---- Shell items / icons ----
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }
    [StructLayout(LayoutKind.Sequential)]
    public struct DIBSECTION { public BITMAP dsBm; public BITMAPINFOHEADER dsBmih; public uint b0, b1, b2; public IntPtr dshSection; public uint dsOffset; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm); }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(uint sigdn, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint mask, out uint attribs);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int order);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    public static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object item);

    [StructLayout(LayoutKind.Sequential)] public struct PROPERTYKEY { public Guid fmtid; public uint pid; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] public struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint c);
        [PreserveSig] int GetAt(uint i, out PROPERTYKEY k);
        [PreserveSig] int GetValue(ref PROPERTYKEY k, out PROPVARIANT v);
        [PreserveSig] int SetValue(ref PROPERTYKEY k, ref PROPVARIANT v);
        [PreserveSig] int Commit();
    }
    [DllImport("shell32.dll")] public static extern int SHGetPropertyStoreForWindow(IntPtr h, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr pbc, int flags, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] public static extern int PropVariantClear(ref PROPVARIANT v);

    static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    static PROPERTYKEY PKEY_AUMID = new() { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };

    static string ReadAumid(IPropertyStore store)
    {
        if (store == null) return null;
        try
        {
            if (store.GetValue(ref PKEY_AUMID, out var pv) != 0) return null;
            string s = pv.vt == 31 ? Marshal.PtrToStringUni(pv.p) : null;
            PropVariantClear(ref pv);
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    public static string GetWindowAumid(IntPtr hwnd)
    {
        var iid = IID_IPropertyStore;
        try { return SHGetPropertyStoreForWindow(hwnd, ref iid, out var st) == 0 ? ReadAumid(st) : null; } catch { return null; }
    }

    public static string GetFileAumid(string path)
    {
        var iid = IID_IPropertyStore;
        try { return SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out var st) == 0 ? ReadAumid(st) : null; } catch { return null; }
    }

    public static string GetWindowTitle(IntPtr h)
    {
        int len = GetWindowTextLengthW(h);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string GetClassName(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassNameW(h, sb, 256);
        return sb.ToString();
    }

    public static string GetProcessPath(uint pid)
    {
        IntPtr hp = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hp == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024); int size = sb.Capacity;
            return QueryFullProcessImageNameW(hp, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(hp); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetApplicationUserModelId(IntPtr hProcess, ref int len, StringBuilder sb);
    [DllImport("psapi.dll")] public static extern bool EmptyWorkingSet(IntPtr hProcess);

    /// <summary>AUMID of a packaged (Store/MSIX) process, e.g. WhatsApp, Spotify, Photos.</summary>
    public static string GetProcessAumid(uint pid)
    {
        IntPtr hp = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hp == IntPtr.Zero) return null;
        try
        {
            int len = 512; var sb = new StringBuilder(len);
            return GetApplicationUserModelId(hp, ref len, sb) == 0 ? sb.ToString() : null;
        }
        finally { CloseHandle(hp); }
    }

    // ---- Raw windows (tray host) ----
    public const uint WS_POPUP = 0x80000000, WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW_U = 0x80, WS_EX_NOACTIVATE_U = 0x08000000;
    public delegate IntPtr WndProcDelegate(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName; public IntPtr hIconSm;
    }
    [StructLayout(LayoutKind.Sequential)] public struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool UnregisterClassW(string cls, IntPtr hInst);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeoutW(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] public static extern bool SendNotifyMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);

    // ---- Monitors ----
    delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfoW(IntPtr hMon, ref MONITORINFOEX mi);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr hMon, int type, out uint dx, out uint dy);

    public class MonitorInfo { public string Device; public RECT Bounds; public bool Primary; public double Dpi = 1; }

    public static List<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfoW(h, ref mi))
            {
                double dpi = GetDpiForMonitor(h, 0, out uint dx, out _) == 0 ? dx / 96.0 : 1;
                list.Add(new MonitorInfo { Device = mi.szDevice, Bounds = mi.rcMonitor, Primary = (mi.dwFlags & 1) != 0, Dpi = dpi });
            }
            return true;
        }, IntPtr.Zero);
        return list.OrderByDescending(m => m.Primary).ToList();
    }

    public static void PressKeys(params byte[] keys)
    {
        foreach (var k in keys) keybd_event(k, 0, 0, UIntPtr.Zero);
        for (int i = keys.Length - 1; i >= 0; i--) keybd_event(keys[i], 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] static extern void SwitchToThisWindow(IntPtr h, bool altTab);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    /// <summary>Brings a window to the front reliably. Windows' foreground lock often refuses a plain
    /// SetForegroundWindow from a no-activate dock (it failed ~50% of the time in testing), so: join the current
    /// foreground thread's input queue for the call (which makes us "the foreground app" for that moment), and as a
    /// last resort use the same switch Alt+Tab uses. No synthetic Alt press: that could pop open the target's menu bar.</summary>
    public static void ActivateWindow(IntPtr h)
    {
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        if (SetForegroundWindow(h) && GetForegroundWindow() == h) return;

        IntPtr fg = GetForegroundWindow();
        uint me = GetCurrentThreadId(), fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        try
        {
            BringWindowToTop(h);
            SetForegroundWindow(h);
        }
        finally { if (attached) AttachThreadInput(me, fgThread, false); }

        if (GetForegroundWindow() != h) SwitchToThisWindow(h, true);
    }
}
