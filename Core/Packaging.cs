namespace ClipBridge.Core;

/// <summary>
/// Knows whether ClipBridge is running from its Microsoft Store (MSIX) package or as a plain exe,
/// and gives the package-specific answers for data folder, auto-start, and relaunch.
/// </summary>
public static class Packaging
{
    public const string StartupTaskId = "ClipBridgeStartup";

    private static bool? _isPackaged;

    public static bool IsPackaged
    {
        get
        {
            if (_isPackaged is bool b) return b;
            try { _ = Windows.ApplicationModel.Package.Current.Id.FamilyName; _isPackaged = true; }
            catch { _isPackaged = false; }
            return _isPackaged.Value;
        }
    }

    /// <summary>
    /// Real (non-virtualized) data folder for the packaged app. Explorer, Notepad, and apps that receive
    /// pasted files can all open paths under it, unlike the redirected %LocalAppData% view.
    /// </summary>
    public static string PackagedDataDir =>
        Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "ClipBridge");

    /// <summary>shell:AppsFolder target that launches the packaged app.</summary>
    public static string AppsFolderTarget =>
        @"shell:AppsFolder\" + Windows.ApplicationModel.Package.Current.Id.FamilyName + "!App";

    /// <summary>
    /// First packaged launch on a machine that already ran the exe version: copy its settings and history
    /// so the sync key and pins carry over. The old folder is left untouched.
    /// </summary>
    public static void MigrateFromExeInstall(string newDir)
    {
        try
        {
            if (File.Exists(Path.Combine(newDir, "settings.json"))) return;
            var oldDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipBridge");
            if (!File.Exists(Path.Combine(oldDir, "settings.json"))) return;
            Directory.CreateDirectory(newDir);
            foreach (var src in Directory.EnumerateFiles(oldDir, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(src);
                if (name.Equals("log.txt", StringComparison.OrdinalIgnoreCase)) continue;
                var dest = Path.Combine(newDir, Path.GetRelativePath(oldDir, src));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(src, dest, overwrite: false);
            }
        }
        catch { /* best effort; a fresh install still works */ }
    }

    public static async Task ApplyStartupTaskAsync(bool enable)
    {
        var task = await Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
        if (enable)
        {
            if (task.State == Windows.ApplicationModel.StartupTaskState.Disabled)
                await task.RequestEnableAsync();
        }
        else if (task.State == Windows.ApplicationModel.StartupTaskState.Enabled)
        {
            task.Disable();
        }
    }
}
