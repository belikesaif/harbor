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
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(DockWindow.DotRow) });
        Host = new Border { Child = content, VerticalAlignment = VerticalAlignment.Bottom, RenderTransform = Lift };
        Children.Add(Host);
        Dot = new Ellipse { Width = 4, Height = 4, Fill = Ui.B(0xD9FFFFFF), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Hidden };
        SetRow(Dot, 1);
        Children.Add(Dot);
    }

    public double BaseWidth(double b) => b * Aspect + 2 * Margin_(b);
    public static double Margin_(double b) => Math.Round(b * 0.055, 1);

    public void ApplySize(double b)
    {
        double s = Kind == "sep" ? 1 : Scale;
        double m = Margin_(b);
        Margin = new Thickness(m, 0, m, 0);
        Width = Math.Max(0, b * Aspect * s);
        Host.Height = Math.Max(0, b * s);
    }
}

internal class DockWindow : Window
{
    public const double DotRow = 9, TopPad = 6, Pad = 7, Gap = 6;
    readonly Config cfg;
    readonly GlassWindow glass;
    readonly string monitorDevice;
    MonitorInfo mon;
    IntPtr hwnd;
    double dpi = 1;

    readonly Grid outer = new(), root = new();
    readonly Border bar = new();
    readonly StackPanel stack = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
    readonly Canvas overlay = new() { IsHitTestVisible = false };
    readonly Border label;
    readonly TextBlock labelText;

    readonly List<DockEntry> pinned = new();
    readonly Dictionary<string, DockEntry> running = new();
    readonly Dictionary<DockEntry, DockCell> appCells = new();
    readonly List<DockCell> cells = new();

    DockCell launchCell, sep1, sep2, sep3, calCell, clockCell, sysCell, mediaCell, ccCell, dlCell, trashCell;
    CalendarTile cal; ClockTile clock; SystemWidget sys; MediaWidget media;
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
    double BarH => Base + TopPad + DotRow;

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

        bar.VerticalAlignment = VerticalAlignment.Bottom;
        bar.BorderThickness = new Thickness(1);
        bar.BorderBrush = new LinearGradientBrush(Ui.C(0x59FFFFFF), Ui.C(0x1AFFFFFF), 90);
        bar.Background = new LinearGradientBrush(Ui.C(0x24FFFFFF), Ui.C(0x0AFFFFFF), 90);
        root.Children.Add(bar);
        root.Children.Add(stack);
        root.HorizontalAlignment = HorizontalAlignment.Center;
        root.VerticalAlignment = VerticalAlignment.Bottom;
        root.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // keeps the magnified zone hoverable
        root.AllowDrop = true;
        outer.Children.Add(root);
        outer.Children.Add(overlay);
        Content = outer;

        labelText = new TextBlock { Foreground = Brushes.White, FontSize = 13, FontFamily = Ui.Font };
        label = new Border
        {
            Child = labelText, CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 4, 10, 5),
            Background = Ui.B(0xEB2C2C2E), BorderBrush = Ui.B(0x33FFFFFF), BorderThickness = new Thickness(0.8),
            Visibility = Visibility.Collapsed
        };
        overlay.Children.Add(label);

        root.MouseMove += Root_MouseMove;
        root.MouseLeave += (_, _) => { if (pressCell == null) OnLeave(); };
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
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        // move onto our monitor first so WPF adopts that screen's DPI
        SetWindowPos(hwnd, IntPtr.Zero, mon.Bounds.Left + 10, mon.Bounds.Top + 10, 100, 100, SWP_NOZORDER | SWP_NOACTIVATE);
        dpi = mon.Dpi;

        appBarMsg = RegisterWindowMessageW("HarborAppBarMessage");
        taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");

        BuildStaticCells();
        ReloadPins();
        ApplySizes();
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
        launchCell = new DockCell("sys", lp.View) { Label = () => "Start", Click = () => PressKeys(VK_LWIN), RightClick = c => ShowMenu(BuildPrefsMenu(), c) };

        cal = new CalendarTile();
        calCell = new DockCell("widget", cal.View) { Label = () => DateTime.Now.ToString("dddd, MMMM d, yyyy"), Click = () => PressKeys(VK_LWIN, 0x4E /*N*/) };
        clock = new ClockTile { ShowSeconds = cfg.ClockSeconds };
        clockCell = new DockCell("widget", clock.View) { Label = ClockTile.Label, Click = () => Shell.Launch("ms-clock:") };
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
        foreach (var c in new[] { launchCell, calCell, clockCell, sysCell, mediaCell, ccCell, dlCell, trashCell }) Wire(c);
        UpdateBin();
    }

    DockCell Separator()
    {
        var line = new Border { Width = 1, Background = Ui.B(0x40FFFFFF), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(0, 6, 0, 4) };
        var c = new DockCell("sep", line) { Aspect = 0.2 };
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
        c.MouseLeftButtonDown += (_, e) =>
        {
            pressCell = c; pressPt = e.GetPosition(this); dragging = false;
            c.Host.Opacity = 0.72; root.CaptureMouse(); e.Handled = true;
        };
        c.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle && c.Entry != null) { Shell.Launch(c.Entry.LaunchPath, c.Entry.LaunchArgs); Bounce(c); e.Handled = true; } };
        c.MouseRightButtonUp += (_, e) => { HideLabel(); c.RightClick?.Invoke(c); e.Handled = true; };
    }

    // ───────────────────────────── apps ─────────────────────────────

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
            want.Add(calCell); want.Add(clockCell); want.Add(sysCell);
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
        var a = new DoubleAnimation(0, -Base * 0.42, TimeSpan.FromMilliseconds(260))
        {
            AutoReverse = true, RepeatBehavior = new RepeatBehavior(3),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
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
        var p = e.GetPosition(this);
        if (pressCell != null && e.LeftButton == MouseButtonState.Pressed)
        {
            if (!dragging && pressCell.Entry?.Pinned == true && (p - pressPt).Length > 6) { dragging = true; HideLabel(); }
            if (dragging) { DragMove(p); return; }
        }
        UpdateTargets(p.X);
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
            var pc = e.GetPosition(c);
            if (pc.X >= 0 && pc.Y >= 0 && pc.X <= c.ActualWidth && pc.Y <= c.ActualHeight) c.Click?.Invoke();
        }
        if (!root.IsMouseOver) OnLeave(); else UpdateTargets(p.X);
    }

    void OnLeave()
    {
        HideLabel();
        foreach (var c in cells) c.Target = 1;
        StartAnim();
    }

    void UpdateTargets(double mouseX)
    {
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
        outer.UpdateLayout();
        PositionLabel();
        SyncGlass();
        if (!moving) { CompositionTarget.Rendering -= OnFrame; animating = false; }
    }

    void ShowLabel(DockCell c)
    {
        hovered = c;
        string s = c.Label?.Invoke();
        if (string.IsNullOrEmpty(s) || dragging) { label.Visibility = Visibility.Collapsed; return; }
        labelText.Text = s;
        label.Visibility = Visibility.Visible;
        PositionLabel();
    }

    void HideLabel() { hovered = null; label.Visibility = Visibility.Collapsed; }

    void PositionLabel()
    {
        if (hovered == null || label.Visibility != Visibility.Visible || !hovered.IsLoaded) return;
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var sz = label.DesiredSize;
        var p = hovered.Host.TranslatePoint(new Point(hovered.Host.ActualWidth / 2, 0), overlay);
        Canvas.SetLeft(label, Math.Clamp(p.X - sz.Width / 2, 4, Math.Max(4, overlay.ActualWidth - sz.Width - 4)));
        Canvas.SetTop(label, Math.Max(0, p.Y - sz.Height - 10));
    }

    // ───────────────────────────── glass, appbar ─────────────────────────────

    void ApplySizes()
    {
        bar.Height = BarH;
        bar.CornerRadius = new CornerRadius(Math.Round(BarH * 0.3));
        stack.Margin = new Thickness(Pad, 0, Pad, 0);
        foreach (var c in cells) c.ApplySize(Base);
    }

    void SyncGlass()
    {
        if (hwnd == IntPtr.Zero || !IsVisible || bar.ActualWidth < 1) return;
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
            SHAppBarMessage(ABM_NEW, ref abd);
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
        int reserve = (int)Math.Round((BarH + Gap * 2) * dpi);
        var abd = NewAbd();
        abd.uEdge = ABE_BOTTOM;
        abd.rc = new RECT { Left = b.Left, Top = b.Bottom - reserve, Right = b.Right, Bottom = b.Bottom };
        SHAppBarMessage(ABM_QUERYPOS, ref abd);
        abd.rc.Top = abd.rc.Bottom - reserve;
        SHAppBarMessage(ABM_SETPOS, ref abd);
        bottomPx = abd.rc.Bottom;
        PositionWindow();
    }

    int lastWinW = -1, lastWinH = -1;

    /// <summary>Keeps the (layered) window only as big as the dock can ever get → less pixels to compose each frame.</summary>
    void PositionWindow()
    {
        if (hwnd == IntPtr.Zero || bottomPx == 0) return;
        var b = mon.Bounds; int sw = b.Right - b.Left;
        double content = 2 * Pad + cells.Sum(c => c.BaseWidth(Base)) + Base * Math.Max(0, cfg.Magnification - 1) * 2.8 + 220;
        int wPx = Math.Min(sw, (int)Math.Ceiling(content * dpi));
        double winH = Base * Math.Max(cfg.Magnification, 1.5) + DotRow + 52;
        int hPx = (int)Math.Ceiling(winH * dpi);
        int gapPx = (int)Math.Round(Gap * dpi);
        if (wPx != lastWinW || hPx != lastWinH)
        {
            SetWindowPos(hwnd, IntPtr.Zero, b.Left + (sw - wPx) / 2, bottomPx - gapPx - hPx, wPx, hPx, SWP_NOZORDER | SWP_NOACTIVATE);
            lastWinW = wPx; lastWinH = hPx;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, SyncGlass);
    }

    void SetFullscreen(bool fs)
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
            if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd")
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
        clock.Update();
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
        m.Closed += (_, _) => { esc.Stop(); UnhookMenu(); };
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
        m.Items.Add(Item("Show Widgets", () => { cfg.ShowWidgets = !cfg.ShowWidgets; cfg.Save(); RebuildCells(); }, true, cfg.ShowWidgets));
        m.Items.Add(Item("Clock Second Hand", () => { cfg.ClockSeconds = !cfg.ClockSeconds; clock.ShowSeconds = cfg.ClockSeconds; cfg.Save(); }, true, cfg.ClockSeconds));
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

    public void Cleanup()
    {
        tick.Stop();
        debounce.Stop();
        foreach (var h in hooks) if (h != IntPtr.Zero) UnhookWinEvent(h);
        hooks.Clear();
        if (appBarRegistered) { var abd = NewAbd(); SHAppBarMessage(ABM_REMOVE, ref abd); appBarRegistered = false; }
    }
}

/// <summary>Auto-hides the Windows taskbar while Harbor runs and restores the user's original setting on exit.</summary>
internal static class TaskbarControl
{
    const int ABS_AUTOHIDE = 1;

    static int GetState()
    {
        var abd = new APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>() };
        return (int)SHAppBarMessage(ABM_GETSTATE, ref abd);
    }

    static void SetState(int state)
    {
        var abd = new APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>(), hWnd = FindWindowW("Shell_TrayWnd", null), lParam = (IntPtr)state };
        SHAppBarMessage(ABM_SETSTATE, ref abd);
    }

    public static bool Hidden { get; private set; }

    /// <summary>All taskbar windows: the main one plus one per extra monitor.</summary>
    public static List<IntPtr> TrayWindows()
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            string cls = Native.GetClassName(h);
            if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") list.Add(h);
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
            if (IsWindowVisible(h)) ShowWindow(h, 0 /* SW_HIDE */);
    }

    public static void Restore(Config cfg)
    {
        Hidden = false;
        foreach (var h in TrayWindows()) ShowWindow(h, 5 /* SW_SHOW */);
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
        foreach (var h in TrayWindows()) ShowWindow(h, 5);
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
