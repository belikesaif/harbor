using System.Diagnostics;
using System.IO;
using static Harbor.Native;

namespace Harbor;

public class DockEntry
{
    public string Id;
    public string Name;
    public string LaunchPath, LaunchArgs;
    public string IconPath;
    public string ExePath;
    public bool Pinned;
    public PinConfig Pin;
    // matching rules
    public string MatchExe, MatchDir, MatchAumid;
    public List<IntPtr> Windows = new();

    public bool Running => Windows.Count > 0;

    public static DockEntry FromPin(PinConfig pin)
    {
        var e = new DockEntry { Pin = pin, Pinned = true, Name = pin.Name, LaunchPath = pin.Path, IconPath = pin.Path, Id = "pin:" + pin.Path };
        string path = pin.Path;
        if (path.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase))
        {
            e.MatchAumid = path.Substring(17).ToLowerInvariant();
        }
        else if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var (target, args) = Shell.ResolveShortcut(path);
            string aumid = GetFileAumid(path);
            if (aumid != null) e.MatchAumid = aumid.ToLowerInvariant();
            if (target.Length > 0) SetExeRules(e, target, args);
            if (e.MatchExe == null && (pin.Name ?? "").Contains("File Explorer", StringComparison.OrdinalIgnoreCase))
                e.MatchExe = "explorer.exe";
        }
        else if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            SetExeRules(e, path, "");
        }
        return e;
    }

    static void SetExeRules(DockEntry e, string target, string args)
    {
        e.ExePath = target;
        string file = Path.GetFileName(target).ToLowerInvariant();
        string dir = Path.GetDirectoryName(target) ?? "";
        // Squirrel installers (GitHub Desktop, Discord…): Update.exe --processStart "App.exe"
        if (file == "update.exe" && args.Contains("--processStart", StringComparison.OrdinalIgnoreCase))
        {
            var parts = args.Split('"', ' ', StringSplitOptions.RemoveEmptyEntries);
            int i = Array.FindIndex(parts, p => p.Equals("--processStart", StringComparison.OrdinalIgnoreCase));
            if (i >= 0 && i + 1 < parts.Length) file = parts[i + 1].ToLowerInvariant();
        }
        e.MatchExe = file;
        string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        int depth = dir.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length;
        if (depth >= 4 && !dir.StartsWith(windir, StringComparison.OrdinalIgnoreCase))
            e.MatchDir = dir.ToLowerInvariant() + "\\";
    }
}

public record WinInfo(IntPtr Hwnd, string Exe, string Aumid);

/// <summary>Enumerates real, user-facing top-level windows (the same set Alt+Tab shows).</summary>
public static class WindowTracker
{
    static readonly Dictionary<uint, string> exeCache = new();
    static readonly Dictionary<uint, string> aumidCache = new();
    static readonly HashSet<string> ignoredClasses = new(StringComparer.Ordinal)
        { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow" };
    static readonly HashSet<string> ignoredExe = new(StringComparer.OrdinalIgnoreCase)
        { "shellexperiencehost.exe", "searchhost.exe", "startmenuexperiencehost.exe", "textinputhost.exe", "lockapp.exe", "searchapp.exe" };
    static readonly uint selfPid = (uint)Environment.ProcessId;

    public static List<WinInfo> Enumerate()
    {
        var list = new List<WinInfo>();
        if (exeCache.Count > 400) { exeCache.Clear(); aumidCache.Clear(); }
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
            long ex = (long)GetWindowLongPtr(h, GWL_EXSTYLE);
            if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return true;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;
            if (GetWindowTextLengthW(h) == 0) return true;
            if (ignoredClasses.Contains(Native.GetClassName(h))) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == selfPid) return true;
            if (!exeCache.TryGetValue(pid, out var exe)) exeCache[pid] = exe = GetProcessPath(pid) ?? "";
            if (exe.Length == 0 || ignoredExe.Contains(Path.GetFileName(exe))) return true;
            string aumid = null;
            bool frameHost = exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
            if (frameHost || exe.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
            {
                aumid = GetWindowAumid(h);
                if (frameHost && aumid == null) return true;
                if (aumid == null)
                {
                    if (!aumidCache.TryGetValue(pid, out aumid)) aumidCache[pid] = aumid = GetProcessAumid(pid);
                }
            }
            list.Add(new WinInfo(h, exe, aumid));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static string DescribeExe(string exe)
    {
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(exe);
            if (!string.IsNullOrWhiteSpace(vi.FileDescription)) return vi.FileDescription.Trim();
        }
        catch { }
        return Path.GetFileNameWithoutExtension(exe);
    }
}
