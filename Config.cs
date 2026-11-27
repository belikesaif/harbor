using System.IO;
using System.Text.Json;

namespace Harbor;

public class PinConfig
{
    public string Path { get; set; }
    public string Name { get; set; }
}

public class Config
{
    public static readonly string Dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Harbor");
    static string FilePath => System.IO.Path.Combine(Dir, "config.json");

    public double IconSize { get; set; } = 54;
    public double Magnification { get; set; } = 1.75;
    public bool ShowWidgets { get; set; } = true;
    public bool ClockSeconds { get; set; } = false;
    public bool HideWindowsTaskbar { get; set; } = true;
    public bool ShowTopBar { get; set; } = true;
    /// <summary>macOS "Automatically hide and show the Dock".</summary>
    public bool AutoHide { get; set; } = false;
    /// <summary>"Digital", "Analog" or "Both".</summary>
    public string ClockStyle { get; set; } = "Digital";
    public int OriginalTaskbarState { get; set; } = -1;
    public List<PinConfig> Pins { get; set; } = new();

    static readonly JsonSerializerOptions opts = new() { WriteIndented = true };

    /// <summary>Set once Store/MSIX taskbar pins have been imported (older versions only read the .lnk folder).</summary>
    public bool StorePinsImported { get; set; } = false;

    /// <summary>Windows taskbar pins Harbor has already mirrored, so "Pin to taskbar" / "Unpin from taskbar" in the
    /// Start menu keep the dock in sync, while pins the user removed from the dock itself don't come back.</summary>
    public List<string> SeenTaskbarPins { get; set; }

    static string pinSignature;

    /// <summary>Cheap check (a directory listing + one registry read) whether the taskbar pins changed.
    /// Applies new pins / removed pins to <see cref="Pins"/> and returns true if the dock must reload.</summary>
    public bool SyncTaskbarPins()
    {
        string sig;
        try
        {
            string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
            var files = Directory.Exists(folder) ? string.Join("|", Directory.GetFiles(folder, "*.lnk").OrderBy(f => f)) : "";
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband");
            var blob = k?.GetValue("Favorites") as byte[];
            sig = files + "#" + (blob == null ? "" : Convert.ToBase64String(System.Security.Cryptography.SHA1.HashData(blob)));
        }
        catch { return false; }
        if (sig == pinSignature) return false;
        bool first = pinSignature == null;
        pinSignature = sig;

        var current = TaskbarPins();
        var cmp = StringComparer.OrdinalIgnoreCase;
        if (SeenTaskbarPins == null)
        {
            // first run of a version with syncing: treat what's already in the dock as seen, so taskbar pins added
            // while Harbor couldn't see them (e.g. via the Start menu) are picked up now
            SeenTaskbarPins = current.Where(c => Pins.Any(p => cmp.Equals(p.Path, c.Path))).Select(c => c.Path).ToList();
        }
        bool changed = false;
        foreach (var c in current.Where(c => !SeenTaskbarPins.Contains(c.Path, cmp)))
        {
            if (!Pins.Any(p => cmp.Equals(p.Path, c.Path))) { Pins.Add(c); changed = true; }
            SeenTaskbarPins.Add(c.Path);
        }
        // unpinned from the taskbar (Start menu → "Unpin from taskbar") → remove from the dock as well
        foreach (var gone in SeenTaskbarPins.Where(s => !current.Any(c => cmp.Equals(c.Path, s))).ToList())
        {
            SeenTaskbarPins.Remove(gone);
            if (Pins.RemoveAll(p => cmp.Equals(p.Path, gone)) > 0) changed = true;
        }
        if (changed || first) Save();
        return changed;
    }

    public static Config Load()
    {
        Directory.CreateDirectory(Dir);
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) ?? Default();
                if (!loaded.StorePinsImported)
                {
                    // one-time catch-up for configs created before Store apps (WhatsApp, Spotify…) were imported
                    foreach (var p in TaskbarPins().Where(t => t.Path.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase)))
                        if (!loaded.Pins.Any(x => string.Equals(x.Path, p.Path, StringComparison.OrdinalIgnoreCase))) loaded.Pins.Add(p);
                    loaded.StorePinsImported = true;
                    loaded.Save();
                }
                return loaded;
            }
        }
        catch (Exception ex) { Log.Write("config load: " + ex.Message); }
        var c = Default();
        c.Save();
        return c;
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, opts)); } catch (Exception ex) { Log.Write("config save: " + ex.Message); }
    }

    /// <summary>First run: import the user's existing Windows taskbar pins.</summary>
    static Config Default()
    {
        var c = new Config { StorePinsImported = true };
        c.Pins.AddRange(TaskbarPins());
        c.SeenTaskbarPins = c.Pins.Select(p => p.Path).ToList();
        if (c.Pins.Count == 0)
            c.Pins.Add(new PinConfig { Path = @"C:\Windows\explorer.exe", Name = "File Explorer" });
        return c;
    }

    /// <summary>The user's Windows taskbar pins, in taskbar order: classic apps (.lnk files in the "User Pinned"
    /// folder) plus Store/MSIX apps such as WhatsApp or Spotify, which only live in the Taskband "Favorites" blob.</summary>
    static List<PinConfig> TaskbarPins()
    {
        var found = new List<(int order, PinConfig pin)>();
        string text = "";
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband");
            if (k?.GetValue("Favorites") is byte[] blob) text = System.Text.Encoding.Unicode.GetString(blob);
        }
        catch { }

        string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
        try
        {
            if (Directory.Exists(folder))
                foreach (var f in Directory.GetFiles(folder, "*.lnk"))
                {
                    string file = System.IO.Path.GetFileName(f);
                    int at = text.IndexOf(file, StringComparison.OrdinalIgnoreCase);
                    // not in the blob (unusual): File Explorer first, everything else after the known pins
                    if (at < 0) at = file.StartsWith("File Explorer", StringComparison.OrdinalIgnoreCase) ? -1 : int.MaxValue;
                    found.Add((at, new PinConfig { Path = f, Name = System.IO.Path.GetFileNameWithoutExtension(f) }));
                }
        }
        catch { }

        // Store apps appear in the blob as their AUMID, e.g. "5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App"
        try
        {
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"[A-Za-z0-9][\w.\-]*_[a-z0-9]{13}![\w.\-]+"))
            {
                string path = @"shell:AppsFolder\" + m.Value;
                if (found.Any(x => x.pin.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
                found.Add((m.Index, new PinConfig { Path = path, Name = Shell.GetDisplayName(path) ?? m.Value.Split('_')[0].Split('.').Last() }));
            }
        }
        catch { }

        return found.OrderBy(x => x.order).Select(x => x.pin).ToList();
    }
}
