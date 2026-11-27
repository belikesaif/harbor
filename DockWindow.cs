using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using static Harbor.Native;

namespace Harbor;

/// <summary>One slot in the dock: an app icon, a widget, or a separator.</summary>
internal class DockCell : Grid
{
    public readonly string Kind;
    public DockEntry Entry;
    public double Aspect = 1, Scale = 1, Target = 1;
    public readonly Border Host;
    public readonly Ellipse Dot;
    public readonly TranslateTransform Lift = new();
    public Func<string> Label;
    public Action Click;
    public Action<DockCell> RightClick;

    public DockCell(string kind, FrameworkElement content)
    {
        Kind = kind;
        VerticalAlignment = VerticalAlignment.Bottom;
        Background = Brushes.Transparent;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(DockWindow.PadFor(54)) });
        Host = new Border { Child = content, VerticalAlignment = VerticalAlignment.Bottom, RenderTransform = Lift };
        Children.Add(Host);
        // macOS-style running indicator: a small, soft dot centred in the bottom padding
        Dot = new Ellipse { Width = 4, Height = 4, Fill = Ui.B(0xCCFFFFFF), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Hidden, IsHitTestVisible = false };
        SetRow(Dot, 1);
        Children.Add(Dot);
    }

    public double BaseWidth(double b) => b * Aspect + 2 * Margin_(b);
    /// <summary>Half the gap between two icons (≈ 6 px gap at 54 px icons, like the macOS Dock).</summary>
    public static double Margin_(double b) => Math.Round(b * 0.06, 1);

    double lastPad = -1;
    public void ApplySize(double b)
    {
        double s = Kind == "sep" ? 1 : Scale;
        double m = Margin_(b);
        Margin = new Thickness(m, 0, m, 0);
        Width = Math.Max(0, b * Aspect * s);
        Host.Height = Math.Max(0, b * s);
        double pad = DockWindow.PadFor(b);
        if (pad != lastPad)
        {
            lastPad = pad;
            RowDefinitions[1].Height = new GridLength(pad);
            double d = Math.Clamp(Math.Round(b * 0.075), 4, 6);
            Dot.Width = Dot.Height = d;
        }
    }
}

internal class DockWindow : Window
{
    /// <summary>Space between the screen edge and the floating bar.</summary>
    public const double Gap = 6;
    /// <summary>Vertical padding above and below the icons: equal on both sides so icons sit visually centred
    /// (the running dot lives inside the bottom padding, exactly like macOS).</summary>
    public static double PadFor(double b) => Math.Round(Math.Max(5, b * 0.14));
    double PadV => PadFor(Base);
    /// <summary>Horizontal bar padding, chosen so the outer icons have the same breathing room as top/bottom.</summary>
    double Pad => Math.Max(2, PadV - DockCell.Margin_(Base));
    readonly Config cfg;
    readonly GlassWindow glass;
    readonly string monitorDevice;
    MonitorInfo mon;
    IntPtr hwnd;
    double dpi = 1;

    readonly Grid outer = new(), root = new();
    readonly Border bar = new(), sheen = new();
    readonly Border[] shadow = new Border[3];
    readonly StackPanel stack = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
    readonly Canvas overlay = new() { IsHitTestVisible = false };
    readonly FrameworkElement label;
    readonly TextBlock labelText;
    readonly TranslateTransform slide = new();
    // auto-hide: a 2 px invisible strip along the screen edge reveals the dock, like macOS
    readonly Border hotZone = new() { Height = 2, VerticalAlignment = VerticalAlignment.Bottom, Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
    readonly DispatcherTimer hideTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    readonly DispatcherTimer revealTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    // WPF doesn't reliably report "mouse left" for this no-activate window when the pointer leaves fast, which left
    // icons stuck magnified. While the pointer is (or was) over the dock, verify its real position 10×/s.
    readonly DispatcherTimer leaveWatch = new() { Interval = TimeSpan.FromMilliseconds(100) };
    double hideAmt, hideTarget; // 0 = shown, 1 = fully hidden
    bool menuOpen;
    bool AutoHide => cfg.AutoHide;

    readonly List<DockEntry> pinned = new();
    readonly Dictionary<string, DockEntry> running = new();
    readonly Dictionary<DockEntry, DockCell> appCells = new();
    readonly List<DockCell> cells = new();

    DockCell launchCell, sep1, sep2, sep3, calCell, clockCell, digiCell, sysCell, mediaCell, ccCell, dlCell, trashCell;
    CalendarTile cal; ClockTile clock; DigitalClockTile digi; SystemWidget sys; MediaWidget media;
    long lastBinCount = -1;

    readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(180) };
    int tickCount;
    WinEventDelegate winEventProc;
    readonly List<IntPtr> hooks = new();
    uint appBarMsg, taskbarCreatedMsg;
    bool appBarRegistered, built, fullscreen;
    int bottomPx;

    DockCell hovered, pressCell;
    Point pressPt;
    bool dragging, animating;
    TimeSpan lastFrame;

    double effBase = -1;
    double Base => effBase > 0 ? effBase : cfg.IconSize;

    /// <summary>Shrinks icons automatically (like macOS) when the dock would be wider than the screen.</summary>
    void FitToScreen(List<DockCell> want)
    {
        var m = CurrentMonitor();
        double avail = (m.Bounds.Right - m.Bounds.Left) / m.Dpi * 0.96 - 2 * Pad;
        // leave headroom for magnification: the raised-cosine bump adds ≈ (mag-1)·range of extra width
        double units = want.Sum(c => c.Aspect + 0.11) + Math.Max(0, cfg.Magnification - 1) * 2.8;
        double nb = Math.Min(cfg.IconSize, Math.Max(30, Math.Floor(avail / units)));
        if (Math.Abs(nb - effBase) < 0.5) return;
        effBase = nb;
        ApplySizes();
        if (appBarRegistered) SetAppBarPos();
    }
    double BarH => Base + 2 * PadV;

    public string MonitorDevice => monitorDevice;
    public GlassWindow Glass => glass;

    /// <summary>Looks this dock's monitor up again (resolution/DPI may have changed). If the monitor is gone we keep
    /// the last known geometry instead of jumping to another screen; Program removes docks for disconnected monitors.</summary>
    MonitorInfo CurrentMonitor()
    {
        var m = GetMonitors().FirstOrDefault(x => x.Device == monitorDevice);
        if (m != null) mon = m;
        return mon;
    }

    public DockWindow(Config cfg, GlassWindow glass, MonitorInfo monitor)
    {
        this.cfg = cfg; this.glass = glass; mon = monitor; monitorDevice = monitor.Device;
        Title = "Harbor";
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true; ShowActivated = false;
        Left = 0; Top = -2000; Width = 800; Height = 200;
        UseLayoutRounding = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        // Soft contact shadow + dark outer hairline, drawn as thin rings *outside* the bar only, so the frosted
        // glass itself never gets darker. Plain strokes cost nothing compared to a blur effect.
        uint[] shadowAlpha = { 0x47000000, 0x1C000000, 0x0B000000 };
        for (int i = 0; i < shadow.Length; i++)
        {
            shadow[i] = new Border { VerticalAlignment = VerticalAlignment.Bottom, BorderThickness = new Thickness(1), BorderBrush = Ui.B(shadowAlpha[i]), IsHitTestVisible = false };
            root.Children.Add(shadow[i]);
        }
        bar.VerticalAlignment = VerticalAlignment.Bottom;
        bar.BorderThickness = new Thickness(1);
        // light inner hairline, brighter along the top edge (catches the light like the macOS Dock)
        bar.BorderBrush = new LinearGradientBrush(new GradientStopCollection
        {
            new GradientStop(Ui.C(0x5CFFFFFF), 0), new GradientStop(Ui.C(0x24FFFFFF), 0.35), new GradientStop(Ui.C(0x14FFFFFF), 1)
        }, 90);
        bar.Background = new LinearGradientBrush(Ui.C(0x1CFFFFFF), Ui.C(0x08FFFFFF), 90);
        root.Children.Add(bar);
        root.Children.Add(stack);
        root.HorizontalAlignment = HorizontalAlignment.Center;
        root.VerticalAlignment = VerticalAlignment.Bottom;
        root.Margin = new Thickness(0, 0, 0, Gap);
        root.RenderTransform = slide;
        root.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // keeps the magnified zone hoverable
        root.AllowDrop = true;
        outer.Children.Add(root);
        outer.Children.Add(hotZone);
        outer.Children.Add(overlay);
        Content = outer;

        // tooltip: dark rounded pill with a little arrow pointing at the icon
        labelText = new TextBlock { Foreground = Ui.B(0xF2FFFFFF), FontSize = 13, FontFamily = Ui.Font };
        var pill = new Border
        {
            Child = labelText, CornerRadius = new CornerRadius(7), Padding = new Thickness(11, 4, 11, 5),
            Background = Ui.B(0xF0262628), BorderBrush = Ui.B(0x40FFFFFF), BorderThickness = new Thickness(0.8)
        };
        var arrow = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M0,0 L7,6.5 L14,0 Z"), Fill = Ui.B(0xF0262628), HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, -1, 0, 0)
        };
        var tip = new StackPanel { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        tip.Children.Add(pill); tip.Children.Add(arrow);
        label = tip;
        overlay.Children.Add(label);

        hotZone.MouseEnter += (_, _) => { if (AutoHide && hideTarget > 0) revealTimer.Start(); };
        hotZone.MouseLeave += (_, _) => revealTimer.Stop();
        revealTimer.Tick += (_, _) => { revealTimer.Stop(); SetHidden(false); };
        // While revealed, check the real cursor position a couple of times a second (WPF can miss "mouse left"
        // on a no-activate window) and tuck the dock away once the pointer has moved off it.
        hideTimer.Tick += (_, _) =>
        {
            if (!AutoHide || hideTarget > 0) { hideTimer.Stop(); return; }
            if (menuOpen || pressCell != null || CursorNearDock()) return;
            hideTimer.Stop();
            SetHidden(true);
        };

        root.MouseMove += Root_MouseMove;
        leaveWatch.Tick += (_, _) =>
        {
            if (pressCell != null || menuOpen) return;
            if (!CursorOverRoot()) { leaveWatch.Stop(); OnLeave(); }
        };
        root.MouseLeave += (_, _) => { if (pressCell == null) OnLeave(); };
        root.MouseLeftButtonDown += Root_MouseLeftButtonDown;
        root.MouseLeftButtonUp += Root_MouseLeftButtonUp;
        root.MouseRightButtonUp += (_, e) => { if (!e.Handled) { ShowMenu(BuildPrefsMenu(), null); e.Handled = true; } };
        root.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None; e.Handled = true; };
        root.Drop += Root_Drop;
        bar.SizeChanged += (_, _) => SyncGlass();

        SourceInitialized += OnSourceInitialized;
        debounce.Tick += (_, _) => { debounce.Stop(); RefreshWindows(); };
        tick.Tick += (_, _) => OnTick();
    }

    // ───────────────────────────── setup ─────────────────────────────

    void OnSourceInitialized(object s, EventArgs e)
    {
        hwnd = new WindowInteropHelper(this).Handle;
        long ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        ex = (ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE) & ~WS_EX_APPWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)ex);
        SetWindowLongPtr(hwnd, -8 /* GWLP_HWNDPARENT: owner */, glass.Handle); // owned → always stacked above the glass
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        // move onto our monitor first so WPF adopts that screen's DPI
        SetWindowPos(hwnd, IntPtr.Zero, mon.Bounds.Left + 10, mon.Bounds.Top + 10, 100, 100, SWP_NOZORDER | SWP_NOACTIVATE);
        dpi = mon.Dpi;

        appBarMsg = RegisterWindowMessageW("HarborAppBarMessage");
        taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");

        BuildStaticCells();
        ReloadPins();
        ApplySizes();
        if (AutoHide) { hideAmt = hideTarget = 1; ApplySlide(); }
        RegisterAppBar();

        winEventProc = OnWinEvent;
        hooks.Add(SetWinEventHook(0x8001, 0x8003, IntPtr.Zero, winEventProc, 0, 0, 2)); // destroy/show/hide
        hooks.Add(SetWinEventHook(0x8017, 0x8018, IntPtr.Zero, winEventProc, 0, 0, 2)); // cloaked/uncloaked (UWP)
        tick.Start();
        built = true;
    }

    void BuildStaticCells()
    {
        var lp = new LaunchpadTile();
        launchCell = new DockCell("sys", lp.View) { Label = () => "Start", Click = TaskbarControl.OpenStart, RightClick = c => ShowMenu(BuildPrefsMenu(), c) };

        cal = new CalendarTile();
        calCell = new DockCell("widget", cal.View) { Label = () => DateTime.Now.ToString("dddd, MMMM d, yyyy"), Click = () => PressKeys(VK_LWIN, 0x4E /*N*/) };
        clock = new ClockTile { ShowSeconds = cfg.ClockSeconds };
        clockCell = new DockCell("widget", clock.View) { Label = ClockTile.Label, Click = () => Shell.Launch("ms-clock:") };
        digi = new DigitalClockTile { ShowSeconds = cfg.ClockSeconds };
        digiCell = new DockCell("widget", digi.View) { Aspect = DigitalClockTile.Aspect, Label = ClockTile.Label, Click = () => Shell.Launch("ms-clock:") };
        sys = new SystemWidget();
        sysCell = new DockCell("widget", sys.View) { Aspect = sys.Aspect, Label = sys.Label, Click = () => Shell.Launch("taskmgr.exe") };
        media = new MediaWidget();
        mediaCell = new DockCell("widget", media.View) { Aspect = MediaWidget.Aspect, Label = () => media.Artist.Length > 0 ? $"{media.Title} — {media.Artist}" : media.Title, Click = media.TogglePlay };
        media.VisibilityChanged += () => { if (built) { RebuildCells(); } };
        var cc = new ControlCenterTile();
        ccCell = new DockCell("sys", cc.View) { Label = () => "Quick Settings", Click = () => PressKeys(VK_LWIN, 0x41 /*A*/) };

        string downloads = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        dlCell = new DockCell("sys", IconImage(downloads)) { Label = () => "Downloads", Click = () => Shell.Launch(downloads), RightClick = c => ShowMenu(BuildDownloadsMenu(downloads), c) };
        const string bin = "::{645FF040-5081-101B-9F08-00AA002F954E}";
        trashCell = new DockCell("sys", IconImage(bin)) { Label = BinLabel, Click = () => Shell.Launch("shell:RecycleBinFolder"), RightClick = c => ShowMenu(BuildBinMenu(), c) };

        sep1 = Separator(); sep2 = Separator(); sep3 = Separator();
        foreach (var c in new[] { launchCell, calCell, clockCell, digiCell, sysCell, mediaCell, ccCell, dlCell, trashCell }) Wire(c);
        UpdateBin();
    }

    DockCell Separator()
    {
        // thin hairline, ~75% of the icon height, centred: the macOS divider
        var line = new Border { Width = 1, Background = Ui.B(0x3DFFFFFF), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch };
        line.SizeChanged += (_, _) => { double h = line.Parent is Border p ? p.ActualHeight : 0; var m = Math.Round(h * 0.13); if (line.Margin.Top != m) line.Margin = new Thickness(0, m, 0, m); };
        var c = new DockCell("sep", line) { Aspect = 0.22 };
        c.MouseRightButtonUp += (_, e) => { ShowMenu(BuildPrefsMenu(), null); e.Handled = true; };
        return c;
    }

    static FrameworkElement IconImage(string parsingName, string fallbackName = null)
    {
        var src = Shell.GetIcon(parsingName, 128);
        if (src != null)
        {
            var img = new Image { Source = src, Stretch = Stretch.Uniform, Margin = new Thickness(1) };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }
        // Fallback: a tidy letter tile
        var t = Ui.Text((fallbackName ?? "?").Substring(0, 1).ToUpperInvariant(), 30, Brushes.White, FontWeights.SemiBold);
        return Ui.Wrap(Ui.Tile(64, Ui.Grad(0xFF5E5CE6, 0xFF0A84FF), t));
    }

    void Wire(DockCell c)
    {
        c.MouseEnter += (_, _) => { if (pressCell == null) ShowLabel(c); };
        c.MouseLeave += (_, _) => { if (hovered == c && pressCell == null) HideLabel(); };
        c.MouseLeftButtonDown += (_, e) => BeginPress(c, e);
        c.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle && c.Entry != null) { Shell.Launch(c.Entry.LaunchPath, c.Entry.LaunchArgs); Bounce(c); e.Handled = true; } };
        c.MouseRightButtonUp += (_, e) => { HideLabel(); c.RightClick?.Invoke(c); e.Handled = true; };
    }

    void BeginPress(DockCell c, MouseButtonEventArgs e)
    {
        pressCell = c; pressPt = e.GetPosition(this); dragging = false;
        c.Host.Opacity = 0.72; root.CaptureMouse(); e.Handled = true;
    }

    /// <summary>A press that landed in the gap between two icons goes to the nearest icon (no dead zones, like macOS).</summary>
    void Root_MouseLeftButtonDown(object s, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        // only inside the bar itself, never in the transparent zone above it (that looks like desktop)
        if (e.GetPosition(bar).Y < 0) return;
        double x = e.GetPosition(this).X;
        DockCell best = null; double bestD = double.MaxValue;
        foreach (var c in cells)
        {
            if (c.Kind == "sep" || !c.IsLoaded) continue;
            double center = c.TranslatePoint(new Point(c.ActualWidth / 2, 0), this).X;
            double reach = c.ActualWidth / 2 + c.Margin.Left + 1;
            double d = Math.Abs(x - center);
            if (d <= reach && d < bestD) { best = c; bestD = d; }
        }
        if (best != null) BeginPress(best, e);
    }

    // ───────────────────────────── apps ─────────────────────────────

    public void ReloadPinsFromConfig() => ReloadPins();

    /// <summary>Win+1…9: open / switch to / minimize the Nth app in the dock, like the Windows taskbar.</summary>
    public void ActivatePin(int index)
    {
        var apps = cells.Where(c => c.Kind == "app" && c.Entry != null).ToList();
        if (index < apps.Count) apps[index].Click?.Invoke();
    }

    void ReloadPins()
    {
        foreach (var p in pinned) appCells.Remove(p);
        pinned.Clear();
        foreach (var pin in cfg.Pins)
        {
            try { pinned.Add(DockEntry.FromPin(pin)); } catch (Exception ex) { Log.Write("pin " + pin.Path + ": " + ex.Message); }
        }
        RefreshWindows();
    }

    public static int DiagRefresh, DiagFrames, DiagEvents, DiagRebuild;
    void RefreshWindows()
    {
        DiagRefresh++;
        List<WinInfo> wins;
        try { wins = WindowTracker.Enumerate(); } catch (Exception ex) { Log.Write("enum: " + ex.Message); return; }
        foreach (var e in pinned) e.Windows.Clear();
        foreach (var e in running.Values) e.Windows.Clear();
        foreach (var w in wins)
        {
            var e = Match(w);
            if (e == null)
            {
                bool fh = w.Exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
                string key = fh ? "aumid:" + w.Aumid.ToLowerInvariant() : w.Exe.ToLowerInvariant();
                if (!running.TryGetValue(key, out e)) running[key] = e = NewRunningEntry(w, key, fh);
            }
            e.Windows.Add(w.Hwnd);
        }
        foreach (var k in running.Where(kv => kv.Value.Windows.Count == 0).Select(kv => kv.Key).ToList())
        {
            appCells.Remove(running[k]); running.Remove(k);
        }
        RebuildCells();
    }

    DockEntry Match(WinInfo w)
    {
        string file = System.IO.Path.GetFileName(w.Exe).ToLowerInvariant();
        string aumid = w.Aumid?.ToLowerInvariant();
        if (aumid != null) foreach (var p in pinned) if (p.MatchAumid == aumid) return p;
        if (file == "applicationframehost.exe") return null;
        foreach (var p in pinned) if (p.MatchExe == file) return p;
        string lower = w.Exe.ToLowerInvariant();
        foreach (var p in pinned) if (p.MatchDir != null && lower.StartsWith(p.MatchDir)) return p;
        return null;
    }

    static DockEntry NewRunningEntry(WinInfo w, string key, bool frameHost)
    {
        var e = new DockEntry { Id = "run:" + key, ExePath = frameHost ? null : w.Exe };
        if (w.Aumid != null && (frameHost || w.Exe.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)))
        {
            e.IconPath = e.LaunchPath = @"shell:AppsFolder\" + w.Aumid;
            e.Name = Shell.GetDisplayName(e.IconPath) ?? WindowTracker.DescribeExe(w.Exe);
            e.MatchAumid = w.Aumid.ToLowerInvariant();
        }
        else
        {
            e.IconPath = e.LaunchPath = w.Exe;
            e.Name = WindowTracker.DescribeExe(w.Exe);
        }
        return e;
    }

    DockCell CellFor(DockEntry e, bool growIn)
    {
        if (appCells.TryGetValue(e, out var c)) return c;
        c = new DockCell("app", IconImage(e.IconPath, e.Name)) { Entry = e };
        c.Label = () => e.Name;
        c.Click = () => ClickApp(e, c);
        c.RightClick = cell => ShowMenu(BuildAppMenu(e), cell);
        Wire(c);
        if (growIn) { c.Scale = 0.01; c.Target = 1; }
        appCells[e] = c;
        return c;
    }

    void RebuildCells()
    {
        var want = new List<DockCell> { launchCell };
        foreach (var p in pinned) want.Add(CellFor(p, built));
        var extra = running.Values.ToList();
        if (extra.Count > 0) { want.Add(sep1); foreach (var r in extra) want.Add(CellFor(r, built)); }
        want.Add(sep2);
        if (cfg.ShowWidgets)
        {
            want.Add(calCell);
            if (cfg.ClockStyle != "Analog") want.Add(digiCell);
            if (cfg.ClockStyle is "Analog" or "Both") want.Add(clockCell);
            want.Add(sysCell);
            if (media.HasSession) want.Add(mediaCell);
        }
        want.Add(ccCell);
        want.Add(sep3); want.Add(dlCell); want.Add(trashCell);

        foreach (var c in want.Where(c => c.Entry != null)) c.Dot.Visibility = c.Entry.Running ? Visibility.Visible : Visibility.Hidden;

        if (!stack.Children.Cast<UIElement>().SequenceEqual(want))
        {
            DiagRebuild++;
            FitToScreen(want);
            stack.Children.Clear();
            foreach (var c in want) { stack.Children.Add(c); c.ApplySize(Base); }
            cells.Clear(); cells.AddRange(want);
            PositionWindow();
            if (cells.Any(c => c.Scale != c.Target)) StartAnim();
        }
    }

    void ClickApp(DockEntry e, DockCell c)
    {
        RefreshWindows(); // fresh z-order
        if (!e.Running || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            Shell.Launch(e.LaunchPath, e.LaunchArgs);
            Bounce(c);
            return;
        }
        IntPtr fg = GetForegroundWindow();
        int idx = e.Windows.IndexOf(fg);
        if (idx >= 0)
        {
            if (e.Windows.Count == 1) ShowWindowAsync(fg, SW_MINIMIZE);
            else ActivateWindow(e.Windows[^1]); // cycle through the app's windows
        }
        else ActivateWindow(e.Windows[0]);
    }

    void Bounce(DockCell c)
    {
        // macOS-style launch bounce: each hop a little lower, with ease-out going up and ease-in falling back
        var a = new DoubleAnimationUsingKeyFrames();
        double t = 0;
        foreach (var (h, ms) in new[] { (0.5, 300.0), (0.32, 250.0), (0.16, 200.0) })
        {
            t += ms * 0.5; a.KeyFrames.Add(new EasingDoubleKeyFrame(-Base * h, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            t += ms * 0.5; a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t)), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        }
        c.Lift.BeginAnimation(TranslateTransform.YProperty, a);
    }

    void Pin(DockEntry e)
    {
        cfg.Pins.Add(new PinConfig { Path = e.LaunchPath, Name = e.Name });
        cfg.Save(); ReloadPins();
    }

    void Unpin(DockEntry e)
    {
        cfg.Pins.Remove(e.Pin);
        cfg.Save(); ReloadPins();
    }

    void Root_Drop(object s, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var f in files)
                cfg.Pins.Add(new PinConfig { Path = f, Name = Shell.GetDisplayName(f) ?? System.IO.Path.GetFileNameWithoutExtension(f) });
            cfg.Save(); ReloadPins();
        }
    }

    // ─────────────────────── mouse, magnification, drag ───────────────────────

    void Root_MouseMove(object s, MouseEventArgs e)
    {
        // WPF re-raises MouseMove with its last known position whenever the layout changes (e.g. icons shrinking back);
        // if the pointer actually left without WPF noticing, that stale position would magnify the icons again.
        if (pressCell == null && !CursorOverRoot()) { if (cells.Any(c => c.Target != 1)) OnLeave(); return; }
        var p = e.GetPosition(this);
        if (pressCell != null && e.LeftButton == MouseButtonState.Pressed)
        {
            if (!dragging && pressCell.Entry?.Pinned == true && (p - pressPt).Length > 6) { dragging = true; HideLabel(); }
            if (dragging) { DragMove(p); return; }
        }
        UpdateTargets(p.X);
        if (!leaveWatch.IsEnabled) leaveWatch.Start();
    }

    void DragMove(Point p)
    {
        foreach (var c in cells) c.Target = 1;
        StartAnim();
        bool remove = p.Y < root.TranslatePoint(new Point(0, 0), this).Y - Base * 0.9;
        pressCell.Host.Opacity = remove ? 0.3 : 0.6;
        if (remove) return;
        // live reorder among pinned cells
        var pinnedCells = pinned.Select(x => appCells[x]).ToList();
        int target = 0;
        foreach (var c in pinnedCells)
        {
            if (c == pressCell) continue;
            double center = c.TranslatePoint(new Point(c.ActualWidth / 2, 0), this).X;
            if (p.X > center) target++;
        }
        int cur = pinned.IndexOf(pressCell.Entry);
        if (target != cur)
        {
            pinned.RemoveAt(cur); pinned.Insert(target, pressCell.Entry);
            RebuildCells();
        }
    }

    void Root_MouseLeftButtonUp(object s, MouseButtonEventArgs e)
    {
        if (pressCell == null) return;
        var c = pressCell; pressCell = null;
        c.Host.Opacity = 1;
        root.ReleaseMouseCapture();
        var p = e.GetPosition(this);
        if (dragging)
        {
            dragging = false;
            bool remove = p.Y < root.TranslatePoint(new Point(0, 0), this).Y - Base * 0.9;
            if (remove) Unpin(c.Entry);
            else { cfg.Pins = pinned.Select(x => x.Pin).ToList(); cfg.Save(); }
        }
        else
        {
            // A press + release that didn't travel is a click on the pressed icon, even if magnification slid the icon
            // a little under a still pointer in between (checking the icon's bounds dropped such clicks).
            var pc = e.GetPosition(c);
            bool inside = pc.X >= 0 && pc.Y >= 0 && pc.X <= c.ActualWidth && pc.Y <= c.ActualHeight;
            if (inside || (p - pressPt).Length < 10) c.Click?.Invoke();
        }
        if (!root.IsMouseOver) OnLeave(); else UpdateTargets(p.X);
    }

    void OnLeave()
    {
        HideLabel();
        foreach (var c in cells) c.Target = 1;
        StartAnim();
    }

    // WPF sometimes never reports "mouse left" (e.g. a window maximized under the pointer via the keyboard), which
    // left icons stuck magnified. While anything is magnified, check the real cursor position a few times a second.
    readonly DispatcherTimer magWatch = new() { Interval = TimeSpan.FromMilliseconds(200) };
    bool magWatchWired;

    void WatchMagnification()
    {
        if (!magWatchWired)
        {
            magWatchWired = true;
            magWatch.Tick += (_, _) =>
            {
                if (cells.All(c => c.Target == 1)) { magWatch.Stop(); return; }
                if (pressCell == null && !menuOpen && !CursorNearDock()) { magWatch.Stop(); OnLeave(); }
            };
        }
        if (!magWatch.IsEnabled) magWatch.Start();
    }

    void UpdateTargets(double mouseX)
    {
        WatchMagnification();
        double mag = cfg.Magnification;
        double total = 2 * Pad + cells.Sum(c => c.BaseWidth(Base));
        double x = (outer.ActualWidth - total) / 2 + Pad;
        double range = Base * 2.7;
        foreach (var c in cells)
        {
            double w = c.BaseWidth(Base), center = x + w / 2; x += w;
            if (c.Kind == "sep" || mag <= 1.01) { c.Target = 1; continue; }
            double d = Math.Abs(mouseX - center) / range;
            c.Target = d < 1 ? 1 + (mag - 1) * (Math.Cos(d * Math.PI) + 1) / 2 : 1;
        }
        StartAnim();
    }

    void StartAnim()
    {
        if (animating) return;
        animating = true; lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    // Runs only while something is moving; unhooks itself at rest → zero idle cost.
    void OnFrame(object s, EventArgs e)
    {
        DiagFrames++;
        var t = ((RenderingEventArgs)e).RenderingTime;
        double dt = lastFrame == TimeSpan.Zero ? 1 / 60.0 : (t - lastFrame).TotalSeconds;
        if (dt <= 0) return;
        lastFrame = t;
        double k = 1 - Math.Exp(-dt * 22);
        bool moving = false;
        foreach (var c in cells)
        {
            double diff = c.Target - c.Scale;
            if (Math.Abs(diff) < 0.002) c.Scale = c.Target; else { c.Scale += diff * k; moving = true; }
            c.ApplySize(Base);
        }
        // auto-hide slide (a touch slower than magnification so it reads as a glide)
        double hd = hideTarget - hideAmt;
        if (Math.Abs(hd) < 0.003) hideAmt = hideTarget; else { hideAmt += hd * (1 - Math.Exp(-dt * 16)); moving = true; }
        ApplySlide();
        outer.UpdateLayout();
        PositionLabel();
        SyncGlass();
        if (!moving) { CompositionTarget.Rendering -= OnFrame; animating = false; }
    }

    void ShowLabel(DockCell c)
    {
        hovered = c;
        string s = c.Label?.Invoke();
        if (string.IsNullOrEmpty(s) || dragging || hideAmt > 0.5) { label.Visibility = Visibility.Collapsed; return; }
        bool wasHidden = label.Visibility != Visibility.Visible;
        if (labelText.Text != s) labelText.Text = s;
        label.Visibility = Visibility.Visible;
        PositionLabel();
        if (wasHidden) label.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
    }

    void HideLabel() { hovered = null; label.BeginAnimation(OpacityProperty, null); label.Visibility = Visibility.Collapsed; }

    void PositionLabel()
    {
        if (hovered == null || label.Visibility != Visibility.Visible || !hovered.IsLoaded) return;
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var sz = label.DesiredSize;
        var p = hovered.Host.TranslatePoint(new Point(hovered.Host.ActualWidth / 2, 0), overlay);
        Canvas.SetLeft(label, Math.Clamp(p.X - sz.Width / 2, 4, Math.Max(4, overlay.ActualWidth - sz.Width - 4)));
        Canvas.SetTop(label, Math.Max(0, p.Y - sz.Height - 6));
    }

    // ───────────────────────────── glass, appbar ─────────────────────────────

    void ApplySizes()
    {
        bar.Height = BarH;
        double r = Math.Round(BarH * 0.29);
        bar.CornerRadius = new CornerRadius(r);
        for (int i = 0; i < shadow.Length; i++)
        {
            double k = i + 1;
            shadow[i].Height = BarH + 2 * k;
            shadow[i].Margin = new Thickness(-k, 0, -k, -k);
            shadow[i].CornerRadius = new CornerRadius(r + k);
        }
        stack.Margin = new Thickness(Pad, 0, Pad, 0);
        foreach (var c in cells) c.ApplySize(Base);
        ApplySlide();
    }

    /// <summary>Moves the dock along its auto-hide path (0 = shown, 1 = tucked below the screen edge).</summary>
    void ApplySlide()
    {
        double y = Math.Round(hideAmt * (BarH + Gap + 6));
        if (slide.Y != y) slide.Y = y;
    }

    void SetHidden(bool hide)
    {
        double t = hide ? 1 : 0;
        if (hideTarget == t) return;
        hideTarget = t;
        if (hide) { HideLabel(); hideTimer.Stop(); }
        else if (AutoHide) hideTimer.Start();
        hotZone.Visibility = AutoHide ? Visibility.Visible : Visibility.Collapsed;
        StartAnim();
    }

    bool CursorOverRoot()
    {
        if (!GetCursorPos(out var p) || root.ActualWidth < 1) return false;
        var tl = root.PointToScreen(new Point(0, 0));
        var br = root.PointToScreen(new Point(root.ActualWidth, root.ActualHeight));
        return p.X >= tl.X && p.X < br.X && p.Y >= tl.Y && p.Y < br.Y;
    }

    /// <summary>True while the pointer is over the dock (including its magnified zone) or the strip below it.</summary>
    bool CursorNearDock()
    {
        if (!GetCursorPos(out var p) || root.ActualWidth < 1) return false;
        var tl = root.PointToScreen(new Point(0, 0));
        var br = root.PointToScreen(new Point(root.ActualWidth, root.ActualHeight));
        return p.X >= tl.X && p.X <= br.X && p.Y >= tl.Y - 4 && p.Y <= mon.Bounds.Bottom;
    }

    void SyncGlass()
    {
        if (hwnd == IntPtr.Zero || !IsVisible || bar.ActualWidth < 1) return;
        // fully tucked away → hide the blur layer too (it would otherwise peek onto a monitor below)
        bool tucked = hideAmt > 0.985;

        if (tucked) { glass.Park(); return; }
        var tl = bar.PointToScreen(new Point(0, 0));
        var br = bar.PointToScreen(new Point(bar.ActualWidth, bar.ActualHeight));
        glass.Place((int)Math.Round(tl.X), (int)Math.Round(tl.Y), (int)Math.Round(br.X - tl.X), (int)Math.Round(br.Y - tl.Y),
            (int)Math.Round(bar.CornerRadius.TopLeft * dpi));
    }

    void RegisterAppBar()
    {
        if (!appBarRegistered)
        {
            var abd = NewAbd(); abd.uCallbackMessage = appBarMsg;
            TrayHost.AppBarMessage(ABM_NEW, ref abd);
            appBarRegistered = true;
        }
        SetAppBarPos();
    }

    APPBARDATA NewAbd() => new() { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>(), hWnd = hwnd };

    void SetAppBarPos()
    {
        if (hwnd == IntPtr.Zero) return;
        var m = CurrentMonitor(); dpi = m.Dpi;
        var b = m.Bounds;
        // Reserve just the bar plus the gap below it (windows may come right up to the dock, as on macOS).
        // With auto-hide on, nothing is reserved: apps get the whole screen and the dock slides over them.
        int reserve = AutoHide ? 0 : (int)Math.Round((BarH + Gap * 2) * dpi);
        var abd = NewAbd();
        abd.uEdge = ABE_BOTTOM;
        abd.rc = new RECT { Left = b.Left, Top = b.Bottom - reserve, Right = b.Right, Bottom = b.Bottom };
        TrayHost.AppBarMessage(ABM_QUERYPOS, ref abd);
        abd.rc.Top = abd.rc.Bottom - reserve;
        TrayHost.AppBarMessage(ABM_SETPOS, ref abd);
        bottomPx = abd.rc.Bottom;
        hotZone.Visibility = AutoHide ? Visibility.Visible : Visibility.Collapsed;
        PositionWindow();
    }

    /// <summary>Called when the auto-hide setting changes.</summary>
    void ApplyAutoHide()
    {
        SetAppBarPos();
        if (AutoHide) hideTimer.Start(); // tucks away as soon as the pointer leaves (e.g. after the menu closes)
        else { hideTimer.Stop(); SetHidden(false); }
    }

    int lastWinW = -1, lastWinH = -1, lastWinY = int.MinValue;

    /// <summary>Keeps the (layered) window only as big as the dock can ever get → less pixels to compose each frame.</summary>
    void PositionWindow()
    {
        if (hwnd == IntPtr.Zero || bottomPx == 0) return;
        var b = mon.Bounds; int sw = b.Right - b.Left;
        double content = 2 * Pad + cells.Sum(c => c.BaseWidth(Base)) + Base * Math.Max(0, cfg.Magnification - 1) * 2.8 + 220;
        int wPx = Math.Min(sw, (int)Math.Ceiling(content * dpi));
        // tall enough for magnified icons + the tooltip above them; the window sits flush with the screen/work-area
        // bottom and the bar floats `Gap` above it (root's bottom margin), leaving room for the soft shadow.
        double winH = Base * Math.Max(cfg.Magnification, 1.5) + 2 * PadV + Gap + 52;
        int hPx = (int)Math.Ceiling(winH * dpi);
        int y = bottomPx - hPx;
        if (wPx != lastWinW || hPx != lastWinH || y != lastWinY)
        {
            SetWindowPos(hwnd, IntPtr.Zero, b.Left + (sw - wPx) / 2, y, wPx, hPx, SWP_NOZORDER | SWP_NOACTIVATE);
            lastWinW = wPx; lastWinH = hPx; lastWinY = y;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, SyncGlass);
    }

    public MonitorInfo Monitor => mon;

    public void SetFullscreen(bool fs)
    {
        if (fs)
        {
            string cls = Native.GetClassName(GetForegroundWindow());
            if (cls is "Progman" or "WorkerW") return; // desktop is not a fullscreen app
        }
        if (fs == fullscreen) return;
        fullscreen = fs;
        if (fs) { Hide(); glass.Hide(); tick.Stop(); }
        else { glass.Show(); Show(); tick.Start(); RefreshWindows(); SetAppBarPos(); }
    }

    IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == appBarMsg)
        {
            if ((uint)w == ABN_POSCHANGED) Dispatcher.BeginInvoke(SetAppBarPos);
            else if ((uint)w == ABN_FULLSCREENAPP) { bool fs = l != IntPtr.Zero; Dispatcher.BeginInvoke(() => SetFullscreen(fs)); }
        }
        else if (msg == taskbarCreatedMsg) // Explorer restarted
        {
            Dispatcher.BeginInvoke(() => { appBarRegistered = false; TaskbarControl.Apply(cfg); RegisterAppBar(); });
        }
        else if (msg == 0x007E || msg == 0x02E0) // WM_DISPLAYCHANGE, WM_DPICHANGED
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, SetAppBarPos);
        }
        else if (msg == 0x0021) // WM_MOUSEACTIVATE → never steal focus from the user's app
        {
            handled = true; return (IntPtr)3;
        }
        return IntPtr.Zero;
    }

    void OnWinEvent(IntPtr hook, uint ev, IntPtr h, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || h == IntPtr.Zero) return;
        // Explorer re-showing its taskbar (e.g. after Start menu) → hide it again right away
        if (ev == 0x8002 && TaskbarControl.Hidden)
        {
            string cls = Native.GetClassName(h);
            if ((cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") && !TrayHost.IsOwnWindow(h))
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, TaskbarControl.EnforceHidden);
                return;
            }
        }
        if (ev != 0x8001 && GetAncestor(h, GA_ROOT) != h) return;
        DiagEvents++;
        if (!debounce.IsEnabled) debounce.Start();
    }

    void OnTick()
    {
        tickCount++;
        TaskbarControl.EnforceHidden(); // safety net, costs a few microseconds
        if (tickCount % 10 == 0 && Environment.GetEnvironmentVariable("HARBOR_DIAG") == "1") { Log.Write($"diag refresh={DiagRefresh} frames={DiagFrames} events={DiagEvents} rebuild={DiagRebuild}"); DiagRefresh = DiagFrames = DiagEvents = DiagRebuild = 0; }
        if (cfg.ClockStyle is "Analog" or "Both") clock.Update();
        if (cfg.ClockStyle != "Analog") digi.Update();
        if (tickCount % 30 == 0) cal.Update();
        if (tickCount % 3 == 0) sys.Update();
        if (tickCount % 5 == 0) UpdateBin();
        if (tickCount % 15 == 0) RefreshWindows(); // safety net; the WinEvent hook does the real work
        if (hovered != null && (hovered == clockCell || hovered == sysCell || hovered == trashCell)) ShowLabel(hovered);
    }

    void UpdateBin()
    {
        var info = new SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<SHQUERYRBINFO>() };
        if (SHQueryRecycleBinW(null, ref info) != 0) return;
        bool changedEmptiness = (lastBinCount == 0) != (info.i64NumItems == 0);
        if (lastBinCount >= 0 && changedEmptiness)
            trashCell.Host.Child = IconImageFresh("::{645FF040-5081-101B-9F08-00AA002F954E}");
        lastBinCount = info.i64NumItems;
    }

    static FrameworkElement IconImageFresh(string p)
    {
        var src = Shell.GetIcon(p, 128, noCache: true);
        return new Image { Source = src, Stretch = Stretch.Uniform, Margin = new Thickness(1) };
    }

    string BinLabel() => lastBinCount > 0 ? $"Recycle Bin  ·  {lastBinCount} item{(lastBinCount == 1 ? "" : "s")}" : "Recycle Bin";

    // ───────────────────────────── menus ─────────────────────────────

    void ShowMenu(ContextMenu m, DockCell anchor)
    {
        if (m == null) return;
        if (anchor != null) { m.PlacementTarget = anchor.Host; m.Placement = System.Windows.Controls.Primitives.PlacementMode.Top; m.VerticalOffset = -6; }
        else { m.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint; }
        m.IsOpen = true;
        WatchMenu(m);
    }

    /// <summary>The dock never takes focus (so it can't steal it from your apps), which means WPF never learns about
    /// clicks elsewhere. While a menu is open, a low-level mouse hook closes it on any click outside it; Esc closes it too.</summary>
    LowLevelProc menuHookProc;
    IntPtr menuHook;

    void WatchMenu(ContextMenu m)
    {
        UnhookMenu();
        menuHookProc = (code, wParam, lParam) =>
        {
            int msg = (int)wParam;
            if (code >= 0 && (msg == 0x201 || msg == 0x204 || msg == 0x207)) // L/R/M button down
            {
                var info = System.Runtime.InteropServices.Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                IntPtr w = GetAncestor(WindowFromPoint(info.pt), GA_ROOT);
                GetWindowThreadProcessId(w, out uint pid);
                bool onMenu = pid == (uint)Environment.ProcessId && w != hwnd && w != glass.Handle;
                if (!onMenu) Dispatcher.BeginInvoke(() => m.IsOpen = false);
            }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        };
        menuHook = SetWindowsHookExW(14 /* WH_MOUSE_LL */, menuHookProc, GetModuleHandleW(null), 0);

        var esc = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        esc.Tick += (_, _) => { if (!m.IsOpen) esc.Stop(); else if ((GetAsyncKeyState(0x1B) & 0x8000) != 0) m.IsOpen = false; };
        esc.Start();
        menuOpen = true; hideTimer.Stop();
        m.Closed += (_, _) =>
        {
            esc.Stop(); UnhookMenu(); menuOpen = false;
            if (AutoHide && hideTarget == 0) hideTimer.Start();
        };
    }

    void UnhookMenu()
    {
        if (menuHook != IntPtr.Zero) { UnhookWindowsHookEx(menuHook); menuHook = IntPtr.Zero; }
    }

    static MenuItem Item(string text, Action a, bool enabled = true, bool? check = null)
    {
        var mi = new MenuItem { Header = text, IsEnabled = enabled };
        if (check.HasValue) { mi.IsCheckable = false; mi.IsChecked = check.Value; }
        if (a != null) mi.Click += (_, _) => a();
        return mi;
    }

    ContextMenu BuildAppMenu(DockEntry e)
    {
        e.Windows.RemoveAll(h => !IsWindow(h));
        var m = new ContextMenu();
        m.Items.Add(new MenuItem { Header = e.Name, IsEnabled = false, FontWeight = FontWeights.SemiBold });
        if (e.Running)
        {
            m.Items.Add(new Separator());
            foreach (var h in e.Windows.Take(12))
            {
                string t = GetWindowTitle(h);
                if (t.Length > 60) t = t[..57] + "…";
                var hh = h;
                m.Items.Add(Item(t, () => ActivateWindow(hh)));
            }
        }
        m.Items.Add(new Separator());
        m.Items.Add(Item(e.Running ? "New Window" : "Open", () => { Shell.Launch(e.LaunchPath, e.LaunchArgs); if (!e.Running) Bounce(appCells[e]); }));
        // Chrome's jump-list task, like on the Windows taskbar
        if (e.ExePath != null && System.IO.Path.GetFileName(e.ExePath).Equals("chrome.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(e.ExePath))
        {
            string chrome = e.ExePath;
            m.Items.Add(Item("New Incognito Window", () => { Shell.Launch(chrome, "--incognito"); if (!e.Running) Bounce(appCells[e]); }));
        }
        m.Items.Add(e.Pinned ? Item("Remove from Dock", () => Unpin(e)) : Item("Keep in Dock", () => Pin(e)));
        string exe = e.ExePath ?? (File.Exists(e.LaunchPath) ? e.LaunchPath : null);
        if (exe != null && File.Exists(exe)) m.Items.Add(Item("Show in File Explorer", () => Shell.ShowInExplorer(exe)));
        if (e.Running)
        {
            m.Items.Add(new Separator());
            m.Items.Add(Item(e.Windows.Count > 1 ? $"Close {e.Windows.Count} Windows" : "Quit", () => { foreach (var h in e.Windows) PostMessageW(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }));
        }
        return m;
    }

    ContextMenu BuildDownloadsMenu(string folder)
    {
        var m = new ContextMenu();
        m.Items.Add(Item("Open Downloads", () => Shell.Launch(folder)));
        try
        {
            var recent = new DirectoryInfo(folder).EnumerateFileSystemInfos().OrderByDescending(f => f.LastWriteTime).Take(10).ToList();
            if (recent.Count > 0) m.Items.Add(new Separator());
            foreach (var f in recent)
            {
                string path = f.FullName, name = f.Name.Length > 48 ? f.Name[..45] + "…" : f.Name;
                m.Items.Add(Item(name, () => Shell.Launch(path)));
            }
        }
        catch { }
        return m;
    }

    ContextMenu BuildBinMenu()
    {
        var m = new ContextMenu();
        m.Items.Add(Item("Open", () => Shell.Launch("shell:RecycleBinFolder")));
        m.Items.Add(Item("Empty Recycle Bin", () => { SHEmptyRecycleBinW(hwnd, null, 0); UpdateBin(); }, lastBinCount > 0));
        return m;
    }

    ContextMenu BuildPrefsMenu()
    {
        var m = new ContextMenu();
        m.Items.Add(new MenuItem { Header = "Harbor Dock", IsEnabled = false, FontWeight = FontWeights.SemiBold });
        m.Items.Add(new Separator());
        var size = new MenuItem { Header = "Icon Size" };
        foreach (var (n, v) in new[] { ("Small", 44.0), ("Medium", 54.0), ("Large", 64.0), ("Extra Large", 76.0) })
            size.Items.Add(Item(n, () => { cfg.IconSize = v; cfg.Save(); Relayout(); }, true, Math.Abs(cfg.IconSize - v) < 0.5));
        m.Items.Add(size);
        var mag = new MenuItem { Header = "Magnification" };
        foreach (var (n, v) in new[] { ("Off", 1.0), ("Subtle", 1.4), ("Classic", 1.75), ("Maximum", 2.1) })
            mag.Items.Add(Item(n, () => { cfg.Magnification = v; cfg.Save(); Relayout(); }, true, Math.Abs(cfg.Magnification - v) < 0.01));
        m.Items.Add(mag);
        m.Items.Add(Item("Automatically Hide Dock", () => { cfg.AutoHide = !cfg.AutoHide; cfg.Save(); Program.ApplyAutoHide(); }, true, cfg.AutoHide));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Show Widgets", () => { cfg.ShowWidgets = !cfg.ShowWidgets; cfg.Save(); RebuildCells(); }, true, cfg.ShowWidgets));
        var style = new MenuItem { Header = "Clock Style" };
        foreach (var n in new[] { "Digital", "Analog", "Both" })
            style.Items.Add(Item(n, () => { cfg.ClockStyle = n; cfg.Save(); RebuildCells(); }, true, cfg.ClockStyle == n));
        m.Items.Add(style);
        m.Items.Add(Item("Clock Seconds", () => { cfg.ClockSeconds = !cfg.ClockSeconds; clock.ShowSeconds = digi.ShowSeconds = cfg.ClockSeconds; digi.Update(); cfg.Save(); }, true, cfg.ClockSeconds));
        m.Items.Add(Item("Top Bar", () => { cfg.ShowTopBar = !cfg.ShowTopBar; cfg.Save(); Program.ApplyTopBar(); }, true, cfg.ShowTopBar));
        m.Items.Add(Item("Hide Windows Taskbar", () => { cfg.HideWindowsTaskbar = !cfg.HideWindowsTaskbar; TaskbarControl.Apply(cfg); cfg.Save(); }, true, cfg.HideWindowsTaskbar));
        m.Items.Add(Item("Start with Windows", () => Startup.Toggle(), true, Startup.Enabled));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Edit Settings File…", () => Shell.Launch("notepad.exe", "\"" + System.IO.Path.Combine(Config.Dir, "config.json") + "\"")));
        m.Items.Add(Item("Reload", () => { ReloadPins(); }));
        m.Items.Add(Item("Quit Harbor", () => Program.Quit()));
        return m;
    }

    void Relayout()
    {
        effBase = -1;
        FitToScreen(cells);
        ApplySizes();
        SetAppBarPos();
        foreach (var c in cells) { c.Scale = c.Target = 1; c.ApplySize(Base); }
    }

    public void OnAutoHideChanged() => ApplyAutoHide();

    public void Cleanup()
    {
        hideTimer.Stop(); revealTimer.Stop(); leaveWatch.Stop();
        tick.Stop();
        debounce.Stop();
        foreach (var h in hooks) if (h != IntPtr.Zero) UnhookWinEvent(h);
        hooks.Clear();
        if (appBarRegistered) { var abd = NewAbd(); TrayHost.AppBarMessage(ABM_REMOVE, ref abd); appBarRegistered = false; }
    }
}

/// <summary>Auto-hides the Windows taskbar while Harbor runs and restores the user's original setting on exit.</summary>
internal static class TaskbarControl
{
    const int ABS_AUTOHIDE = 1;

    static int GetState()
    {
        var abd = new APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>() };
        return (int)TrayHost.AppBarMessage(ABM_GETSTATE, ref abd);
    }

    static void SetState(int state)
    {
        var main = TrayWindows().FirstOrDefault(h => Native.GetClassName(h) == "Shell_TrayWnd"); // Explorer's, never Harbor's tray host
        var abd = new APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>(), hWnd = main, lParam = (IntPtr)state };
        TrayHost.AppBarMessage(ABM_SETSTATE, ref abd);
    }

    public static bool Hidden { get; private set; }

    /// <summary>All taskbar windows: the main one plus one per extra monitor.</summary>
    public static List<IntPtr> TrayWindows()
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            string cls = Native.GetClassName(h);
            if ((cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") && !TrayHost.IsOwnWindow(h)) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static void Apply(Config cfg)
    {
        if (cfg.HideWindowsTaskbar)
        {
            if (cfg.OriginalTaskbarState < 0) { cfg.OriginalTaskbarState = GetState(); cfg.Save(); }
            SetState(cfg.OriginalTaskbarState | ABS_AUTOHIDE); // frees the screen space it reserved
            Hidden = true;
            EnforceHidden();
        }
        else Restore(cfg);
    }

    /// <summary>Hides every taskbar completely (no hover pop-up). Cheap: only acts if one became visible.</summary>
    public static void EnforceHidden()
    {
        if (!Hidden) return;
        foreach (var h in TrayWindows())
        {
            // Windows re-shows its taskbar whenever Start opens, so hiding alone doesn't stick: it slid up over the dock.
            // An empty window region keeps it invisible even while Windows considers it shown.
            if (!shaped.Contains(h)) { SetWindowRgn(h, CreateRectRgn(0, 0, 0, 0), true); shaped.Add(h); }
            if (IsWindowVisible(h)) ShowWindow(h, 0 /* SW_HIDE */);
        }
    }

    static readonly HashSet<IntPtr> shaped = new();

    /// <summary>Opens the Start menu. Asking the taskbar directly is reliable; a synthetic Win key press from a
    /// no-activate window was ignored about 2 times in 3.</summary>
    public static void OpenStart()
    {
        var main = TrayWindows().FirstOrDefault(h => Native.GetClassName(h) == "Shell_TrayWnd");
        if (main == IntPtr.Zero || !PostMessageW(main, 0x0112 /* WM_SYSCOMMAND */, (IntPtr)0xF130 /* SC_TASKLIST */, IntPtr.Zero))
            PressKeys(VK_LWIN);
    }

    public static void Restore(Config cfg)
    {
        Hidden = false;
        foreach (var h in TrayWindows()) { SetWindowRgn(h, IntPtr.Zero, true); ShowWindow(h, 5 /* SW_SHOW */); }
        shaped.Clear();
        if (cfg != null && cfg.OriginalTaskbarState >= 0)
        {
            SetState(cfg.OriginalTaskbarState);
            cfg.OriginalTaskbarState = -1; cfg.Save();
        }
    }

    /// <summary>Emergency path used by "Harbor.exe --restore-taskbar".</summary>
    public static void ForceRestore()
    {
        Hidden = false;
        foreach (var h in TrayWindows()) { SetWindowRgn(h, IntPtr.Zero, true); ShowWindow(h, 5); }
        var cfg = Config.Load();
        SetState(cfg.OriginalTaskbarState >= 0 ? cfg.OriginalTaskbarState : 0);
        cfg.OriginalTaskbarState = -1; cfg.Save();
    }
}

internal static class Startup
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool Enabled
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(Key); return k?.GetValue("Harbor") != null; }
    }
    public static void Toggle()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key, true);
        if (k == null) return;
        if (k.GetValue("Harbor") != null) k.DeleteValue("Harbor");
        else k.SetValue("Harbor", "\"" + Environment.ProcessPath + "\"");
    }
}
