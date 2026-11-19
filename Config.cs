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
    /// <summary>"Digital", "Analog" or "Both".</summary>
    public string ClockStyle { get; set; } = "Digital";
    public int OriginalTaskbarState { get; set; } = -1;
    public List<PinConfig> Pins { get; set; } = new();

    static readonly JsonSerializerOptions opts = new() { WriteIndented = true };

    public static Config Load()
    {
        Directory.CreateDirectory(Dir);
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) ?? Default();
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
        var c = new Config();
        string pinned = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
        try
        {
            var files = Directory.Exists(pinned) ? Directory.GetFiles(pinned, "*.lnk") : Array.Empty<string>();
            var ordered = files.OrderBy(f => System.IO.Path.GetFileName(f).StartsWith("File Explorer", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                               .ThenBy(f => f, StringComparer.OrdinalIgnoreCase);
            foreach (var f in ordered)
                c.Pins.Add(new PinConfig { Path = f, Name = System.IO.Path.GetFileNameWithoutExtension(f) });
        }
        catch { }
        if (c.Pins.Count == 0)
            c.Pins.Add(new PinConfig { Path = @"C:\Windows\explorer.exe", Name = "File Explorer" });
        return c;
    }
}
