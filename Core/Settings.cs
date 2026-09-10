using System.Text.Json;

namespace ClipBridge.Core;

public sealed class Settings
{
    public string SharedKey { get; set; } = "ClipBridge";
    public int SyncPort { get; set; } = 47821;
    public int DiscoveryPort { get; set; } = 47820;
    public List<string> Peers { get; set; } = new();
    public int MaxItems { get; set; } = 100;
    public int MaxImageMB { get; set; } = 10;
    public int MaxFileTransferMB { get; set; } = 50;
    public int MaxPins { get; set; } = 5;
    public bool StartWithWindows { get; set; } = true;
    public bool EnableOcr { get; set; } = true;
    public bool EnableSync { get; set; } = true;
    public bool CaptureEnabled { get; set; } = true;
    public int HudHoldMs { get; set; } = 500;
    /// <summary>When true the list window reopens where you last dragged it; otherwise it opens near the mouse.</summary>
    public bool RememberListPosition { get; set; } = true;
    public double? ListLeft { get; set; }
    public double? ListTop { get; set; }
    public double? ListWidth { get; set; }
    public double? ListHeight { get; set; }
    public string MachineName { get; set; } = Environment.MachineName;
    public string HotkeyNote { get; set; } =
        "Ctrl+Alt+1..9 paste slot | Ctrl+Alt+Shift+1..9 paste as plain text | Ctrl+Alt+V open list | " +
        "Ctrl+Alt+T toggle type-it-out mode | Ctrl+Alt+Q toggle paste queue | Ctrl+Alt+P paste next queued | hold Ctrl+Alt for HUD";

    private static string? _dir;
    /// <summary>Data folder. Override with "--data &lt;folder&gt;" or CLIPBRIDGE_DATA for side-by-side test instances.</summary>
    public static string Dir => _dir ??= ResolveDir();
    public static bool IsCustomDir { get; private set; }

    private static string ResolveDir()
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals("--data", StringComparison.OrdinalIgnoreCase)) { IsCustomDir = true; return Path.GetFullPath(args[i + 1]); }
        var env = Environment.GetEnvironmentVariable("CLIPBRIDGE_DATA");
        if (!string.IsNullOrWhiteSpace(env)) { IsCustomDir = true; return Path.GetFullPath(env); }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipBridge");
    }
    public static string FilePath => Path.Combine(Dir, "settings.json");
    public static string ReceivedDir => Path.Combine(Dir, "Received");
    public static string LogPath => Path.Combine(Dir, "log.txt");

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static Settings Load()
    {
        Directory.CreateDirectory(Dir);
        Directory.CreateDirectory(ReceivedDir);
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Opts);
                if (s != null) { s.Save(); return s; }
            }
        }
        catch (Exception ex) { Log.Write("settings load failed: " + ex.Message); }
        var fresh = new Settings { SharedKey = NewRandomKey() };
        fresh.Save();
        IsFirstRun = true;
        return fresh;
    }

    /// <summary>True when this launch created settings.json (fresh install).</summary>
    public static bool IsFirstRun { get; private set; }

    /// <summary>Easy-to-type random key, e.g. "k7mp-4xqz-9hdt". Must be copied to the other machine.</summary>
    public static string NewRandomKey()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        var chars = bytes.Select(b => alphabet[b % alphabet.Length]).ToArray();
        return $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}-{new string(chars, 8, 4)}";
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts)); }
        catch (Exception ex) { Log.Write("settings save failed: " + ex.Message); }
    }
}

public static class Log
{
    private static readonly object Gate = new();
    public static void Write(string msg)
    {
        try
        {
            lock (Gate)
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {msg}{Environment.NewLine}";
                File.AppendAllText(Settings.LogPath, line);
                if (new FileInfo(Settings.LogPath).Length > 2_000_000)
                    File.WriteAllText(Settings.LogPath, line);
            }
        }
        catch { }
    }
}
