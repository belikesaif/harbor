using System.Numerics;
using System.Runtime.InteropServices;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using static Harbor.Native;

namespace Harbor;

/// <summary>The frosted-glass layer behind the dock. A separate window so the blur can be clipped to a rounded shape
/// while icons are free to magnify above it.
///
/// On Windows 11, DWM ignores window regions for the legacy acrylic/blur "accent" effect, which left a square grey
/// slab behind the rounded dock. So the blur is drawn with Windows.UI.Composition instead: a host-backdrop (real
/// desktop blur) visual plus a tint, clipped by an anti-aliased rounded rectangle of any radius. If composition is
/// unavailable, it falls back to the old accent + region path.</summary>
public class GlassWindow
{
    const string ClassName = "HarborDockGlass";
    const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    static WndProcDelegate wndProc; // kept alive for the lifetime of the class registration
    static bool registered;
    static object dispatcherQueueController;
    static Compositor compositor;

    IntPtr hwnd;
    public IntPtr Handle => hwnd;
    int lw, lh, lr;

    // composition path
    DesktopWindowTarget target;
    ContainerVisual rootVisual;
    SpriteVisual blur, tint;
    CompositionRoundedRectangleGeometry clipShape;
    readonly bool composed;

    public GlassWindow()
    {
        IntPtr inst = GetModuleHandleW(null);
        if (!registered)
        {
            wndProc = (h, msg, w, l) => msg switch
            {
                0x0084 => (IntPtr)(-1), // WM_NCHITTEST → HTTRANSPARENT (never takes clicks)
                0x0021 => (IntPtr)3,    // WM_MOUSEACTIVATE → MA_NOACTIVATE
                0x0014 => (IntPtr)1,    // WM_ERASEBKGND: nothing to paint, composition draws everything
                _ => DefWindowProcW(h, msg, w, l)
            };
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
                hInstance = inst, lpszClassName = ClassName
            };
            RegisterClassExW(ref wc);
            registered = true;
        }

        bool tryComposition = Environment.GetEnvironmentVariable("HARBOR_LEGACY_GLASS") != "1";
        uint ex = WS_EX_TOOLWINDOW_U | WS_EX_NOACTIVATE_U | WS_EX_TOPMOST | (uint)WS_EX_TRANSPARENT;
        hwnd = CreateWindowExW(ex | (tryComposition ? WS_EX_NOREDIRECTIONBITMAP : 0), ClassName, "Harbor Glass",
            WS_POPUP, -32000, -32000, 10, 10, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);

        if (tryComposition)
        {
            try { InitComposition(); composed = true; }
            catch (Exception e)
            {
                Log.Write("composition glass unavailable, using legacy acrylic: " + e.Message);
                // a no-redirection window can't show the legacy accent → recreate it as a normal window
                DestroyWindow(hwnd);
                hwnd = CreateWindowExW(ex, ClassName, "Harbor Glass", WS_POPUP, -32000, -32000, 10, 10, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            }
        }
        if (!composed) EnableAcrylic(hwnd, 0x66221E1E); // AABBGGRR: graphite tint
    }

    void InitComposition()
    {
        EnsureCompositor();
        int on = 1;
        DwmSetWindowAttribute(hwnd, 17 /* DWMWA_USE_HOSTBACKDROPBRUSH */, ref on, sizeof(int));

        var interop = (ICompositorDesktopInterop)Marshal.GetObjectForIUnknown(((WinRT.IWinRTObject)compositor).NativeObject.ThisPtr);
        interop.CreateDesktopWindowTarget(hwnd, true, out IntPtr raw);
        target = DesktopWindowTarget.FromAbi(raw);
        Marshal.Release(raw);

        rootVisual = compositor.CreateContainerVisual();
        rootVisual.RelativeSizeAdjustment = Vector2.One;
        // real blur of whatever is behind the window (desktop / other apps)
        blur = compositor.CreateSpriteVisual();
        blur.RelativeSizeAdjustment = Vector2.One;
        blur.Brush = compositor.CreateHostBackdropBrush();
        // graphite tint on top of the blur, like the macOS dark Dock
        tint = compositor.CreateSpriteVisual();
        tint.RelativeSizeAdjustment = Vector2.One;
        tint.Brush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0x8C, 0x1E, 0x1E, 0x22));
        rootVisual.Children.InsertAtTop(blur);
        rootVisual.Children.InsertAtTop(tint);

        clipShape = compositor.CreateRoundedRectangleGeometry();
        rootVisual.Clip = compositor.CreateGeometricClip(clipShape);
        target.Root = rootVisual;
    }

    static void EnsureCompositor()
    {
        if (compositor != null) return;
        // Composition needs a DispatcherQueue on this (the WPF UI) thread
        var opts = new DispatcherQueueOptions { dwSize = Marshal.SizeOf<DispatcherQueueOptions>(), threadType = 2 /* current */, apartmentType = 2 /* STA */ };
        Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(opts, out IntPtr ctrl));
        dispatcherQueueController = ctrl; // keep it alive for the process lifetime
        compositor = new Compositor();
    }

    public void Show() { if (hwnd != IntPtr.Zero) ShowWindow(hwnd, 4 /* SW_SHOWNOACTIVATE */); }
    public void Hide() { if (hwnd != IntPtr.Zero) ShowWindow(hwnd, 0 /* SW_HIDE */); }
    public void Close()
    {
        if (hwnd == IntPtr.Zero) return;
        try { target?.Dispose(); } catch { }
        target = null;
        DestroyWindow(hwnd); hwnd = IntPtr.Zero;
    }

    /// <summary>Moves the blur layer off-screen (used while the dock is auto-hidden).</summary>
    public void Park()
    {
        if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    public void Place(int x, int y, int w, int h, int radius)
    {
        if (hwnd == IntPtr.Zero || w <= 0 || h <= 0) return;
        SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        if (w == lw && h == lh && radius == lr) return;
        lw = w; lh = h; lr = radius;
        if (composed)
        {
            // inset by half a pixel so the dock's own hairline border sits exactly on the glass edge
            clipShape.Offset = new Vector2(0.5f, 0.5f);
            clipShape.Size = new Vector2(w - 1f, h - 1f);
            clipShape.CornerRadius = new Vector2(Math.Max(0, radius - 0.5f));
        }
        else
        {
            int r = Math.Max(0, radius - 1) * 2;
            SetWindowRgn(hwnd, CreateRoundRectRgn(1, 1, w, h, r, r), true);
        }
    }

    // ---- interop ----
    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ICompositorDesktopInterop
    {
        void CreateDesktopWindowTarget(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool isTopmost, out IntPtr target);
        void EnsureOnThread(uint threadId);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DispatcherQueueOptions { public int dwSize; public int threadType; public int apartmentType; }

    [DllImport("CoreMessaging.dll")]
    static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
