using System.Runtime.InteropServices;
using static Harbor.Native;

namespace Harbor;

/// <summary>Win+1…9 open the Nth dock app, like the Windows taskbar (whose own shortcuts stop working while it's
/// hidden). A low-level keyboard hook sees Win+digit first and swallows it, so Windows doesn't act on it too.</summary>
internal static class Hotkeys
{
    static LowLevelProc proc;
    static IntPtr hook;
    public static Action<int> OnPin;

    public static void Start()
    {
        if (hook != IntPtr.Zero) return;
        proc = Callback;
        hook = SetWindowsHookExW(13 /* WH_KEYBOARD_LL */, proc, GetModuleHandleW(null), 0);
        if (hook == IntPtr.Zero) Log.Write("hotkeys: hook failed " + Marshal.GetLastWin32Error());
    }

    public static void Stop()
    {
        if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT { public uint vk, scan, flags, time; public IntPtr extra; }

    static IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = (int)wParam;
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool down = msg == 0x100 || msg == 0x104, up = msg == 0x101 || msg == 0x105;
            if ((down || up) && k.vk >= 0x31 && k.vk <= 0x39)
            {
                bool win = (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;
                bool other = (GetAsyncKeyState(0x10) & 0x8000) != 0 || (GetAsyncKeyState(0x11) & 0x8000) != 0 || (GetAsyncKeyState(0x12) & 0x8000) != 0;
                if (win && !other)
                {
                    if (down)
                    {
                        int index = (int)k.vk - 0x31;
                        // Windows only saw "Win down … Win up" → it would open Start on release. An unassigned key
                        // in between makes it a combination, so Start stays closed.
                        keybd_event(0xE8, 0, 0, UIntPtr.Zero); keybd_event(0xE8, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => OnPin?.Invoke(index));
                    }
                    return (IntPtr)1; // swallow: Windows' own Win+digit would act on the hidden taskbar
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }
}
