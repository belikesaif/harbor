using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;
using static Harbor.Native;

namespace Harbor;

/// <summary>"Tide Island": rest the cursor on the top edge and three glass pods (tray · clock · status) drop from the
/// top centre and slide apart. The centre pod doubles as a read-out: hovering anything shows its details there.
/// Nothing is drawn while it is closed. The window is hidden and only a cheap cursor check runs.</summary>
internal class TopBar : Window
{
    const double PodH = 62, WinH = 84, DropFrom = -96;
    const int DwellTicks = 3, AwayTicks = 4; // × 90 ms

    readonly string monitorDevice;
    MonitorInfo mon;
    IntPtr hwnd;
    double dpi = 1, winW = 640;
    public string MonitorDevice => monitorDevice;

    readonly StackPanel island = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 0, 0) };
    readonly TranslateTransform drop = new(0, DropFrom), leftSlide = new(), rightSlide = new();
    readonly ScaleTransform centerScale = new(1, 1);
    readonly Border trayPod, clockPod, statusPod;
    readonly StackPanel trayRow = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock time, ampm, readout;
    readonly Border dayFill;
    readonly WifiGlyph wifi = new();
    readonly TextBlock ethernet, btGlyph;
    readonly Border wifiChip, btChip, batChip;
    readonly BatteryRing battery = new();
    readonly TextBlock batText;

    readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromMilliseconds(90) };
    readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(1) };
    int dwell, away;
    bool open, closing, trayDirty = true;
    string netLabel = "", btLabel = "", batLabel = "";

    static Radio bt;
    static bool btLookupStarted;
    static event Action BtChanged;

    public TopBar(MonitorInfo monitor)
    {
        mon = monitor; monitorDevice = monitor.Device;
        Title = "Harbor Top Bar";
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true; ShowActivated = false;
        Left = 0; Top = -2000; Width = winW; Height = WinH;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        // ── tray pod ──
        trayPod = Pod(trayRow, 56);
        trayPod.RenderTransform = leftSlide;

        // ── clock pod (time · read-out · day progress) ──
        time = Ui.Text("", 24, Brushes.White, FontWeights.Light);
        ampm = Ui.Text("", 10.5, Ui.B(0xB3FFFFFF), FontWeights.SemiBold);
        ampm.VerticalAlignment = VerticalAlignment.Bottom; ampm.Margin = new Thickness(4, 0, 0, 5);
        var timeRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        timeRow.Children.Add(time); timeRow.Children.Add(ampm);
        readout = Ui.Text("", 11, Ui.B(0xC8FFFFFF), FontWeights.Normal);
        readout.MaxWidth = 260; readout.Margin = new Thickness(0, -3, 0, 0);
        dayFill = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left, Background = new LinearGradientBrush(Ui.C(0xFF64D2FF), Ui.C(0xFFBF5AF2), 0) };
        var dayTrack = new Grid { Width = 150, Height = 3, Margin = new Thickness(0, 5, 0, 0) };
        dayTrack.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = Ui.B(0x26FFFFFF) });
        dayTrack.Children.Add(dayFill);
        var clockStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        clockStack.Children.Add(timeRow); clockStack.Children.Add(readout); clockStack.Children.Add(dayTrack);
        clockPod = Pod(clockStack, 210);
        clockPod.Padding = new Thickness(22, 0, 22, 0);
        clockPod.RenderTransformOrigin = new Point(0.5, 0.5);
        clockPod.RenderTransform = centerScale;
        clockPod.Cursor = Cursors.Hand;
        Hover(clockPod, () => "Notifications & calendar");
        clockPod.MouseLeftButtonUp += (_, e) => { e.Handled = true; PressKeys(VK_LWIN, 0x4E /* N */); };
        clockPod.MouseRightButtonUp += (_, e) => { e.Handled = true; OpenUri("ms-settings:dateandtime"); };

        // ── status pod (Wi-Fi · Bluetooth · battery) ──
        ethernet = new TextBlock { Text = "", FontFamily = Ui.Icons, FontSize = 17, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        var netGrid = new Grid(); netGrid.Children.Add(wifi.View); netGrid.Children.Add(ethernet);
        wifiChip = Chip(netGrid);
        Hover(wifiChip, () => netLabel);
        wifiChip.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenUri("ms-availablenetworks:"); Retract(); };
        wifiChip.MouseRightButtonUp += (_, e) => { e.Handled = true; OpenUri("ms-settings:network"); };

        btGlyph = new TextBlock { Text = "", FontFamily = Ui.Icons, FontSize = 16, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        btChip = Chip(btGlyph);
        Hover(btChip, () => btLabel);
        btChip.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenBluetoothFlyout(); };
        btChip.MouseRightButtonUp += (_, e) => { e.Handled = true; ToggleBluetooth(); };

        batText = Ui.Text("", 12, Brushes.White, FontWeights.SemiBold);
        batText.Margin = new Thickness(5, 0, 2, 0);
        var batRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        batRow.Children.Add(battery.View); batRow.Children.Add(batText);
        batChip = Chip(batRow, round: false);
        Hover(batChip, () => batLabel);
        batChip.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenUri("ms-settings:batterysaver"); };

        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        statusRow.Children.Add(wifiChip); statusRow.Children.Add(btChip); statusRow.Children.Add(batChip);
        statusPod = Pod(statusRow, 56);
        statusPod.RenderTransform = rightSlide;

        island.Children.Add(trayPod); island.Children.Add(clockPod); island.Children.Add(statusPod);
        island.RenderTransform = drop;
        island.Opacity = 0;
        var root = new Grid { Background = Brushes.Transparent };
        root.Children.Add(island);
        Content = root;

        SourceInitialized += OnSourceInitialized;
        poll.Tick += (_, _) => Poll();
        tick.Tick += (_, _) => RefreshClock();
        TrayHost.Changed += OnTrayChanged;
        NetworkInformation.NetworkStatusChanged += OnNetworkChanged;
        BtChanged += OnBtChanged;
    }

    /// <summary>Creates the (hidden) window and starts watching the top edge.</summary>
    public void Init()
    {
        new WindowInteropHelper(this).EnsureHandle();
        poll.Start();
        if (!btLookupStarted) { btLookupStarted = true; FindBluetooth(); }
    }

    void OnSourceInitialized(object s, EventArgs e)
    {
        hwnd = new WindowInteropHelper(this).Handle;
        long ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)((ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE) & ~WS_EX_APPWINDOW));
        HwndSource.FromHwnd(hwnd).AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == 0x0021) { handled = true; return (IntPtr)3; } // WM_MOUSEACTIVATE → never steal focus
            return IntPtr.Zero;
        });
        // adopt this monitor's DPI before the first show
        SetWindowPos(hwnd, IntPtr.Zero, mon.Bounds.Left + 10, mon.Bounds.Top - 4000, 100, 100, SWP_NOZORDER | SWP_NOACTIVATE);
        dpi = mon.Dpi;
    }

    public void Cleanup()
    {
        poll.Stop(); tick.Stop();
        TrayHost.Changed -= OnTrayChanged;
        NetworkInformation.NetworkStatusChanged -= OnNetworkChanged;
        BtChanged -= OnBtChanged;
    }

    // ───────────────────────────── trigger ─────────────────────────────

    void Poll()
    {
        if (hwnd == IntPtr.Zero || !GetCursorPos(out var p)) return;
        var b = mon.Bounds;
        int wPx = (int)Math.Round(winW * dpi);
        int left = b.Left + (b.Right - b.Left - wPx) / 2;

        if (!open || closing)
        {
            bool atEdge = p.Y >= b.Top && p.Y <= b.Top + 1 && p.X >= left && p.X < left + wPx;
            bool buttonDown = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;
            if (atEdge && !buttonDown && !FullscreenAppHere()) { if (++dwell >= DwellTicks) { dwell = 0; Open(); } }
            else dwell = 0;
        }
        else
        {
            GetWindowRect(hwnd, out var r);
            int pad = (int)(22 * dpi);
            bool near = p.X >= r.Left - pad && p.X <= r.Right + pad && p.Y >= r.Top - 2 && p.Y <= r.Bottom + pad;
            if (near) away = 0;
            else if (++away >= AwayTicks) Retract();
        }
    }

    bool FullscreenAppHere()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        string cls = GetClassName(fg);
        if (cls is "Progman" or "WorkerW") return false;
        GetWindowRect(fg, out var r);
        var b = mon.Bounds;
        return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
    }

    // ───────────────────────────── open / close ─────────────────────────────

    void Open()
    {
        if (open && !closing) return;
        bool wasHidden = !IsVisible;
        open = true; closing = false; away = 0;
        RefreshAll();
        if (trayDirty) RebuildTray();
        Place();
        if (wasHidden)
        {
            drop.Y = DropFrom; leftSlide.X = 70; rightSlide.X = -70; centerScale.ScaleX = centerScale.ScaleY = 0.8; island.Opacity = 0;
            Show();
        }
        var outEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        Animate(drop, TranslateTransform.YProperty, 0, 360, new BackEase { Amplitude = 0.32, EasingMode = EasingMode.EaseOut });
        Animate(leftSlide, TranslateTransform.XProperty, 0, 440, outEase);
        Animate(rightSlide, TranslateTransform.XProperty, 0, 440, outEase);
        Animate(centerScale, ScaleTransform.ScaleXProperty, 1, 380, outEase);
        Animate(centerScale, ScaleTransform.ScaleYProperty, 1, 380, outEase);
        Animate(island, OpacityProperty, 1, 200, null);
        tick.Start();
    }

    void Retract()
    {
        if (!open || closing) return;
        closing = true;
        var inEase = new CubicEase { EasingMode = EasingMode.EaseIn };
        var last = Animate(drop, TranslateTransform.YProperty, DropFrom, 230, inEase);
        Animate(leftSlide, TranslateTransform.XProperty, 50, 230, inEase);
        Animate(rightSlide, TranslateTransform.XProperty, -50, 230, inEase);
        Animate(centerScale, ScaleTransform.ScaleXProperty, 0.88, 230, inEase);
        Animate(centerScale, ScaleTransform.ScaleYProperty, 0.88, 230, inEase);
        Animate(island, OpacityProperty, 0, 200, null);
        last.Completed += (_, _) =>
        {
            if (!closing) return; // re-opened mid-way
            closing = false; open = false;
            tick.Stop();
            Hide();
            ResetReadout();
        };
    }

    static DoubleAnimation Animate(IAnimatable target, DependencyProperty prop, double to, int ms, IEasingFunction ease)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
        target.BeginAnimation(prop, a);
        return a;
    }

    /// <summary>Sizes the window to the island and centres it on the top edge of this monitor.</summary>
    void Place()
    {
        mon = GetMonitors().FirstOrDefault(x => x.Device == monitorDevice) ?? mon;
        dpi = mon.Dpi;
        island.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var b = mon.Bounds;
        double maxW = (b.Right - b.Left) / dpi;
        winW = Math.Min(maxW, Math.Max(520, island.DesiredSize.Width + 60));
        int wPx = (int)Math.Ceiling(winW * dpi), hPx = (int)Math.Ceiling(WinH * dpi);
        SetWindowPos(hwnd, IntPtr.Zero, b.Left + (b.Right - b.Left - wPx) / 2, b.Top, wPx, hPx, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    // ───────────────────────────── content ─────────────────────────────

    void RefreshAll() { RefreshClock(); RefreshBattery(); RefreshNetwork(); RefreshBluetooth(); RefreshBtDevices(); }

    void RefreshClock()
    {
        var now = DateTime.Now;
        bool h24 = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('H');
        string t = now.ToString(h24 ? "HH:mm" : "h:mm");
        if (time.Text != t) time.Text = t;
        ampm.Text = h24 ? "" : now.ToString("tt");
        ampm.Visibility = h24 ? Visibility.Collapsed : Visibility.Visible;
        if (!hovering) ResetReadout();
        dayFill.Width = 150 * now.TimeOfDay.TotalMinutes / 1440.0;
        if (now.Second % 5 == 0) RefreshBattery();
    }

    void RefreshBattery()
    {
        if (!GetSystemPowerStatus(out var ps) || ps.BatteryFlag == 128 || ps.BatteryLifePercent > 100)
        {
            batChip.Visibility = Visibility.Collapsed;
            return;
        }
        batChip.Visibility = Visibility.Visible;
        int pct = ps.BatteryLifePercent; bool charging = ps.ACLineStatus == 1;
        battery.Set(pct, charging);
        batText.Text = pct + "%";
        string left = !charging && ps.LifeTime > 0 ? $"  ·  {TimeSpan.FromSeconds(ps.LifeTime):h\\:mm} left" : "";
        batLabel = $"Battery {pct}%" + (charging ? (pct >= 100 ? "  ·  fully charged" : "  ·  charging") : left);
    }

    void RefreshNetwork()
    {
        try
        {
            var prof = NetworkInformation.GetInternetConnectionProfile();
            if (prof == null)
            {
                ShowNet(wifiMode: true); wifi.Set(0, false);
                netLabel = "Not connected";
            }
            else if (prof.IsWlanConnectionProfile)
            {
                ShowNet(wifiMode: true);
                int bars = prof.GetSignalBars() ?? 0;
                wifi.Set(bars >= 4 ? 3 : bars >= 2 ? 2 : bars >= 1 ? 1 : 0, true);
                string ssid = prof.WlanConnectionProfileDetails?.GetConnectedSsid();
                netLabel = $"Wi-Fi  ·  {(string.IsNullOrEmpty(ssid) ? prof.ProfileName : ssid)}";
            }
            else
            {
                ShowNet(wifiMode: false);
                netLabel = prof.IsWwanConnectionProfile ? "Cellular  ·  " + prof.ProfileName : "Ethernet  ·  " + prof.ProfileName;
            }
            var level = prof?.GetNetworkConnectivityLevel();
            if (prof != null && level != NetworkConnectivityLevel.InternetAccess) netLabel += "  ·  no internet";
        }
        catch (Exception ex) { Log.Write("topbar net: " + ex.Message); netLabel = "Network"; }
    }

    void ShowNet(bool wifiMode)
    {
        wifi.View.Visibility = wifiMode ? Visibility.Visible : Visibility.Collapsed;
        ethernet.Visibility = wifiMode ? Visibility.Collapsed : Visibility.Visible;
    }

    void OnNetworkChanged(object sender) => Dispatcher.BeginInvoke(() => { if (open) RefreshNetwork(); });

    // Bluetooth: one radio lookup shared by every monitor's bar
    static async void FindBluetooth()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            bt = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (bt != null) bt.StateChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() => BtChanged?.Invoke());
            Application.Current?.Dispatcher.BeginInvoke(() => BtChanged?.Invoke());
        }
        catch (Exception ex) { Log.Write("topbar bt: " + ex.Message); }
    }

    void OnBtChanged() { RefreshBluetooth(); RefreshBtDevices(); }

    List<string> btConnected = new();

    void RefreshBluetooth()
    {
        bool on = bt?.State == RadioState.On;
        btChip.Background = on ? Ui.B(0xFF0A84FF) : Ui.B(0x14FFFFFF);
        btGlyph.Foreground = on ? Brushes.White : Ui.B(0x80FFFFFF);
        if (bt == null) btLabel = "Bluetooth  ·  not available";
        else if (!on) btLabel = "Bluetooth off  ·  right-click to turn on";
        else if (btConnected.Count == 0) btLabel = "Bluetooth on  ·  no devices connected";
        else btLabel = "Connected: " + string.Join(", ", btConnected);
        if (hovering && hoverSource == btChip) readout.Text = btLabel;
    }

    /// <summary>Names of connected Bluetooth devices (classic + Low Energy).</summary>
    async void RefreshBtDevices()
    {
        if (bt?.State != RadioState.On) { btConnected = new(); RefreshBluetooth(); return; }
        try
        {
            var names = new List<string>();
            foreach (var sel in new[]
            {
                BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
                BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected)
            })
                foreach (var d in await DeviceInformation.FindAllAsync(sel))
                    if (!string.IsNullOrWhiteSpace(d.Name) && !names.Contains(d.Name)) names.Add(d.Name);
            btConnected = names;
        }
        catch (Exception ex) { Log.Write("topbar bt devices: " + ex.Message); }
        RefreshBluetooth();
    }

    /// <summary>Opens Windows' own Bluetooth device list (Quick Settings › Bluetooth), like the Wi-Fi chip does.</summary>
    void OpenBluetoothFlyout()
    {
        try { Process.Start(new ProcessStartInfo("ms-actioncenter:controlcenter/bluetooth") { UseShellExecute = true }); }
        catch { OpenUri("ms-settings:bluetooth"); }
        Retract();
    }

    async void ToggleBluetooth()
    {
        if (bt == null) { OpenUri("ms-settings:bluetooth"); return; }
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) { OpenUri("ms-settings:bluetooth"); return; }
            await bt.SetStateAsync(bt.State == RadioState.On ? RadioState.Off : RadioState.On);
        }
        catch (Exception ex) { Log.Write("topbar bt toggle: " + ex.Message); OpenUri("ms-settings:bluetooth"); }
        RefreshBluetooth();
    }

    void OnTrayChanged()
    {
        trayDirty = true;
        if (open && !closing) { RebuildTray(); Place(); }
    }

    void RebuildTray()
    {
        trayDirty = false;
        trayRow.Children.Clear();
        var icons = TrayHost.Icons.Where(i => !i.Hidden && i.Icon != null).ToList();
        if (icons.Count == 0)
        {
            var empty = Ui.Text(TrayHost.Running ? "No tray icons" : "Tray off", 11, Ui.B(0x80FFFFFF), FontWeights.Normal);
            empty.Margin = new Thickness(8, 0, 8, 0);
            trayRow.Children.Add(empty);
            return;
        }
        foreach (var ic in icons)
        {
            var img = new Image { Source = ic.Icon, Width = 18, Height = 18, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var cell = Chip(img);
            Hover(cell, () => string.IsNullOrWhiteSpace(ic.Tip) ? "Tray icon" : ic.Tip.Replace("\r", "").Replace("\n", "  ·  "));
            cell.MouseEnter += (_, _) => TrayHost.Hover(ic);
            cell.MouseLeftButtonDown += (_, e) => { e.Handled = true; if (e.ClickCount == 2) TrayHost.Click(ic, false, dbl: true); };
            cell.MouseLeftButtonUp += (_, e) => { e.Handled = true; TrayHost.Click(ic, false); };
            cell.MouseRightButtonUp += (_, e) => { e.Handled = true; TrayHost.Click(ic, true); };
            trayRow.Children.Add(cell);
        }
    }

    // ───────────────────────────── read-out ─────────────────────────────

    bool hovering;
    FrameworkElement hoverSource;

    void Hover(FrameworkElement el, Func<string> text)
    {
        el.MouseEnter += (_, _) =>
        {
            hovering = true; hoverSource = el;
            readout.Text = text(); readout.Foreground = Brushes.White;
            if (el != clockPod && el is Border b && b.Tag == null) b.Background = Ui.B(0x26FFFFFF);
        };
        el.MouseLeave += (_, _) =>
        {
            if (hoverSource == el) { hovering = false; hoverSource = null; ResetReadout(); }
            if (el != clockPod && el is Border b && b.Tag == null) b.Background = Brushes.Transparent;
        };
    }

    void ResetReadout()
    {
        readout.Text = DateTime.Now.ToString("dddd, MMMM d");
        readout.Foreground = Ui.B(0xC8FFFFFF);
    }

    // ───────────────────────────── building blocks ─────────────────────────────

    static Border Pod(UIElement child, double minW) => new()
    {
        Height = PodH, MinWidth = minW, CornerRadius = new CornerRadius(PodH / 2), Padding = new Thickness(12, 0, 12, 0),
        Margin = new Thickness(5, 0, 5, 0), Child = child,
        Background = Ui.Grad(0xF22B2B2E, 0xF2151517),
        BorderBrush = Ui.Grad(0x55FFFFFF, 0x12FFFFFF), BorderThickness = new Thickness(1)
    };

    /// <summary>A round hover target. Chips with a coloured state (Bluetooth) are tagged so hover doesn't repaint them.</summary>
    Border Chip(UIElement child, bool round = true) => new()
    {
        Height = 38, MinWidth = 38, CornerRadius = new CornerRadius(19), Margin = new Thickness(1.5, 0, 1.5, 0),
        Padding = round ? new Thickness(0) : new Thickness(6, 0, 8, 0),
        Background = Brushes.Transparent, Child = child, Cursor = Cursors.Hand,
        Tag = child == btGlyph ? "state" : null
    };

    static void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("open " + uri + ": " + ex.Message); }
    }

    /// <summary>Wi-Fi strength drawn as three arcs over a dot (vector, so it stays sharp at any DPI).</summary>
    class WifiGlyph
    {
        public readonly Canvas View = new() { Width = 24, Height = 19, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        readonly Path[] arcs = new Path[3];
        readonly Ellipse dot = new() { Width = 4, Height = 4 };
        static readonly Brush On = Brushes.White, Off = Ui.B(0x40FFFFFF);
        public WifiGlyph()
        {
            const double cx = 12, cy = 17;
            double[] radii = { 5.5, 10, 14.5 };
            for (int i = 0; i < 3; i++)
            {
                double r = radii[i], s = r * Math.Sin(Math.PI / 4), c = r * Math.Cos(Math.PI / 4);
                var fig = new PathFigure { StartPoint = new Point(cx - s, cy - c) };
                fig.Segments.Add(new ArcSegment(new Point(cx + s, cy - c), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
                var geo = new PathGeometry(); geo.Figures.Add(fig); geo.Freeze();
                arcs[i] = new Path { Data = geo, StrokeThickness = 2.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                View.Children.Add(arcs[i]);
            }
            Canvas.SetLeft(dot, cx - 2); Canvas.SetTop(dot, cy - 2);
            View.Children.Add(dot);
            Set(0, false);
        }
        public void Set(int lit, bool connected)
        {
            for (int i = 0; i < 3; i++) arcs[i].Stroke = connected && i < lit ? On : Off;
            dot.Fill = connected ? On : Off;
        }
    }

    /// <summary>Battery as a ring: white normally, red at 20% or less, green with a bolt while charging.</summary>
    class BatteryRing
    {
        public readonly Grid View = new() { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center };
        readonly Path arc = new() { StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        readonly TextBlock bolt = new() { Text = "", FontFamily = Ui.Icons, FontSize = 10, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        int lastPct = -1; bool lastCharging;
        public BatteryRing()
        {
            View.Children.Add(new Ellipse { Stroke = Ui.B(0x2EFFFFFF), StrokeThickness = 3, Margin = new Thickness(1.5) });
            View.Children.Add(arc);
            View.Children.Add(bolt);
        }
        public void Set(int pct, bool charging)
        {
            if (pct == lastPct && charging == lastCharging) return;
            lastPct = pct; lastCharging = charging;
            arc.Stroke = Ui.B(charging ? 0xFF30D158 : pct <= 20 ? 0xFFFF453A : 0xFFFFFFFF);
            bolt.Visibility = charging ? Visibility.Visible : Visibility.Collapsed;
            const double R = 10.5, C = 12;
            double a = Math.Clamp(pct, 0, 100) / 100.0 * 359.99 * Math.PI / 180;
            var fig = new PathFigure { StartPoint = new Point(C, C - R) };
            fig.Segments.Add(new ArcSegment(new Point(C + R * Math.Sin(a), C - R * Math.Cos(a)), new Size(R, R), 0, a > Math.PI, SweepDirection.Clockwise, true));
            var geo = new PathGeometry(); geo.Figures.Add(fig); geo.Freeze();
            arc.Data = pct > 0 ? geo : null;
        }
    }
}
