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

/// <summary>"Tide Line": a slim frosted strip across the top of each monitor, reserved with the AppBar API like a taskbar,
/// so maximized windows (and Chrome's tabs) start below it and nothing is ever covered. Three pills: tray icons,
/// a clock that doubles as a read-out (hover anything to see its details there), and Wi-Fi / Bluetooth / battery.
/// A hairline along the bottom edge fills from left to right as the day passes.</summary>
internal class TopBar : Window
{
    public const double BarH = 30;
    const double PillH = 24;

    readonly string monitorDevice;
    MonitorInfo mon;
    IntPtr hwnd;
    double dpi = 1;
    public string MonitorDevice => monitorDevice;

    readonly StackPanel trayRow = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly Border trayPill, clockPill, statusPill, dayLine;
    readonly TextBlock time, date, readout;
    readonly StackPanel clockRow;
    readonly WifiGlyph wifi = new();
    readonly TextBlock ethernet, btGlyph, batText;
    readonly Border wifiChip, btChip, batChip;
    readonly BatteryRing battery = new();

    readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(1) };
    uint appBarMsg;
    bool appBarRegistered, fullscreen;
    int tickCount;
    string netLabel = "", btLabel = "", batLabel = "";

    static Radio bt;
    static bool btLookupStarted;
    static event Action BtChanged;

    public TopBar(MonitorInfo monitor)
    {
        mon = monitor; monitorDevice = monitor.Device;
        Title = "Harbor Top Bar";
        WindowStyle = WindowStyle.None; AllowsTransparency = true; ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // acrylic shows through; 1/255 keeps it hit-testable
        ShowInTaskbar = false; Topmost = true; ShowActivated = false;
        Left = 0; Top = -2000; Width = 800; Height = BarH;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        // ── tray pill (left) ──
        trayPill = Pill(trayRow);
        trayPill.HorizontalAlignment = HorizontalAlignment.Left;
        trayPill.Margin = new Thickness(8, 0, 0, 0);

        // ── clock pill (centre) — also the read-out ──
        time = Ui.Text("", 12.5, Brushes.White, FontWeights.SemiBold);
        date = Ui.Text("", 12, Ui.B(0xB3FFFFFF), FontWeights.Normal);
        date.Margin = new Thickness(8, 0, 0, 0);
        clockRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        clockRow.Children.Add(time); clockRow.Children.Add(date);
        readout = Ui.Text("", 12, Brushes.White, FontWeights.Normal);
        readout.MaxWidth = 420; readout.Opacity = 0;
        var clockGrid = new Grid { Margin = new Thickness(6, 0, 6, 0) };
        clockGrid.Children.Add(clockRow); clockGrid.Children.Add(readout);
        clockPill = Pill(clockGrid);
        clockPill.HorizontalAlignment = HorizontalAlignment.Center;
        clockPill.Cursor = Cursors.Hand;
        Hover(clockPill, () => "Notifications & calendar");
        clockPill.MouseLeftButtonUp += (_, e) => { e.Handled = true; PressKeys(VK_LWIN, 0x4E /* N */); };
        clockPill.MouseRightButtonUp += (_, e) => { e.Handled = true; OpenUri("ms-settings:dateandtime"); };

        // ── status pill (right) ──
        ethernet = new TextBlock { Text = "\uE839", FontFamily = Ui.Icons, FontSize = 13, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        var netGrid = new Grid();
        netGrid.Children.Add(new Viewbox { Child = wifi.View, Height = 13 });
        netGrid.Children.Add(ethernet);
        wifiChip = Chip(netGrid);
        Hover(wifiChip, () => netLabel);
        wifiChip.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenUri("ms-availablenetworks:"); };
        wifiChip.MouseRightButtonUp += (_, e) => { e.Handled = true; OpenUri("ms-settings:network"); };

        btGlyph = new TextBlock { Text = "\uE702", FontFamily = Ui.Icons, FontSize = 12.5, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        btChip = Chip(btGlyph, state: true);
        Hover(btChip, () => btLabel);
        btChip.MouseEnter += (_, _) => RefreshBtDevices();
        btChip.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenBluetoothFlyout(); };
        btChip.MouseRightButtonUp += (_, e) => { e.Handled = true; ToggleBluetooth(); };

        batText = Ui.Text("", 12, Brushes.White, FontWeights.SemiBold);
        batText.Margin = new Thickness(5, 0, 0, 0);
        var batRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        batRow.Children.Add(new Viewbox { Child = battery.View, Height = 15 }); batRow.Children.Add(batText);
        batChip = Chip(batRow, wide: true);
        Hover(batChip, () => batLabel);
        batChip.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenUri("ms-settings:batterysaver"); };

        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        statusRow.Children.Add(wifiChip); statusRow.Children.Add(btChip); statusRow.Children.Add(batChip);
        statusPill = Pill(statusRow);
        statusPill.HorizontalAlignment = HorizontalAlignment.Right;
        statusPill.Margin = new Thickness(0, 0, 8, 0);

        // ── the tide line: fills across the bottom edge as the day passes ──
        dayLine = new Border
        {
            Height = 1.5, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left,
            Background = new LinearGradientBrush(Ui.C(0x0064D2FF), Ui.C(0xCC64D2FF), 0) { GradientStops = { new GradientStop(Ui.C(0x0064D2FF), 0), new GradientStop(Ui.C(0x9964D2FF), 0.6), new GradientStop(Ui.C(0xE6BF5AF2), 1) } }
        };

        var root = new Grid();
        root.Children.Add(new Border { BorderBrush = Ui.B(0x1FFFFFFF), BorderThickness = new Thickness(0, 0, 0, 1) });
        root.Children.Add(trayPill); root.Children.Add(clockPill); root.Children.Add(statusPill);
        root.Children.Add(dayLine);
        root.SizeChanged += (_, _) => UpdateDayLine();
        Content = root;

        SourceInitialized += OnSourceInitialized;
        tick.Tick += (_, _) => OnTick();
        TrayHost.Changed += OnTrayChanged;
        NetworkInformation.NetworkStatusChanged += OnNetworkChanged;
        BtChanged += OnBtChanged;
    }

    /// <summary>Creates the window, reserves the strip and starts the clock.</summary>
    public void Init()
    {
        Show();
        if (!btLookupStarted) { btLookupStarted = true; FindBluetooth(); }
    }

    void OnSourceInitialized(object s, EventArgs e)
    {
        hwnd = new WindowInteropHelper(this).Handle;
        long ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)((ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE) & ~WS_EX_APPWINDOW));
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        EnableAcrylic(hwnd, 0x8C1A1716); // AABBGGRR: graphite, a touch darker than the dock
        // adopt this monitor's DPI before sizing
        SetWindowPos(hwnd, IntPtr.Zero, mon.Bounds.Left + 10, mon.Bounds.Top + 10, 100, 30, SWP_NOZORDER | SWP_NOACTIVATE);
        dpi = mon.Dpi;

        appBarMsg = RegisterWindowMessageW("HarborTopBarMessage");
        var abd = NewAbd(); abd.uCallbackMessage = appBarMsg;
        TrayHost.AppBarMessage(ABM_NEW, ref abd);
        appBarRegistered = true;
        SetAppBarPos();

        RebuildTray();
        RefreshAll();
        tick.Start();
    }

    public void Cleanup()
    {
        tick.Stop();
        TrayHost.Changed -= OnTrayChanged;
        NetworkInformation.NetworkStatusChanged -= OnNetworkChanged;
        BtChanged -= OnBtChanged;
        if (appBarRegistered) { var abd = NewAbd(); TrayHost.AppBarMessage(ABM_REMOVE, ref abd); appBarRegistered = false; }
    }

    // ───────────────────────────── appbar ─────────────────────────────

    APPBARDATA NewAbd() => new() { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>(), hWnd = hwnd };

    /// <summary>Reserves the strip at the top of this monitor; Windows shrinks maximized windows to fit below it.</summary>
    void SetAppBarPos()
    {
        if (hwnd == IntPtr.Zero || !appBarRegistered) return;
        mon = GetMonitors().FirstOrDefault(x => x.Device == monitorDevice) ?? mon;
        dpi = mon.Dpi;
        var b = mon.Bounds;
        int h = (int)Math.Round(BarH * dpi);
        var abd = NewAbd();
        abd.uEdge = 1; // ABE_TOP
        abd.rc = new RECT { Left = b.Left, Top = b.Top, Right = b.Right, Bottom = b.Top + h };
        TrayHost.AppBarMessage(ABM_QUERYPOS, ref abd);
        abd.rc.Bottom = abd.rc.Top + h;
        TrayHost.AppBarMessage(ABM_SETPOS, ref abd);
        SetWindowPos(hwnd, IntPtr.Zero, abd.rc.Left, abd.rc.Top, abd.rc.Right - abd.rc.Left, h, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == appBarMsg && appBarMsg != 0)
        {
            if ((uint)w == ABN_POSCHANGED) Dispatcher.BeginInvoke(SetAppBarPos);
            else if ((uint)w == ABN_FULLSCREENAPP) { bool fs = l != IntPtr.Zero; Dispatcher.BeginInvoke(() => SetFullscreen(fs)); }
        }
        else if (msg == 0x007E || msg == 0x02E0) // WM_DISPLAYCHANGE, WM_DPICHANGED
            Dispatcher.BeginInvoke(DispatcherPriority.Background, SetAppBarPos);
        else if (msg == 0x0021) { handled = true; return (IntPtr)3; } // WM_MOUSEACTIVATE → never steal focus
        return IntPtr.Zero;
    }

    void SetFullscreen(bool fs)
    {
        if (fs && !FullscreenAppHere()) return; // a fullscreen app on another monitor
        if (fs == fullscreen) return;
        fullscreen = fs;
        if (fs) { Hide(); tick.Stop(); }
        else { Show(); tick.Start(); RefreshAll(); SetAppBarPos(); }
    }

    bool FullscreenAppHere()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        if (GetClassName(fg) is "Progman" or "WorkerW") return false;
        GetWindowRect(fg, out var r);
        var b = mon.Bounds;
        return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
    }

    // ───────────────────────────── content ─────────────────────────────

    void OnTick()
    {
        tickCount++;
        RefreshClock();
        if (tickCount % 30 == 0) RefreshBattery();
        if (tickCount % 20 == 0 && wifi.View.Visibility == Visibility.Visible) RefreshNetwork(); // signal strength drifts
    }

    void RefreshAll() { RefreshClock(); RefreshBattery(); RefreshNetwork(); RefreshBluetooth(); RefreshBtDevices(); }

    void RefreshClock()
    {
        var now = DateTime.Now;
        bool h24 = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('H');
        string t = now.ToString(h24 ? "HH:mm" : "h:mm tt");
        if (time.Text != t) { time.Text = t; UpdateDayLine(); }
        string d = now.ToString("ddd, MMM d");
        if (date.Text != d) date.Text = d;
    }

    void UpdateDayLine()
    {
        if (Content is FrameworkElement root && root.ActualWidth > 0)
            dayLine.Width = root.ActualWidth * DateTime.Now.TimeOfDay.TotalMinutes / 1440.0;
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
            if (prof != null && prof.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.InternetAccess) netLabel += "  ·  no internet";
        }
        catch (Exception ex) { Log.Write("topbar net: " + ex.Message); netLabel = "Network"; }
        if (hoverSource == wifiChip) ShowReadout(netLabel);
    }

    void ShowNet(bool wifiMode)
    {
        wifi.View.Visibility = wifiMode ? Visibility.Visible : Visibility.Collapsed;
        ethernet.Visibility = wifiMode ? Visibility.Collapsed : Visibility.Visible;
    }

    void OnNetworkChanged(object sender) => Dispatcher.BeginInvoke(RefreshNetwork);

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
        btChip.Background = on ? Ui.B(0xFF0A84FF) : Brushes.Transparent;
        btGlyph.Foreground = on ? Brushes.White : Ui.B(0x80FFFFFF);
        if (bt == null) btLabel = "Bluetooth  ·  not available";
        else if (!on) btLabel = "Bluetooth off  ·  right-click to turn on";
        else if (btConnected.Count == 0) btLabel = "Bluetooth on  ·  no devices connected";
        else btLabel = "Connected: " + string.Join(", ", btConnected);
        if (hoverSource == btChip) ShowReadout(btLabel);
    }

    /// <summary>Names of connected Bluetooth devices (classic + Low Energy).</summary>
    async void RefreshBtDevices()
    {
        if (bt?.State != RadioState.On) { btConnected = new(); RefreshBluetooth(); return; }
        var names = new List<string>();
        try
        {
            foreach (var sel in new[]
            {
                BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
                BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected)
            })
                foreach (var d in await DeviceInformation.FindAllAsync(sel))
                    if (!string.IsNullOrWhiteSpace(d.Name) && !names.Contains(d.Name)) names.Add(d.Name);
        }
        catch (Exception ex) { Log.Write("topbar bt devices: " + ex.Message); return; }
        // the await may resume on a pool thread (e.g. before the UI loop starts), so hop back to the UI thread
        _ = Dispatcher.BeginInvoke(() => { btConnected = names; RefreshBluetooth(); });
    }

    /// <summary>Opens Windows' own Bluetooth device list (Quick Settings › Bluetooth), like the Wi-Fi chip does.</summary>
    static void OpenBluetoothFlyout()
    {
        try { Process.Start(new ProcessStartInfo("ms-actioncenter:controlcenter/bluetooth") { UseShellExecute = true }); }
        catch { OpenUri("ms-settings:bluetooth"); }
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
        _ = Dispatcher.BeginInvoke(RefreshBluetooth);
    }

    void OnTrayChanged() => RebuildTray();

    void RebuildTray()
    {
        trayRow.Children.Clear();
        var icons = TrayHost.Icons.Where(i => !i.Hidden && i.Icon != null).ToList();
        if (icons.Count == 0)
        {
            var empty = Ui.Text(TrayHost.Running ? "No tray icons" : "Tray off", 11.5, Ui.B(0x80FFFFFF), FontWeights.Normal);
            empty.Margin = new Thickness(8, 0, 8, 0);
            trayRow.Children.Add(empty);
            return;
        }
        foreach (var ic in icons)
        {
            var img = new Image { Source = ic.Icon, Width = 16, Height = 16, Stretch = Stretch.Uniform };
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

    FrameworkElement hoverSource;

    void Hover(FrameworkElement el, Func<string> text)
    {
        el.MouseEnter += (_, _) =>
        {
            hoverSource = el;
            if (el != clockPill) ShowReadout(text());
            if (el is Border b && b.Tag == null && el != clockPill) b.Background = Ui.B(0x26FFFFFF);
        };
        el.MouseLeave += (_, _) =>
        {
            if (hoverSource == el) { hoverSource = null; ShowReadout(null); }
            if (el is Border b && b.Tag == null && el != clockPill) b.Background = Brushes.Transparent;
        };
    }

    /// <summary>Cross-fades the clock pill between the time/date and a hovered item's details (null = back to time).</summary>
    void ShowReadout(string text)
    {
        bool show = !string.IsNullOrEmpty(text);
        if (show) readout.Text = text;
        var d = TimeSpan.FromMilliseconds(140);
        readout.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, d));
        clockRow.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 0 : 1, d));
    }

    // ───────────────────────────── building blocks ─────────────────────────────

    static Border Pill(UIElement child) => new()
    {
        Height = PillH, CornerRadius = new CornerRadius(PillH / 2), Padding = new Thickness(4, 0, 4, 0),
        VerticalAlignment = VerticalAlignment.Center, Child = child,
        Background = Ui.B(0x1AFFFFFF), BorderBrush = Ui.B(0x1FFFFFFF), BorderThickness = new Thickness(0.8)
    };

    /// <summary>A round hover target. Chips with a coloured state (Bluetooth) are tagged so hover doesn't repaint them.</summary>
    static Border Chip(UIElement child, bool wide = false, bool state = false) => new()
    {
        Height = 20, MinWidth = 26, CornerRadius = new CornerRadius(10), Margin = new Thickness(1, 0, 1, 0),
        Padding = wide ? new Thickness(5, 0, 7, 0) : new Thickness(0),
        Background = Brushes.Transparent, Child = child, Cursor = Cursors.Hand, Tag = state ? "state" : null
    };

    static void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("open " + uri + ": " + ex.Message); }
    }

    /// <summary>Wi-Fi strength drawn as three arcs over a dot (vector, so it stays sharp at any DPI).</summary>
    class WifiGlyph
    {
        public readonly Canvas View = new() { Width = 24, Height = 19 };
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
                arcs[i] = new Path { Data = geo, StrokeThickness = 2.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
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
        public readonly Grid View = new() { Width = 24, Height = 24 };
        readonly Path arc = new() { StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        readonly TextBlock bolt = new() { Text = "\uE945", FontFamily = Ui.Icons, FontSize = 11, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
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
