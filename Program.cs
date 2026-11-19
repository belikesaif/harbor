using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using static Harbor.Native;

namespace Harbor;

/// <summary>The frosted-glass layer behind the dock. A separate window so the blur can be clipped to a rounded shape
/// while icons are free to magnify above it.</summary>
public class GlassWindow : Window
{
    IntPtr hwnd;
    public IntPtr Handle => hwnd;
    int lw, lh, lr;

    public GlassWindow()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        ShowInTaskbar = false; Topmost = true; ShowActivated = false;
        Left = -200; Top = -200; Width = 10; Height = 10;
        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            long ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)((ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT) & ~WS_EX_APPWINDOW));
            EnableAcrylic(hwnd, 0x66221E1E); // AABBGGRR: graphite tint
        };
    }

    public void Place(int x, int y, int w, int h, int radius)
    {
        if (hwnd == IntPtr.Zero || w <= 0 || h <= 0) return;
        SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        if (w != lw || h != lh || radius != lr)
        {
            SetWindowRgn(hwnd, CreateRoundRectRgn(0, 0, w + 1, h + 1, radius * 2, radius * 2), true);
            lw = w; lh = h; lr = radius;
        }
    }
}

public static class Program
{
    static Config cfg;
    static readonly List<DockWindow> docks = new();
    static bool cleaned;
    // Windows sends a burst of display-change notifications while a monitor is (un)plugged; wait for it to settle.
    static readonly System.Windows.Threading.DispatcherTimer displayDebounce = new() { Interval = TimeSpan.FromMilliseconds(800) };

    static void OnDisplaySettingsChanged(object s, EventArgs e) =>
        displayDebounce.Dispatcher.BeginInvoke(() => { if (cleaned) return; displayDebounce.Stop(); displayDebounce.Start(); });

    /// <summary>Removes docks whose monitor was disconnected and adds docks for newly connected monitors.</summary>
    static void SyncDocks()
    {
        if (cleaned) return;
        var monitors = Native.GetMonitors();
        if (monitors.Count == 0) return; // transient state during a display switch

        foreach (var d in docks.Where(d => !monitors.Any(m => m.Device == d.MonitorDevice)).ToList())
        {
            Log.Write("monitor removed: " + d.MonitorDevice);
            docks.Remove(d);
            try { d.Cleanup(); } catch (Exception ex) { Log.Write("dock cleanup: " + ex); }
            try { d.Close(); d.Glass.Close(); } catch (Exception ex) { Log.Write("dock close: " + ex); }
        }

        foreach (var m in monitors.Where(m => !docks.Any(d => d.MonitorDevice == m.Device)))
        {
            if (docks.Count > 0) Log.Write("monitor added: " + m.Device);
            var glass = new GlassWindow();
            glass.Show();
            var dock = new DockWindow(cfg, glass, m) { Owner = glass };
            dock.Show();
            docks.Add(dock);
        }

        TaskbarControl.EnforceHidden(); // Explorer may recreate secondary taskbars on a display change
    }

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Any(a => a.Equals("--restore-taskbar", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("Harbor"))
                if (p.Id != Environment.ProcessId) { try { p.Kill(); p.WaitForExit(2000); } catch { } }
            TaskbarControl.ForceRestore();
            return;
        }

        using var mutex = new Mutex(true, "Harbor.Dock.SingleInstance", out bool first);
        if (!first) return;

        // Layered (per-pixel transparent) windows force WPF to read every GPU frame back to system memory.
        // For a small dock surface the software rasterizer is cheaper in both CPU and RAM.
        if (Environment.GetEnvironmentVariable("HARBOR_GPU") != "1")
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/Harbor;component/Theme.xaml", UriKind.Relative)));
        app.DispatcherUnhandledException += (_, e) => { Log.Write("UI error: " + e.Exception); e.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { Log.Write("fatal: " + e.ExceptionObject); Cleanup(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
        app.SessionEnding += (_, _) => Cleanup();

        cfg = Config.Load();
        TaskbarControl.Apply(cfg);

        // one dock per monitor, kept in sync as monitors are connected / disconnected
        SyncDocks();
        displayDebounce.Tick += (_, _) => { displayDebounce.Stop(); SyncDocks(); };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        app.Run();
        Cleanup();
    }

    public static void Quit()
    {
        Cleanup();
        Application.Current.Shutdown();
    }

    static void Cleanup()
    {
        if (cleaned) return;
        cleaned = true;
        try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; } catch { }
        foreach (var d in docks) { try { d.Dispatcher.Invoke(d.Cleanup); } catch { } }
        try { TaskbarControl.Restore(cfg); } catch { }
    }
}
