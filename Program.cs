using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using static Harbor.Native;

namespace Harbor;

public static class Program
{
    static Config cfg;
    static readonly List<DockWindow> docks = new();
    static readonly List<TopBar> bars = new();
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
            var dock = new DockWindow(cfg, glass, m); // the dock makes itself owned by the glass → always drawn above it
            dock.Show();
            docks.Add(dock);
        }

        foreach (var b in bars.Where(b => !cfg.ShowTopBar || !monitors.Any(m => m.Device == b.MonitorDevice)).ToList())
        {
            bars.Remove(b);
            try { b.Cleanup(); b.Close(); } catch (Exception ex) { Log.Write("top bar close: " + ex); }
        }
        if (cfg.ShowTopBar)
            foreach (var m in monitors.Where(m => !bars.Any(b => b.MonitorDevice == m.Device)))
            {
                var bar = new TopBar(m);
                bar.Init();
                bars.Add(bar);
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

        if (cfg.ShowTopBar) TrayHost.Start();

        // one dock (and top bar) per monitor, kept in sync as monitors are connected / disconnected
        SyncDocks();
        displayDebounce.Tick += (_, _) => { displayDebounce.Stop(); SyncDocks(); };

        // follow "Pin to taskbar" / "Unpin from taskbar" from the Start menu (the Windows taskbar itself is hidden)
        if (cfg.SyncTaskbarPins()) foreach (var d in docks) d.ReloadPinsFromConfig();
        var pinWatch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        pinWatch.Tick += (_, _) =>
        {
            if (cleaned) { pinWatch.Stop(); return; }
            if (cfg.SyncTaskbarPins()) foreach (var d in docks) d.ReloadPinsFromConfig();
        };
        pinWatch.Start();

        StartFullscreenWatch();

        Hotkeys.OnPin = i => docks.FirstOrDefault()?.ActivatePin(i);
        Hotkeys.Start();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        app.Run();
        Cleanup();
    }

    /// <summary>Turns the top bar (and the tray host behind it) on or off after the setting changed.</summary>
    public static void ApplyTopBar()
    {
        if (cfg.ShowTopBar) TrayHost.Start(); else TrayHost.Stop();
        SyncDocks();
    }

    // ───── fullscreen detection ─────
    // Windows' ABN_FULLSCREENAPP notification never arrived in testing (the dock stayed on top of a fullscreen
    // window), so Harbor detects it itself: the foreground window covers its whole monitor and isn't just maximized.
    static WinEventDelegate fsProc;
    static readonly List<IntPtr> fsHooks = new();
    static readonly System.Windows.Threading.DispatcherTimer fsTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    static bool fsQueued;

    static void StartFullscreenWatch()
    {
        fsProc = (hook, ev, h, idObject, idChild, thread, time) =>
        {
            if (idObject != 0) return;
            if (ev == 0x800B /* LOCATIONCHANGE */ && h != GetForegroundWindow()) return;
            if (fsQueued) return;
            fsQueued = true;
            Application.Current?.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => { fsQueued = false; CheckFullscreen(); });
        };
        fsHooks.Add(SetWinEventHook(0x0003, 0x0003, IntPtr.Zero, fsProc, 0, 0, 2)); // foreground changed
        fsHooks.Add(SetWinEventHook(0x800B, 0x800B, IntPtr.Zero, fsProc, 0, 0, 2)); // a window moved/resized (F11)
        fsTimer.Tick += (_, _) => CheckFullscreen();
        fsTimer.Start();
    }

    static void StopFullscreenWatch()
    {
        fsTimer.Stop();
        foreach (var h in fsHooks) if (h != IntPtr.Zero) UnhookWinEvent(h);
        fsHooks.Clear();
    }

    static readonly HashSet<string> shellClasses = new(StringComparer.Ordinal)
        { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "ControlCenterWindow", "TopLevelWindowForOverflowXamlIsland" };

    static void CheckFullscreen()
    {
        if (cleaned) return;
        var fg = GetForegroundWindow();
        RECT r = default;
        bool candidate = fg != IntPtr.Zero && !shellClasses.Contains(GetClassName(fg)) && GetWindowRect(fg, out r);
        if (candidate)
        {
            GetWindowThreadProcessId(fg, out uint pid);
            long style = (long)GetWindowLongPtr(fg, -16 /* GWL_STYLE */);
            const long WS_CAPTION = 0x00C00000;
            // a maximized window can also cover the screen (with auto-hide on) but isn't fullscreen; borderless
            // windows count even when maximized, and plenty of real fullscreen apps keep caption styles
            // …but with auto-hide on, a maximized window covers the whole screen anyway (and many apps, e.g. Electron
            // ones, draw their own title bar), so then only non-maximized windows count — else the dock would vanish.
            candidate = pid != (uint)Environment.ProcessId && (!IsZoomed(fg) || ((style & WS_CAPTION) != WS_CAPTION && !cfg.AutoHide));
        }
        foreach (var d in docks) d.SetFullscreen(candidate && Covers(r, d.Monitor.Bounds));
        foreach (var b in bars) b.SetFullscreen(candidate && Covers(r, b.Monitor.Bounds));
    }

    static bool Covers(RECT r, RECT b) => r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;

    /// <summary>Applies the auto-hide setting to the dock on every monitor.</summary>
    public static void ApplyAutoHide()
    {
        foreach (var d in docks) d.OnAutoHideChanged();
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
        foreach (var b in bars) { try { b.Dispatcher.Invoke(b.Cleanup); } catch { } }
        try { Application.Current?.Dispatcher.Invoke(TrayHost.Stop); } catch { }
        try { Hotkeys.Stop(); } catch { }
        try { StopFullscreenWatch(); } catch { }
        try { TaskbarControl.Restore(cfg); } catch { }
    }
}
