using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using static Harbor.Native;

namespace Harbor;

/// <summary>A tiny notification-area host. Apps find the tray with FindWindow("Shell_TrayWnd") and send it their icons via
/// WM_COPYDATA, so Harbor registers its own (invisible) Shell_TrayWnd above Explorer's, keeps a copy of every icon and
/// forwards each message to Explorer unchanged, so Explorer's tray, toasts and appbars keep working as before.</summary>
internal static class TrayHost
{
    public class TrayIcon
    {
        public IntPtr Owner; public uint Id; public Guid Guid;
        public uint CallbackMessage, Version;
        public ImageSource Icon; public string Tip = "";
        public bool Hidden;
    }

    public static readonly List<TrayIcon> Icons = new();
    public static event Action Changed;
    public static bool Running => hwnd != IntPtr.Zero;

    const string ClassName = "Shell_TrayWnd";
    const uint WM_COPYDATA = 0x004A, WM_COMMAND = 0x0111, WM_USER = 0x0400;
    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_STATE = 8, NIF_GUID = 0x20;
    const uint NIS_HIDDEN = 1;
    const int TraySignature = 0x34753423;

    static readonly bool Diag = Environment.GetEnvironmentVariable("HARBOR_DIAG") == "1";
    static IntPtr hwnd;
    static WndProcDelegate proc; // kept alive for the lifetime of the window
    static DispatcherTimer keepOnTop;
    static uint taskbarCreated;

    public static void Start()
    {
        if (hwnd != IntPtr.Zero) return;
        proc = WndProc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = GetModuleHandleW(null), lpszClassName = ClassName
        };
        if (RegisterClassExW(ref wc) == 0) { Log.Write("tray: RegisterClass failed " + Marshal.GetLastWin32Error()); return; }
        hwnd = CreateWindowExW(WS_EX_TOOLWINDOW_U | WS_EX_TOPMOST | WS_EX_NOACTIVATE_U, ClassName, "", WS_POPUP, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) { Log.Write("tray: CreateWindow failed " + Marshal.GetLastWin32Error()); UnregisterClassW(ClassName, wc.hInstance); return; }

        taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        BringToTop();
        // Explorer can end up above us again (e.g. after it shows its own taskbar); FindWindow returns the topmost match.
        keepOnTop = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        keepOnTop.Tick += (_, _) => { BringToTop(); Prune(); };
        keepOnTop.Start();
        // ask every running app to (re-)add its icons; they now reach us first
        AnnounceTaskbarCreated();
        // some apps are slow to answer, or only listen once their UI is idle → ask once more a little later
        var again = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        again.Tick += (_, _) => { again.Stop(); if (hwnd != IntPtr.Zero) AnnounceTaskbarCreated(); };
        again.Start();
    }

    /// <summary>Broadcasts "TaskbarCreated". HWND_BROADCAST only reaches top-level windows, but many apps own their
    /// tray icon from a hidden message-only window (WhatsApp, NVIDIA, download managers…), so post to those too.</summary>
    static void AnnounceTaskbarCreated()
    {
        SendNotifyMessageW((IntPtr)0xFFFF, taskbarCreated, IntPtr.Zero, IntPtr.Zero);
        uint self = (uint)Environment.ProcessId;
        IntPtr child = IntPtr.Zero;
        for (int guard = 0; guard < 5000; guard++)
        {
            child = FindWindowExW((IntPtr)(-3) /* HWND_MESSAGE */, child, null, null);
            if (child == IntPtr.Zero) break;
            GetWindowThreadProcessId(child, out uint pid);
            if (pid != self) PostMessageW(child, taskbarCreated, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public static void Stop()
    {
        if (hwnd == IntPtr.Zero) return;
        keepOnTop?.Stop();
        DestroyWindow(hwnd);
        UnregisterClassW(ClassName, GetModuleHandleW(null));
        hwnd = IntPtr.Zero;
        Icons.Clear();
        Changed?.Invoke();
    }

    /// <summary>Sends an appbar message straight to Explorer: our tray window steps below Explorer's for the call, so
    /// shell32's FindWindow("Shell_TrayWnd") reaches Explorer directly (it ignores appbars that arrive via us).</summary>
    public static IntPtr AppBarMessage(uint msg, ref APPBARDATA abd)
    {
        if (hwnd == IntPtr.Zero) return SHAppBarMessage(msg, ref abd);
        SetWindowPos(hwnd, (IntPtr)(-2) /* HWND_NOTOPMOST */, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        SetWindowPos(hwnd, (IntPtr)1 /* HWND_BOTTOM */, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        try { return SHAppBarMessage(msg, ref abd); }
        finally { BringToTop(); }
    }

    public static bool IsOwnWindow(IntPtr h) => h != IntPtr.Zero && h == hwnd;

    static void BringToTop()
    {
        if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, (IntPtr)(-1) /* HWND_TOPMOST */, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Explorer's real tray window (never ours).</summary>
    static IntPtr ExplorerTray()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (h != hwnd && GetClassName(h) == ClassName) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static IntPtr Forward(uint msg, IntPtr w, IntPtr l)
    {
        var target = ExplorerTray();
        if (target == IntPtr.Zero) return IntPtr.Zero;
        SendMessageTimeoutW(target, msg, w, l, 0x2 /* SMTO_ABORTIFHUNG */, 3000, out var result);
        return result;
    }

    static IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        try
        {
            if (msg == WM_COPYDATA)
            {
                var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(l);
                if (cds.dwData == (IntPtr)1 && cds.lpData != IntPtr.Zero && cds.cbData >= 8 + 24)
                {
                    bool ok = HandleNotify(cds.lpData, cds.cbData);
                    Forward(msg, w, l);
                    return ok ? (IntPtr)1 : IntPtr.Zero;
                }
                return Forward(msg, w, l); // appbar messages (0), icon-rect queries (3), …
            }
            if (msg == WM_COMMAND || (msg >= WM_USER && msg < 0x8000)) return Forward(msg, w, l);
        }
        catch (Exception ex) { Log.Write("tray: " + ex.Message); }
        return DefWindowProcW(h, msg, w, l);
    }

    // SHELLTRAYDATA { int signature; uint message; NOTIFYICONDATA32 nid }. Handles are stored as 32-bit values.
    static bool HandleNotify(IntPtr p, int size)
    {
        if (Marshal.ReadInt32(p) != TraySignature) return false;
        uint message = (uint)Marshal.ReadInt32(p, 4);
        IntPtr n = p + 8; int nSize = size - 8;
        uint U(int off) => off + 4 <= nSize ? (uint)Marshal.ReadInt32(n, off) : 0;

        var owner = (IntPtr)(int)U(4);
        uint id = U(8), flags = U(12);
        Guid guid = Guid.Empty;
        if ((flags & NIF_GUID) != 0 && nSize >= 952) { var g = new byte[16]; Marshal.Copy(n + 936, g, 0, 16); guid = new Guid(g); }

        var icon = Icons.FirstOrDefault(i => guid != Guid.Empty ? i.Guid == guid : i.Owner == owner && i.Id == id);
        if (Diag)
        {
            GetWindowThreadProcessId(owner, out uint dpid);
            Log.Write($"tray msg={message} known={icon != null} owner=0x{(long)owner:X} exe={System.IO.Path.GetFileName(GetProcessPath(dpid) ?? "?")} id={id} flags=0x{flags:X} guid={guid} size={size}");
        }
        switch (message)
        {
            case NIM_ADD:
            case NIM_MODIFY:
                if (icon == null)
                {
                    // An update for an icon we never saw being added: it was added before Harbor started (many apps,
                    // e.g. Windows Security, answer "TaskbarCreated" with a modify). Adopt it if it carries an icon.
                    if (message == NIM_MODIFY && ((flags & NIF_ICON) == 0 || owner == IntPtr.Zero)) return false;
                    icon = new TrayIcon { Owner = owner, Id = id, Guid = guid };
                    Icons.Add(icon);
                }
                // GUID-identified icons may leave hWnd empty in later updates (allowed by the API; WhatsApp does it).
                // Keep the window we already know, otherwise the icon looks orphaned and gets pruned.
                if (owner != IntPtr.Zero) icon.Owner = owner;
                if ((flags & NIF_MESSAGE) != 0) icon.CallbackMessage = U(16);
                if ((flags & NIF_ICON) != 0) icon.Icon = ToImage((IntPtr)(int)U(20)) ?? icon.Icon;
                if ((flags & NIF_TIP) != 0) icon.Tip = Marshal.PtrToStringUni(n + 24, 128)?.Split('\0')[0].Trim() ?? "";
                if ((flags & NIF_STATE) != 0)
                {
                    uint state = U(280), mask = U(284);
                    if ((mask & NIS_HIDDEN) != 0) icon.Hidden = (state & NIS_HIDDEN) != 0;
                }
                break;
            case NIM_DELETE:
                if (icon == null) return false;
                Icons.Remove(icon);
                break;
            case NIM_SETVERSION:
                if (icon == null) return false;
                icon.Version = U(800);
                return true;
            default:
                return true;
        }
        Changed?.Invoke();
        return true;
    }

    static ImageSource ToImage(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
    }

    /// <summary>Drops icons whose app quit without removing them.</summary>
    static void Prune()
    {
        if (Diag) foreach (var i in Icons.Where(i => !IsWindow(i.Owner))) Log.Write($"tray prune: owner 0x{(long)i.Owner:X} gone, id={i.Id} tip={i.Tip}");
        if (Icons.RemoveAll(i => !IsWindow(i.Owner)) > 0) Changed?.Invoke();
    }

    // ───────────── talking back to the app (same messages Explorer sends) ─────────────

    static readonly Guid VolumeGuid = new("7820ae73-23e3-4229-82c1-e41cb67d5b9c");

    public static void Click(TrayIcon ic, bool right, bool dbl = false)
    {
        if (Diag) Log.Write($"tray click right={right} dbl={dbl} owner=0x{(long)ic.Owner:X} alive={IsWindow(ic.Owner)} v={ic.Version} cb=0x{ic.CallbackMessage:X} tip={ic.Tip}");
        if (!IsWindow(ic.Owner)) { Prune(); return; }
        GetWindowThreadProcessId(ic.Owner, out uint pid);
        // Windows' own tray icons (volume, network…) belong to Explorer and ignore forwarded clicks, so open the
        // matching panel ourselves: Quick Settings has the volume slider; right-click volume → Sound settings.
        if (string.Equals(System.IO.Path.GetFileName(GetProcessPath(pid) ?? ""), "explorer.exe", StringComparison.OrdinalIgnoreCase))
        {
            if (right && ic.Guid == VolumeGuid) Shell.Launch("ms-settings:sound");
            else PressKeys(VK_LWIN, 0x41 /* A */);
            return;
        }
        // Icons adopted from an update (added before Harbor started) may lack the callback message, so clicks can't be
        // forwarded. Do the obvious thing instead: open the app.
        if (ic.CallbackMessage == 0)
        {
            string exe = System.IO.Path.GetFileName(GetProcessPath(pid) ?? "");
            if (exe.Equals("SecurityHealthSystray.exe", StringComparison.OrdinalIgnoreCase)) { Shell.Launch("windowsdefender:"); return; }
            var win = WindowTracker.Enumerate().FirstOrDefault(w => { GetWindowThreadProcessId(w.Hwnd, out uint p); return p == pid; });
            if (win != null) ActivateWindow(win.Hwnd);
            return;
        }
        AllowSetForegroundWindow(pid);
        if (right) { Send(ic, 0x0204); Send(ic, 0x0205); if (ic.Version >= 4) Send(ic, 0x007B /* WM_CONTEXTMENU */); }
        else if (dbl) { Send(ic, 0x0203); Send(ic, 0x0202); }
        else { Send(ic, 0x0201); Send(ic, 0x0202); if (ic.Version >= 4) Send(ic, 0x0400 /* NIN_SELECT */); }
    }

    public static void Hover(TrayIcon ic) { if (IsWindow(ic.Owner)) Send(ic, 0x0200); }

    static void Send(TrayIcon ic, uint mouseMsg)
    {
        if (ic.CallbackMessage == 0) return;
        IntPtr w, l;
        if (ic.Version >= 4)
        {
            GetCursorPos(out var pt);
            w = (IntPtr)(((pt.Y & 0xFFFF) << 16) | (pt.X & 0xFFFF));
            l = (IntPtr)(((ic.Id & 0xFFFF) << 16) | (mouseMsg & 0xFFFF));
        }
        else { w = (IntPtr)ic.Id; l = (IntPtr)mouseMsg; }
        PostMessageW(ic.Owner, ic.CallbackMessage, w, l);
    }
}
