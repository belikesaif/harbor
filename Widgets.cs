using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Windows.Media.Control;
using static Harbor.Native;

namespace Harbor;

/// <summary>All widgets are vector art designed on a 64px-tall canvas and scaled by a Viewbox,
/// so they stay razor sharp at any icon size or magnification.</summary>
internal static class Ui
{
    public const double H = 64;
    public static readonly FontFamily Font = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static SolidColorBrush B(uint argb)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze(); return b;
    }

    public static LinearGradientBrush Grad(uint top, uint bottom)
    {
        var g = new LinearGradientBrush(C(top), C(bottom), 90); g.Freeze(); return g;
    }
    public static Color C(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    public static TextBlock Text(string s, double size, Brush fg, FontWeight w) => new()
    {
        Text = s, FontSize = size, Foreground = fg, FontWeight = w, FontFamily = Font,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    public static Border Tile(double width, Brush bg, UIElement child, Brush stroke = null) => new()
    {
        // 22.5% corner radius ≈ the macOS app-icon squircle, so widgets sit naturally next to real app icons
        Width = width, Height = H, CornerRadius = new CornerRadius(14.4), Background = bg,
        BorderBrush = stroke ?? new LinearGradientBrush(C(0x40FFFFFF), C(0x0FFFFFFF), 90), BorderThickness = new Thickness(0.8), Child = child, ClipToBounds = true
    };

    public static Viewbox Wrap(FrameworkElement e) => new() { Child = e, Stretch = Stretch.Uniform };
}

internal class CalendarTile
{
    public readonly Viewbox View;
    readonly TextBlock day, num;
    public CalendarTile()
    {
        day = Ui.Text("", 11, Ui.B(0xFFFF3B30), FontWeights.SemiBold);
        num = Ui.Text("", 33, Ui.B(0xFF1C1C1E), FontWeights.Light);
        day.VerticalAlignment = VerticalAlignment.Top; day.Margin = new Thickness(0, 6, 0, 0);
        num.Margin = new Thickness(0, 12, 0, 0);
        var g = new Grid(); g.Children.Add(day); g.Children.Add(num);
        View = Ui.Wrap(Ui.Tile(64, Ui.Grad(0xFFFFFFFF, 0xFFE9E9EE), g, Ui.B(0x33000000)));
        Update();
    }
    public void Update()
    {
        var now = DateTime.Now;
        day.Text = now.ToString("ddd").ToUpperInvariant();
        num.Text = now.Day.ToString();
    }
}

internal class ClockTile
{
    public readonly Viewbox View;
    readonly RotateTransform hr = new(0, 32, 32), mn = new(0, 32, 32), sc = new(0, 32, 32);
    public ClockTile()
    {
        var c = new Canvas { Width = 64, Height = 64 };
        var face = new Ellipse { Width = 54, Height = 54, Fill = Ui.Grad(0xFFFFFFFF, 0xFFF0F0F3) };
        Canvas.SetLeft(face, 5); Canvas.SetTop(face, 5); c.Children.Add(face);
        for (int i = 0; i < 12; i++)
        {
            bool major = i % 3 == 0;
            var tick = new Rectangle { Width = major ? 2 : 1.2, Height = major ? 5 : 3.5, Fill = Ui.B(0xFF1C1C1E), RadiusX = 0.6, RadiusY = 0.6 };
            tick.RenderTransform = new RotateTransform(i * 30, tick.Width / 2, 27);
            Canvas.SetLeft(tick, 32 - tick.Width / 2); Canvas.SetTop(tick, 5); c.Children.Add(tick);
        }
        c.Children.Add(Hand(2.6, 15, Ui.B(0xFF1C1C1E), hr));
        c.Children.Add(Hand(2, 22, Ui.B(0xFF1C1C1E), mn));
        c.Children.Add(secondHand = Hand(1, 24, Ui.B(0xFFFF9F0A), sc));
        var pin = new Ellipse { Width = 4, Height = 4, Fill = Ui.B(0xFFFF9F0A) };
        Canvas.SetLeft(pin, 30); Canvas.SetTop(pin, 30); c.Children.Add(pin);
        View = Ui.Wrap(Ui.Tile(64, Ui.Grad(0xFF3A3A3C, 0xFF1C1C1E), c));
        Update();
    }
    static Rectangle Hand(double w, double len, Brush fill, RotateTransform rt)
    {
        var r = new Rectangle { Width = w, Height = len + 4, Fill = fill, RadiusX = w / 2, RadiusY = w / 2 };
        Canvas.SetLeft(r, 32 - w / 2); Canvas.SetTop(r, 32 - len);
        rt.CenterX = w / 2; rt.CenterY = len; r.RenderTransform = rt;
        return r;
    }
    Rectangle secondHand;
    bool seconds = true;
    public bool ShowSeconds
    {
        get => seconds;
        set { seconds = value; if (secondHand != null) secondHand.Visibility = value ? Visibility.Visible : Visibility.Collapsed; Update(); }
    }

    // WPF only re-renders when a value actually changes, so with seconds off this costs one redraw per minute.
    public void Update()
    {
        var n = DateTime.Now;
        if (seconds) sc.Angle = n.Second * 6;
        mn.Angle = n.Minute * 6 + (seconds ? n.Second * 0.1 : 0);
        hr.Angle = (n.Hour % 12) * 30 + n.Minute * 0.5;
    }
    public static string Label() => DateTime.Now.ToString("h:mm tt  ·  dddd, MMM d");
}

/// <summary>Always-visible digital time for the dock: big hours:minutes, AM/PM (or seconds) underneath.</summary>
internal class DigitalClockTile
{
    public readonly Viewbox View;
    public const double Aspect = 104 / Ui.H;
    readonly TextBlock hm, sub;
    readonly Border pulse;
    public bool ShowSeconds { get; set; }

    public DigitalClockTile()
    {
        hm = Ui.Text("", 30, Brushes.White, FontWeights.Light);
        hm.Margin = new Thickness(0, -3, 0, 0);
        sub = Ui.Text("", 9.5, Ui.B(0xB3FFFFFF), FontWeights.SemiBold);
        sub.Margin = new Thickness(0, -4, 0, 0);
        // thin accent line that fills as the minute passes: the only moving part, redrawn once per second
        pulse = new Border { Height = 2, CornerRadius = new CornerRadius(1), HorizontalAlignment = HorizontalAlignment.Left, Background = new LinearGradientBrush(Ui.C(0xFF64D2FF), Ui.C(0xFFBF5AF2), 0) };
        var track = new Grid { Width = 56, Height = 2, Margin = new Thickness(0, 3, 0, 0) };
        track.Children.Add(new Border { CornerRadius = new CornerRadius(1), Background = Ui.B(0x26FFFFFF) });
        track.Children.Add(pulse);
        var s = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        s.Children.Add(hm); s.Children.Add(sub); s.Children.Add(track);
        View = Ui.Wrap(Ui.Tile(104, Ui.Grad(0xFF3A3A3C, 0xFF1C1C1E), s));
        Update();
    }

    public void Update()
    {
        var n = DateTime.Now;
        bool h24 = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('H');
        string t = n.ToString(h24 ? "HH:mm" : "h:mm");
        if (hm.Text != t) hm.Text = t;
        string st = ShowSeconds ? n.ToString(h24 ? "ss" : "ss  ·  tt") : h24 ? n.ToString("ddd").ToUpperInvariant() : n.ToString("tt");
        if (sub.Text != st) sub.Text = st;
        pulse.Width = 56 * n.Second / 59.0;
    }
}

internal class LaunchpadTile
{
    public readonly Viewbox View;
    public LaunchpadTile()
    {
        uint[] colors = { 0xFFFF453A, 0xFFFF9F0A, 0xFFFFD60A, 0xFF30D158, 0xFF64D2FF, 0xFF0A84FF, 0xFF5E5CE6, 0xFFBF5AF2, 0xFFFF375F };
        var g = new UniformGrid3();
        foreach (var col in colors) g.Add(new Border { Width = 11, Height = 11, CornerRadius = new CornerRadius(3.5), Background = Ui.B(col), Margin = new Thickness(2.2) });
        View = Ui.Wrap(Ui.Tile(64, Ui.Grad(0xFF48484A, 0xFF1C1C1E), g.Panel));
    }
    class UniformGrid3
    {
        public readonly System.Windows.Controls.Primitives.UniformGrid Panel = new() { Rows = 3, Columns = 3, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        public void Add(UIElement e) => Panel.Children.Add(e);
    }
}

internal class ControlCenterTile
{
    public readonly Viewbox View;
    public ControlCenterTile()
    {
        var s = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        s.Children.Add(Toggle(true)); s.Children.Add(Toggle(false));
        View = Ui.Wrap(Ui.Tile(64, Ui.Grad(0xFF636366, 0xFF2C2C2E), s));
    }
    static Grid Toggle(bool on)
    {
        var g = new Grid { Width = 34, Height = 16, Margin = new Thickness(0, 3, 0, 3) };
        g.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = on ? Ui.B(0xFF0A84FF) : Ui.B(0x66FFFFFF) });
        g.Children.Add(new Ellipse { Width = 12, Height = 12, Fill = Brushes.White, HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(2, 0, 2, 0) });
        return g;
    }
}

/// <summary>CPU / RAM / Battery rings. Reads counters straight from the kernel (no WMI, no PerfCounters).</summary>
internal class SystemWidget
{
    public readonly Viewbox View;
    public readonly double Aspect;
    readonly Ring cpu, ram, bat;
    long lastIdle, lastKernel, lastUser;
    public int Cpu, Ram, Battery = -1; public bool Charging;
    readonly bool hasBattery;

    public SystemWidget()
    {
        GetSystemPowerStatus(out var ps);
        hasBattery = ps.BatteryFlag != 128 && ps.BatteryLifePercent <= 100;
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        cpu = new Ring("CPU", 0xFF0A84FF); ram = new Ring("RAM", 0xFF30D158);
        row.Children.Add(cpu.Root); row.Children.Add(ram.Root);
        if (hasBattery) { bat = new Ring("BAT", 0xFFFFD60A); row.Children.Add(bat.Root); }
        double w = hasBattery ? 150 : 104;
        Aspect = w / Ui.H;
        View = Ui.Wrap(Ui.Tile(w, Ui.Grad(0xFF3A3A3C, 0xFF1C1C1E), row));
        GetSystemTimes(out lastIdle, out lastKernel, out lastUser);
        Update();
    }

    public void Update()
    {
        GetSystemTimes(out long idle, out long kernel, out long user);
        long dIdle = idle - lastIdle, dTotal = (kernel - lastKernel) + (user - lastUser);
        lastIdle = idle; lastKernel = kernel; lastUser = user;
        if (dTotal > 0) Cpu = (int)Math.Clamp(Math.Round(100.0 * (dTotal - dIdle) / dTotal), 0, 100);
        var m = new MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref m)) Ram = (int)m.dwMemoryLoad;
        cpu.Set(Cpu, Cpu.ToString()); ram.Set(Ram, Ram.ToString());
        if (hasBattery && GetSystemPowerStatus(out var ps))
        {
            Battery = ps.BatteryLifePercent; Charging = ps.ACLineStatus == 1;
            uint col = Charging ? 0xFF30D158 : Battery <= 20 ? 0xFFFF453A : 0xFFFFD60A;
            bat.SetColor(col);
            bat.SetCaption(Charging ? "⚡ BAT" : "BAT");
            bat.Set(Battery, Battery.ToString());
        }
    }

    public string Label() => $"CPU {Cpu}%   ·   Memory {Ram}%" + (Battery >= 0 ? $"   ·   Battery {Battery}%{(Charging ? " (charging)" : "")}" : "");

    class Ring
    {
        public readonly StackPanel Root = new() { Margin = new Thickness(4, 0, 4, 0) };
        readonly System.Windows.Shapes.Path arc = new() { StrokeThickness = 4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        readonly TextBlock value, cap;
        public void SetCaption(string c) { if (cap.Text != c) cap.Text = c; }
        const double D = 36, R = 16;
        public Ring(string caption, uint color)
        {
            var g = new Grid { Width = D, Height = D };
            g.Children.Add(new Ellipse { Width = 2 * R + 4, Height = 2 * R + 4, Stroke = Ui.B(0x26FFFFFF), StrokeThickness = 4 });
            SetColor(color);
            g.Children.Add(arc);
            value = Ui.Text("", 10, Brushes.White, FontWeights.SemiBold);
            // the number always fits inside the ring (shrinks for "100")
            g.Children.Add(new Viewbox { Child = value, Width = 2 * R - 12, Height = 12, StretchDirection = StretchDirection.DownOnly });
            Root.Children.Add(g);
            cap = Ui.Text(caption, 7.5, Ui.B(0xB3FFFFFF), FontWeights.SemiBold);
            cap.Margin = new Thickness(0, 2, 0, 0);
            Root.Children.Add(cap);
        }
        public void SetColor(uint c) => arc.Stroke = Ui.B(c);
        int lastPct = -1; string lastText;
        public void Set(int pct, string text)
        {
            if (pct == lastPct && text == lastText) return; // no change → no redraw
            lastPct = pct; lastText = text;
            value.Text = text;
            double a = Math.Clamp(pct, 0, 100) / 100.0 * 359.99 * Math.PI / 180;
            double cx = D / 2, cy = D / 2;
            var start = new Point(cx, cy - R);
            var end = new Point(cx + R * Math.Sin(a), cy - R * Math.Cos(a));
            var fig = new PathFigure { StartPoint = start, IsClosed = false };
            fig.Segments.Add(new ArcSegment(end, new Size(R, R), 0, a > Math.PI, SweepDirection.Clockwise, true));
            var geo = new PathGeometry(); geo.Figures.Add(fig); geo.Freeze();
            arc.Data = pct > 0 ? geo : null;
        }
    }
}

/// <summary>Now Playing — works with Spotify, browsers, Media Player… via the system media session.</summary>
internal class MediaWidget
{
    public readonly Viewbox View;
    public const double Aspect = 176 / Ui.H;
    public event Action VisibilityChanged;
    public bool HasSession { get; private set; }
    public string Title = "", Artist = "";
    readonly TextBlock title, artist, playGlyph;
    readonly Border art;
    GlobalSystemMediaTransportControlsSessionManager mgr;
    GlobalSystemMediaTransportControlsSession session;
    readonly System.Windows.Threading.Dispatcher ui;

    public MediaWidget()
    {
        ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        art = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(9), Background = Ui.Grad(0xFFFF375F, 0xFFBF5AF2), Margin = new Thickness(9, 0, 1, 0) };
        art.Child = new TextBlock { Text = "", FontFamily = Ui.Icons, FontSize = 20, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        g.Children.Add(art);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0) };
        title = Ui.Text("", 11.5, Brushes.White, FontWeights.SemiBold); title.HorizontalAlignment = HorizontalAlignment.Left;
        artist = Ui.Text("", 9.5, Ui.B(0xB3FFFFFF), FontWeights.Normal); artist.HorizontalAlignment = HorizontalAlignment.Left;
        texts.Children.Add(title); texts.Children.Add(artist);
        Grid.SetColumn(texts, 1); g.Children.Add(texts);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        playGlyph = Glyph("", async () => { if (session != null) await session.TryTogglePlayPauseAsync(); });
        buttons.Children.Add(playGlyph);
        buttons.Children.Add(Glyph("", async () => { if (session != null) await session.TrySkipNextAsync(); }));
        Grid.SetColumn(buttons, 2); g.Children.Add(buttons);
        View = Ui.Wrap(Ui.Tile(176, Ui.Grad(0xFF3A3A3C, 0xFF1C1C1E), g));
        Init();
    }

    TextBlock Glyph(string glyph, Func<Task> action)
    {
        var t = new TextBlock { Text = glyph, FontFamily = Ui.Icons, FontSize = 15, Foreground = Brushes.White, Padding = new Thickness(5, 8, 5, 8), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        t.MouseLeftButtonDown += (_, e) => e.Handled = true;
        t.MouseLeftButtonUp += async (_, e) => { e.Handled = true; try { await action(); } catch { } };
        t.MouseEnter += (_, _) => t.Opacity = 0.7; t.MouseLeave += (_, _) => t.Opacity = 1;
        return t;
    }

    public async void TogglePlay() { try { if (session != null) await session.TryTogglePlayPauseAsync(); } catch { } }

    async void Init()
    {
        try
        {
            mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            mgr.CurrentSessionChanged += (_, _) => ui.BeginInvoke(Attach);
            ui.BeginInvoke(Attach);
        }
        catch (Exception ex) { Log.Write("media: " + ex.Message); }
    }

    void Attach()
    {
        if (session != null) { session.MediaPropertiesChanged -= OnProps; session.PlaybackInfoChanged -= OnPlayback; }
        session = mgr?.GetCurrentSession();
        if (session != null) { session.MediaPropertiesChanged += OnProps; session.PlaybackInfoChanged += OnPlayback; }
        Refresh();
    }
    void OnProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs a) => ui.BeginInvoke(Refresh);
    void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs a) => ui.BeginInvoke(RefreshPlayback);

    void RefreshPlayback()
    {
        try
        {
            var st = session?.GetPlaybackInfo()?.PlaybackStatus;
            playGlyph.Text = st == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ? "" : "";
        }
        catch { }
    }

    async void Refresh()
    {
        bool had = HasSession;
        try
        {
            if (session == null) { HasSession = false; }
            else
            {
                var p = await session.TryGetMediaPropertiesAsync();
                Title = p?.Title ?? ""; Artist = p?.Artist ?? "";
                HasSession = Title.Length > 0;
                title.Text = Title; artist.Text = Artist.Length > 0 ? Artist : "Now Playing";
                RefreshPlayback();
                if (p?.Thumbnail != null)
                {
                    using var ras = await p.Thumbnail.OpenReadAsync();
                    using var s = ras.AsStream();
                    var ms = new MemoryStream(); await s.CopyToAsync(ms); ms.Position = 0;
                    var bmp = new BitmapImage();
                    bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.DecodePixelWidth = 128; bmp.StreamSource = ms; bmp.EndInit(); bmp.Freeze();
                    art.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                    art.Child = null;
                }
                else
                {
                    art.Background = Ui.Grad(0xFFFF375F, 0xFFBF5AF2);
                    art.Child ??= new TextBlock { Text = "", FontFamily = Ui.Icons, FontSize = 20, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                }
            }
        }
        catch (Exception ex) { Log.Write("media refresh: " + ex.Message); }
        if (had != HasSession) VisibilityChanged?.Invoke();
    }
}
