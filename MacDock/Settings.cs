using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using IOPath = System.IO.Path;

namespace MacDock;

public class PinnedApp
{
    public string Name { get; set; }
    /// <summary>Path to a .lnk, .exe or any launchable file.</summary>
    public string Path { get; set; }
    public string Args { get; set; }
    /// <summary>AppUserModelID; used for Store apps and to match windows precisely.</summary>
    public string AppId { get; set; }
}

public class DockSettings
{
    public int IconSize { get; set; } = 43;
    public int MagnifiedSize { get; set; } = 62;
    public bool Magnification { get; set; } = true;
    /// <summary>How far magnification spreads, in icon widths either side of the cursor.</summary>
    public double MagnifyRange { get; set; } = 1.8;
    /// <summary>How much neighbours slide aside for a magnified icon: 1 = full macOS movement, 0 = icons grow in place.</summary>
    public double MagnifyPush { get; set; } = 0.5;
    public bool AutoHide { get; set; } = true;
    public int ShowDelayMs { get; set; } = 60;
    public int HideDelayMs { get; set; } = 150;
    public bool HideWindowsTaskbar { get; set; } = true;

    public bool ShowTrash { get; set; } = true;
    public bool ShowTray { get; set; } = true;
    /// <summary>Tray icons (exe name or title) forced into / out of the dock, overriding Windows' own choice.</summary>
    public List<string> TrayAlwaysShow { get; set; } = new();
    public List<string> TrayAlwaysHide { get; set; } = new();

    /// <summary>.NET format string; null uses the system short time format.</summary>
    public string ClockTimeFormat { get; set; }
    public string ClockDateFormat { get; set; } = "ddd d MMM";

    /// <summary>Dock background, #AARRGGBB.</summary>
    public string BackgroundColor { get; set; } = "#D6202020"; // Flow Launcher's DarkBG
    /// <summary>Blur what's behind the dock, like Flow Launcher's acrylic window.</summary>
    public bool Blur { get; set; } = true;

    /// <summary>Diagnostics: log animation frame timing to dock.log.</summary>
    public bool LogFrameStats { get; set; }

    public List<PinnedApp> Pinned { get; set; } = new();

    /// <summary>Apps never shown in the dock, by exe name (e.g. "Overwolf") or AppUserModelID.</summary>
    public List<string> HiddenApps { get; set; } = new();

    // ------------------------------------------------------------------------

    public static string Dir { get; } = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacDock");
    public static string FilePath => IOPath.Combine(Dir, "settings.json");
    public static string IconsDir => IOPath.Combine(Dir, "icons");

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static DockSettings Load()
    {
        Directory.CreateDirectory(Dir);
        Directory.CreateDirectory(IconsDir);
        if (File.Exists(FilePath))
        {
            try { return JsonSerializer.Deserialize<DockSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new DockSettings(); }
            catch (Exception e)
            {
                Log.Error("settings parse", e);
                File.Copy(FilePath, FilePath + ".broken", true);
            }
        }
        var s = new DockSettings { Pinned = ImportTaskbarPins() };
        s.Save();
        return s;
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions)); }
        catch (Exception e) { Log.Error("settings save", e); }
    }

    /// <summary>First run: start with whatever is pinned to the Windows taskbar, in the same order.</summary>
    static List<PinnedApp> ImportTaskbarPins()
    {
        var result = new List<PinnedApp>();
        try
        {
            var dir = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
            if (!Directory.Exists(dir)) return result;

            // The Taskband "Favorites" blob lists pins in order; the .lnk names appear in it as UTF-16 text.
            string blob = "";
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband"))
                if (key?.GetValue("Favorites") is byte[] bytes)
                    blob = Encoding.Unicode.GetString(bytes) + Encoding.Unicode.GetString(bytes, 1, bytes.Length - 1);

            result = Directory.GetFiles(dir, "*.lnk")
                .Select(f => (file: f, idx: blob.IndexOf(IOPath.GetFileName(f), StringComparison.OrdinalIgnoreCase)))
                // The folder can hold stale shortcuts for things no longer pinned; trust the Favorites list when we have it.
                .Where(t => blob.Length == 0 || t.idx >= 0)
                .OrderBy(t => t.idx < 0 ? int.MaxValue : t.idx)
                .Select(t => new PinnedApp { Name = IOPath.GetFileNameWithoutExtension(t.file), Path = t.file })
                .ToList();
        }
        catch (Exception e) { Log.Error("import pins", e); }
        return result;
    }

    // --- Start at login -------------------------------------------------------

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool StartAtLogin
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("MacDock") != null;
        }
        set
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (value) key.SetValue("MacDock", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("MacDock", false);
        }
    }
}

static class Log
{
    static readonly object Gate = new();
    static string LogPath => IOPath.Combine(DockSettings.Dir, "dock.log");

    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(DockSettings.Dir);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1_000_000) File.Delete(LogPath);
        }
        catch { }
    }

    public static void Write(string message)
    {
        try { lock (Gate) File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}"); }
        catch { }
    }

    public static void Error(string context, Exception e) => Write($"ERROR [{context}] {e}");
}
