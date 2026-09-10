using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Threading;
using ClipBridge.Core;
using ClipBridge.Input;
using ClipBridge.Models;
using ClipBridge.Ocr;
using ClipBridge.Sync;
using ClipBridge.UI;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace ClipBridge;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private Settings _settings = null!;
    private Store _store = null!;
    private ClipService _svc = null!;
    private MessageWindow _msg = null!;
    private SyncService _sync = null!;
    private WinForms.NotifyIcon _tray = null!;
    private ListWindow _list = null!;
    private HudWindow _hud = null!;
    private DispatcherTimer _hudTimer = null!;
    private DateTime? _holdStart;
    private WinForms.ToolStripMenuItem _pauseItem = null!, _typeItem = null!, _queueItem = null!, _syncItem = null!;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        var mutexName = "ClipBridge.SingleInstance." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Settings.Dir.ToLowerInvariant())), 0, 8);
        _mutex = new Mutex(true, mutexName, out var created);
        var utilityRun = Environment.GetCommandLineArgs().Any(a => a is "--dump" or "--shots");
        if (!created && !utilityRun)
        {
            WinForms.MessageBox.Show("ClipBridge is already running. Look for its icon in the system tray.", "ClipBridge");
            Shutdown();
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Write("UNHANDLED: " + ex.ExceptionObject);
        DispatcherUnhandledException += (_, ex) => { Log.Write("UI EXCEPTION: " + ex.Exception); ex.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Write("TASK EXCEPTION: " + ex.Exception); ex.SetObserved(); };

        _settings = Settings.Load();
        if (Environment.GetCommandLineArgs().Contains("--dump"))
        {
            // Diagnostic: write the slot list to dump.txt in the data folder and exit.
            using var s = new Store(Path.Combine(Settings.Dir, "clipbridge.db"));
            var svc = new ClipService(_settings, s);
            var lines = svc.Slots().Select((it, i) =>
                $"{i + 1,3}. [{it.Kind}]{(it.Pinned ? $" PIN{it.PinOrder}" : "")} v{it.Version} {it.Origin} {DateTimeOffset.FromUnixTimeMilliseconds(it.CreatedUtc).ToLocalTime():HH:mm:ss} " +
                $"{(it.Kind == ClipKind.Files ? string.Join(",", it.Files.Select(f => f.Name + (File.Exists(f.LocalPath) ? "(ok)" : "(no file)"))) : it.Text.Replace("\r", "").Replace("\n", "\\n"))}");
            File.WriteAllLines(Path.Combine(Settings.Dir, "dump.txt"), lines);
            Shutdown();
            return;
        }
        var argv = Environment.GetCommandLineArgs();
        var shotsIdx = Array.IndexOf(argv, "--shots");
        if (shotsIdx >= 0 && shotsIdx + 1 < argv.Length)
        {
            // Documentation helper: render the list window and HUD with demo data to PNG files, no screen capture needed.
            RenderDemoShots(argv[shotsIdx + 1]);
            Shutdown();
            return;
        }
        Log.Write($"ClipBridge starting on {_settings.MachineName}");
        _store = new Store(Path.Combine(Settings.Dir, "clipbridge.db"));
        _store.PurgeOldTombstones();
        _svc = new ClipService(_settings, _store);
        _svc.Notify += m => Dispatcher.BeginInvoke(() => Balloon(m));
        _svc.OcrWanted += it => _ = RunOcrAsync(it);
        _svc.Changed += () => Dispatcher.BeginInvoke(RefreshUi);

        _msg = new MessageWindow();
        _msg.ClipboardChanged += () =>
        {
            try { _svc.CaptureFromClipboard(); }
            catch (Exception ex) { Log.Write("capture: " + ex); }
        };
        _msg.Hotkey += OnHotkey;
        _msg.RegisterHotkeys();

        _sync = new SyncService(_svc);
        _sync.StatusChanged += () => Dispatcher.BeginInvoke(RefreshUi);
        _sync.Start();

        _list = new ListWindow(_svc, () => (_sync.Connected, _sync.Status));
        _hud = new HudWindow(_svc);

        BuildTray();
        if (!Settings.IsCustomDir) ApplyStartWithWindows(_settings.StartWithWindows);

        _hudTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _hudTimer.Tick += HudTick;
        _hudTimer.Start();

        // OCR anything that was captured before OCR finished last time.
        foreach (var it in _store.Live().Where(i => i.Kind == ClipKind.Image && !i.OcrDone && i.Origin == _settings.MachineName))
            _ = RunOcrAsync(it);

        if (_msg.FailedHotkeys.Count > 0)
            Balloon("Some hotkeys are taken by another app: " + string.Join(", ", _msg.FailedHotkeys));
        else if (Settings.IsFirstRun)
            Balloon($"ClipBridge is running. Ctrl+Alt+V opens the list. Your sync key is {_settings.SharedKey}. Enter the same key on your other machine (Settings).");
        else
            Balloon("ClipBridge is running. Ctrl+Alt+V opens the list, Ctrl+Alt+1..9 pastes.");
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        if (_store == null) return; // --dump run or duplicate instance
        try { _hudTimer?.Stop(); _sync?.Dispose(); _msg?.Dispose(); _tray?.Dispose(); _store?.Dispose(); } catch { }
        Log.Write("ClipBridge exited");
    }

    // ---------------- hotkeys ----------------

    private void OnHotkey(int id)
    {
        try
        {
            Log.Write($"hotkey id {id}");
            if (id >= MessageWindow.HkPasteBase && id < MessageWindow.HkPasteBase + 9) { PasteSlot(id - MessageWindow.HkPasteBase + 1, plain: false); return; }
            if (id >= MessageWindow.HkPlainBase && id < MessageWindow.HkPlainBase + 9) { PasteSlot(id - MessageWindow.HkPlainBase + 1, plain: true); return; }
            switch (id)
            {
                case MessageWindow.HkList:
                    if (_list.IsVisible) _list.HideList(); else { _hud.HideHud(); _list.ShowNearCursor(); }
                    break;
                case MessageWindow.HkTypeMode: _svc.ToggleTypeMode(); break;
                case MessageWindow.HkQueue: _svc.ToggleQueue(); break;
                case MessageWindow.HkQueueNext:
                    var next = _svc.QueueNext();
                    if (next != null) _ = Paster.DeliverAsync(_svc, next, Transform.None, false, _svc.TypeMode, IntPtr.Zero);
                    break;
            }
        }
        catch (Exception ex) { Log.Write("hotkey: " + ex); }
    }

    private void PasteSlot(int n, bool plain)
    {
        var item = _svc.Slot(n);
        if (item == null) { Balloon($"Slot {n} is empty."); return; }
        _hud.HideHud();
        _ = Paster.DeliverAsync(_svc, item, Transform.None, plain, _svc.TypeMode, IntPtr.Zero);
    }

    private void HudTick(object? s, EventArgs e)
    {
        var both = Native.IsDown(Native.VK_CONTROL) && Native.IsDown(Native.VK_MENU);
        if (both && !_list.IsVisible)
        {
            _holdStart ??= DateTime.UtcNow;
            if (!_hud.IsVisible && (DateTime.UtcNow - _holdStart.Value).TotalMilliseconds >= _settings.HudHoldMs)
                _hud.ShowHud();
        }
        else
        {
            _holdStart = null;
            if (_hud.IsVisible) _hud.HideHud();
        }
    }

    private async Task RunOcrAsync(ClipItem it)
    {
        try
        {
            var png = it.ImagePng ?? _store.GetImage(it.Id);
            if (png == null) return;
            var text = await OcrService.RecognizeAsync(png);
            _svc.SetOcrText(it.Id, text ?? "");
        }
        catch (Exception ex) { Log.Write("ocr task: " + ex.Message); }
    }

    // ---------------- tray ----------------

    private void BuildTray()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open list  (Ctrl+Alt+V)", null, (_, _) => _list.ShowNearCursor());
        _typeItem = new WinForms.ToolStripMenuItem("Type-it-out mode  (Ctrl+Alt+T)", null, (_, _) => _svc.ToggleTypeMode());
        _queueItem = new WinForms.ToolStripMenuItem("Paste queue  (Ctrl+Alt+Q)", null, (_, _) => _svc.ToggleQueue());
        menu.Items.Add(_typeItem);
        menu.Items.Add(_queueItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Unpin all", null, (_, _) => _svc.UnpinAll());
        menu.Items.Add("Clear unpinned history", null, (_, _) =>
        {
            if (WinForms.MessageBox.Show("Delete every unpinned item on both machines?", "ClipBridge", WinForms.MessageBoxButtons.YesNo) == WinForms.DialogResult.Yes)
                _svc.ClearUnpinned();
        });
        _pauseItem = new WinForms.ToolStripMenuItem("Pause capturing", null, (_, _) =>
        {
            _settings.CaptureEnabled = !_settings.CaptureEnabled; _settings.Save(); RefreshUi();
        });
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        _syncItem = new WinForms.ToolStripMenuItem("Sync: ...") { Enabled = false };
        menu.Items.Add(_syncItem);
        menu.Items.Add("Reset list window position", null, (_, _) => { _list.ResetPosition(); Balloon("The list will open near the mouse again until you drag it."); });
        menu.Items.Add("Settings...", null, (_, _) => new SettingsWindow(_settings).ShowDialog());
        menu.Items.Add("Open data folder", null, (_, _) => Process.Start("explorer.exe", Settings.Dir));
        menu.Items.Add("Show log", null, (_, _) => Process.Start("notepad.exe", Settings.LogPath));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());

        _tray = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(), Text = "ClipBridge", Visible = true, ContextMenuStrip = menu,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) _list.ShowNearCursor(); };
        RefreshUi();
    }

    private void RefreshUi()
    {
        try
        {
            if (_list.IsVisible) _list.Refresh();
            if (_hud.IsVisible) _hud.Refresh();
            _typeItem.Checked = _svc.TypeMode;
            _queueItem.Checked = _svc.QueueActive;
            _queueItem.Text = _svc.QueueActive ? $"Paste queue ON, {_svc.QueueRemaining} left  (Ctrl+Alt+P pastes next)" : "Paste queue  (Ctrl+Alt+Q)";
            _pauseItem.Checked = !_settings.CaptureEnabled;
            _syncItem.Text = "Sync: " + _sync.Status;
            var tip = "ClipBridge · " + _sync.Status + (_svc.TypeMode ? " · TYPE mode" : "") + (_svc.QueueActive ? " · queue" : "") + (!_settings.CaptureEnabled ? " · PAUSED" : "");
            _tray.Text = tip.Length > 120 ? tip[..120] : tip;
        }
        catch (Exception ex) { Log.Write("refresh ui: " + ex.Message); }
    }

    private void Balloon(string text)
    {
        try { _tray?.ShowBalloonTip(2500, "ClipBridge", text, WinForms.ToolTipIcon.None); } catch { }
    }

    private static Icon LoadIcon()
    {
        try
        {
            var p = Environment.ProcessPath;
            if (p != null) { var ic = Icon.ExtractAssociatedIcon(p); if (ic != null) return ic; }
        }
        catch { }
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var b = new SolidBrush(Color.FromArgb(59, 130, 246));
            g.FillEllipse(b, 1, 1, 30, 30);
            using var f = new Font("Segoe UI", 16, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            g.DrawString("9", f, Brushes.White, new RectangleF(0, 0, 32, 32), new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    // ---------------- documentation screenshots ----------------

    private static void RenderDemoShots(string dir)
    {
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(Path.GetTempPath(), "clipbridge-shots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var settings = new Settings { MachineName = "MAIN-PC", EnableSync = false };
        using var store = new Store(Path.Combine(tmp, "demo.db"));
        var svc = new ClipService(settings, store);
        long now = ClipItem.NowMs();
        ClipItem T(string text, int minutesAgo, string origin = "MAIN-PC") => new()
        {
            Kind = ClipKind.Text, Text = text, Hash = Clip.ClipboardIO.Sha("T:" + text),
            CreatedUtc = now - minutesAgo * 60000L, ModifiedUtc = now, Origin = origin, SizeBytes = text.Length * 2L,
        };
        foreach (var it in new[]
        {
            T("Meeting notes: call the title company at 2pm about the Maple St closing. Bring the signed addendum.", 42),
            T("https://example.com/listing/4821?utm_source=newsletter&utm_campaign=sept", 31, "LAPTOP"),
            T("SELECT id, name FROM leads WHERE status = 'new' ORDER BY created DESC;", 18),
            T("Thanks for your time today. I'll send the paperwork over tonight and follow up Thursday.", 9, "LAPTOP"),
            T("555-0147", 4),
            T("sam@example.com", 1),
        }) store.Upsert(it);

        var png = DemoChartPng();
        var (w, h, thumb) = Clip.ClipboardIO.MakeThumb(png);
        store.Upsert(new ClipItem
        {
            Kind = ClipKind.Image, ImagePng = png, Thumb = thumb, ImageWidth = w, ImageHeight = h, Hash = Clip.ClipboardIO.Sha(png),
            CreatedUtc = now - 13 * 60000L, ModifiedUtc = now, Origin = "LAPTOP", Text = "Q3 pipeline: 38 deals", OcrDone = true, SizeBytes = png.Length,
        });
        var addr = T("Northwind Holdings LLC\n742 Evergreen Terrace, Suite 4\nSpringfield, OR 97477", 300); addr.Pinned = true; addr.PinOrder = 1; store.Upsert(addr);
        var sig = T("Best regards,\nSam Rivera · Northwind Holdings · 555-0100", 250); sig.Pinned = true; sig.PinOrder = 2; store.Upsert(sig);

        var list = new ListWindow(svc, () => (true, "synced with LAPTOP")) { Width = 470, Height = 640 };
        list.Refresh();
        RenderToPng(list, list.Width, list.Height, Path.Combine(dir, "list.png"));

        var hud = new HudWindow(svc);
        hud.Refresh();
        RenderToPng(hud, double.PositiveInfinity, double.PositiveInfinity, Path.Combine(dir, "hud.png"));
        try { Directory.Delete(tmp, true); } catch { }
    }

    private static void RenderToPng(Window win, double width, double height, string path)
    {
        var root = (FrameworkElement)win.Content;
        root.Measure(new System.Windows.Size(width, height));
        var size = double.IsInfinity(width) ? root.DesiredSize : new System.Windows.Size(width, height);
        root.Arrange(new Rect(0, 0, size.Width, size.Height));
        root.UpdateLayout();
        const double scale = 2; // crisp on high-DPI displays and GitHub
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(size.Width * scale), (int)(size.Height * scale), 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static byte[] DemoChartPng()
    {
        using var img = new Bitmap(640, 360);
        using (var g = Graphics.FromImage(img))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(245, 247, 250));
            using var pen = new Pen(Color.FromArgb(59, 130, 246), 6);
            var pts = Enumerable.Range(0, 8).Select(i => new PointF(40 + i * 80, 300 - i * i * 4 - i * 10)).ToArray();
            g.DrawLines(pen, pts);
            using var f = new Font("Segoe UI", 22, System.Drawing.FontStyle.Bold);
            g.DrawString("Q3 pipeline: 38 deals", f, Brushes.DimGray, 30, 20);
        }
        using var ms = new MemoryStream();
        img.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    // ---------------- helpers used by settings ----------------

    public static void ApplyStartWithWindows(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (enable && Environment.ProcessPath != null) key.SetValue("ClipBridge", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("ClipBridge", false);
        }
        catch (Exception ex) { Log.Write("startup registry: " + ex.Message); }
    }

    public static void RestartApp()
    {
        var exe = Environment.ProcessPath;
        if (exe != null)
        {
            var app = (App)Current;
            app.OnExit(app, null!);
            app._mutex?.ReleaseMutex();
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        Current.Shutdown();
    }
}
