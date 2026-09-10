using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipBridge.Core;
using ClipBridge.Input;
using ClipBridge.Models;
using Transform = ClipBridge.Core.Transform;

namespace ClipBridge.UI;

public sealed class ItemVM
{
    public ClipItem Item { get; init; } = null!;
    public string Id => Item.Id;
    public string Slot { get; init; } = "";
    public Brush SlotBrush { get; init; } = Brushes.Gray;
    public string PreviewText { get; init; } = "";
    public bool HasText { get; init; }
    public bool HasImage { get; init; }
    public BitmapImage? Thumb { get; init; }
    public string Meta { get; init; } = "";
    public string PinGlyph => Item.Pinned ? "★" : "☆";
    public Brush PinBrush => Item.Pinned ? new SolidColorBrush(Color.FromRgb(0xF5, 0xB8, 0x2E)) : Brushes.Gray;
    public Brush TextBrush => Item.Kind == ClipKind.Files ? new SolidColorBrush(Color.FromRgb(0x9C, 0xD3, 0xFF)) : Brushes.WhiteSmoke;
}

public partial class ListWindow : Window
{
    private readonly ClipService _svc;
    private readonly Func<(bool connected, string text)> _sync;
    private readonly Dictionary<string, BitmapImage> _thumbs = new();
    private IntPtr _target;
    private bool _holdOpen;

    private static readonly Brush PinnedBrush = new SolidColorBrush(Color.FromRgb(0xC7, 0x8A, 0x1A));
    private static readonly Brush SlotBrushBlue = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
    private static readonly Brush NoSlotBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3D, 0x44));

    public ListWindow(ClipService svc, Func<(bool, string)> sync)
    {
        _svc = svc; _sync = sync;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            HudWindow.MakeToolWindow(h);
        };
    }

    public void ShowNearCursor()
    {
        _target = Native.GetForegroundWindow();
        if (_target == new WindowInteropHelper(this).Handle) _target = IntPtr.Zero;

        Native.GetCursorPos(out var pt);
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(pt.X, pt.Y));
        var wa = screen.WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        var st = _svc.Settings;
        if (st.RememberListPosition && st.ListLeft.HasValue && st.ListTop.HasValue)
        {
            if (st.ListWidth is > 200) Width = st.ListWidth.Value;
            if (st.ListHeight is > 200) Height = st.ListHeight.Value;
            // Keep the remembered spot, but make sure it is still on a screen.
            var all = System.Windows.Forms.Screen.AllScreens.Select(sc => sc.WorkingArea).ToList();
            double lPx = st.ListLeft.Value * dpi.DpiScaleX, tPx = st.ListTop.Value * dpi.DpiScaleY;
            var onScreen = all.Any(r => lPx + 80 < r.Right && lPx + Width * dpi.DpiScaleX - 80 > r.Left && tPx + 40 < r.Bottom && tPx > r.Top - 10);
            if (onScreen) { Left = st.ListLeft.Value; Top = st.ListTop.Value; }
            else PlaceNearCursor(pt, wa, dpi);
        }
        else PlaceNearCursor(pt, wa, dpi);

        SearchBox.Text = "";
        Refresh();
        Show();
        Paster.Focus(new WindowInteropHelper(this).Handle);
        Activate();
        SearchBox.Focus();
        if (List.Items.Count > 0) { List.SelectedIndex = 0; List.ScrollIntoView(List.Items[0]); }
    }

    private void PlaceNearCursor(Native.POINT pt, System.Drawing.Rectangle wa, DpiScale dpi)
    {
        double wPx = Width * dpi.DpiScaleX, hPx = Height * dpi.DpiScaleY;
        double left = Math.Max(wa.Left, Math.Min(pt.X - 60, wa.Right - wPx));
        double top = Math.Max(wa.Top, Math.Min(pt.Y - 40, wa.Bottom - hPx));
        Left = left / dpi.DpiScaleX; Top = top / dpi.DpiScaleY;
    }

    public void HideList()
    {
        _holdOpen = false;
        if (_svc.Settings.ListLeft.HasValue) SavePosition(); // only once the user has dragged it somewhere
        Hide();
    }

    public void Refresh()
    {
        var selectedId = (List.SelectedItem as ItemVM)?.Id;
        var slots = _svc.Slots();
        var q = SearchBox.Text.Trim();
        var vms = new List<ItemVM>(slots.Count);
        for (int i = 0; i < slots.Count; i++)
        {
            var it = slots[i];
            if (q.Length > 0 && !Matches(it, q)) continue;
            vms.Add(ToVM(it, i + 1));
        }
        List.ItemsSource = vms;
        var sel = vms.FirstOrDefault(v => v.Id == selectedId) ?? vms.FirstOrDefault();
        List.SelectedItem = sel;

        TypeBtn.Content = _svc.TypeMode ? "Type: ON" : "Type: off";
        TypeBtn.Background = _svc.TypeMode ? PinnedBrush : new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x33));
        QueueBtn.Content = _svc.QueueActive ? $"Queue: {_svc.QueueRemaining} left" : "Queue: off";
        QueueBtn.Background = _svc.QueueActive ? PinnedBrush : new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x33));
        var (connected, text) = _sync();
        SyncDot.Fill = connected ? Brushes.LimeGreen : Brushes.Gray;
        SyncText.Text = text;
    }

    private static bool Matches(ClipItem it, string q)
    {
        if (it.Text.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        if (it.Kind == ClipKind.Files && it.Files.Any(f => f.Name.Contains(q, StringComparison.OrdinalIgnoreCase))) return true;
        if (it.Origin.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private ItemVM ToVM(ClipItem it, int slot)
    {
        BitmapImage? thumb = null;
        if (it.Kind == ClipKind.Image && it.Thumb != null)
        {
            if (!_thumbs.TryGetValue(it.Id, out thumb))
            {
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = new MemoryStream(it.Thumb); bi.EndInit(); bi.Freeze();
                    thumb = bi; _thumbs[it.Id] = bi;
                }
                catch { }
            }
        }
        string preview = it.Kind switch
        {
            ClipKind.Files => string.Join("\n", it.Files.Select(f => (f.Size < 0 ? "\U0001F4C1 " : "\U0001F4C4 ") + f.Name)),
            ClipKind.Image => it.OcrDone && it.Text.Length > 0 ? "✓ " + Collapse(it.Text, 160) : (it.OcrDone ? "" : "(reading text...)"),
            _ => Collapse(it.Text, 240),
        };
        var kind = it.Kind switch
        {
            ClipKind.Image => $"image {it.ImageWidth}×{it.ImageHeight}",
            ClipKind.Files => it.Files.Count == 1 ? "file" : $"{it.Files.Count} files",
            _ => $"{it.Text.Length:n0} chars",
        };
        if (it.Kind == ClipKind.Files && !it.FilesTransferred) kind += " (names only)";
        var meta = $"{it.Origin} · {Ago(it.CreatedUtc)} · {kind}" + (it.Pinned ? " · pinned" : "");
        return new ItemVM
        {
            Item = it,
            Slot = slot <= 9 ? slot.ToString() : "",
            SlotBrush = it.Pinned ? PinnedBrush : slot <= 9 ? SlotBrushBlue : NoSlotBrush,
            PreviewText = preview,
            HasText = preview.Length > 0,
            HasImage = thumb != null,
            Thumb = thumb,
            Meta = meta,
        };
    }

    private static string Collapse(string s, int max)
    {
        s = s.Replace("\r\n", "\n").Trim();
        if (s.Length > max) s = s[..max] + "…";
        return s;
    }

    private static string Ago(long unixMs)
    {
        var d = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        if (d.TotalSeconds < 45) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 2) return "yesterday";
        return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("MMM d");
    }

    // ---------- actions ----------

    private ItemVM? Selected => List.SelectedItem as ItemVM;

    private async void Deliver(ItemVM? vm, Transform t = Transform.None, bool plain = false, bool typeIt = false)
    {
        if (vm == null) return;
        var target = _target;
        HideList();
        await Paster.DeliverAsync(_svc, vm.Item, t, plain, typeIt || _svc.TypeMode, target);
    }

    private static ItemVM? Ctx(object sender) => (sender as FrameworkElement)?.DataContext as ItemVM;

    private void Ctx_Paste(object s, RoutedEventArgs e) => Deliver(Ctx(s));
    private void Ctx_Plain(object s, RoutedEventArgs e) => Deliver(Ctx(s), plain: true);
    private void Ctx_Type(object s, RoutedEventArgs e) => Deliver(Ctx(s), typeIt: true);
    private void Ctx_Upper(object s, RoutedEventArgs e) => Deliver(Ctx(s), Transform.Upper, true);
    private void Ctx_Lower(object s, RoutedEventArgs e) => Deliver(Ctx(s), Transform.Lower, true);
    private void Ctx_Trim(object s, RoutedEventArgs e) => Deliver(Ctx(s), Transform.Trim, true);
    private void Ctx_CleanUrl(object s, RoutedEventArgs e) => Deliver(Ctx(s), Transform.CleanUrl, true);
    private void Ctx_Copy(object s, RoutedEventArgs e) { var vm = Ctx(s); if (vm != null) { Paster.CopyOnly(_svc, vm.Item); HideList(); } }
    private void Ctx_Pin(object s, RoutedEventArgs e) { var vm = Ctx(s); if (vm != null) _svc.TogglePin(vm.Id); }
    private void Ctx_Delete(object s, RoutedEventArgs e) { var vm = Ctx(s); if (vm != null) _svc.Delete(vm.Id); }

    private void Ctx_SaveImage(object s, RoutedEventArgs e)
    {
        var vm = Ctx(s);
        if (vm == null || vm.Item.Kind != ClipKind.Image) return;
        var png = _svc.Store.GetImage(vm.Id);
        if (png == null) return;
        _holdOpen = true;
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PNG image|*.png", FileName = $"clip-{DateTimeOffset.FromUnixTimeMilliseconds(vm.Item.CreatedUtc).ToLocalTime():yyyyMMdd-HHmmss}.png",
            };
            if (dlg.ShowDialog(this) == true) File.WriteAllBytes(dlg.FileName, png);
        }
        catch (Exception ex) { Log.Write("save image: " + ex.Message); }
        finally { _holdOpen = false; Activate(); }
    }

    private void OnPinClick(object s, RoutedEventArgs e) { var vm = Ctx(s); if (vm != null) _svc.TogglePin(vm.Id); e.Handled = true; }
    private void OnDeleteClick(object s, RoutedEventArgs e) { var vm = Ctx(s); if (vm != null) _svc.Delete(vm.Id); e.Handled = true; }
    private void OnDoubleClick(object s, MouseButtonEventArgs e) => Deliver(Selected);
    private void OnTypeToggle(object s, RoutedEventArgs e) => _svc.ToggleTypeMode();
    private void OnQueueToggle(object s, RoutedEventArgs e) => _svc.ToggleQueue();
    private void OnUnpinAll(object s, RoutedEventArgs e) => _svc.UnpinAll();
    private void OnClose(object s, RoutedEventArgs e) => HideList();
    private void OnSearch(object s, TextChangedEventArgs e) => Refresh();
    /// <summary>Drag the window from any non-interactive surface (title bar, footer, margins).</summary>
    private void OnDragMove(object s, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        for (DependencyObject? d = e.OriginalSource as DependencyObject; d != null && d != this; d = VisualTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.Button || d is TextBox || d is ListBoxItem || d is ListBox) return;
        }
        try { DragMove(); } catch { }
        SavePosition();
    }

    private void SavePosition()
    {
        if (!_svc.Settings.RememberListPosition || !IsVisible) return;
        _svc.Settings.ListLeft = Left; _svc.Settings.ListTop = Top;
        _svc.Settings.ListWidth = Width; _svc.Settings.ListHeight = Height;
        _svc.Settings.Save();
    }

    public void ResetPosition()
    {
        _svc.Settings.ListLeft = _svc.Settings.ListTop = _svc.Settings.ListWidth = _svc.Settings.ListHeight = null;
        _svc.Settings.Save();
    }

    private void OnSettings(object s, RoutedEventArgs e)
    {
        _holdOpen = true;
        try { new SettingsWindow(_svc.Settings) { Owner = this }.ShowDialog(); }
        finally { _holdOpen = false; Activate(); }
    }

    private void OnDeactivated(object? s, EventArgs e)
    {
        if (!_holdOpen) HideList();
    }

    private void OnPreviewKeyDown(object s, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var inSearch = SearchBox.IsKeyboardFocusWithin;
        switch (e.Key)
        {
            case Key.Escape: HideList(); e.Handled = true; break;
            case Key.Enter: Deliver(Selected, plain: shift, typeIt: ctrl); e.Handled = true; break;
            case Key.Down: MoveSel(1); e.Handled = true; break;
            case Key.Up: MoveSel(-1); e.Handled = true; break;
            case Key.PageDown: MoveSel(8); e.Handled = true; break;
            case Key.PageUp: MoveSel(-8); e.Handled = true; break;
            case Key.Delete when !inSearch || SearchBox.Text.Length == 0:
                if (Selected != null) _svc.Delete(Selected.Id); e.Handled = true; break;
            case Key.P when ctrl:
                if (Selected != null) _svc.TogglePin(Selected.Id); e.Handled = true; break;
            case Key.U when ctrl && shift: _svc.UnpinAll(); e.Handled = true; break;
            default:
                if (!inSearch && !ctrl && e.Key >= Key.A && e.Key <= Key.Z) { SearchBox.Focus(); }
                break;
        }
    }

    private void MoveSel(int delta)
    {
        var n = List.Items.Count; if (n == 0) return;
        var i = Math.Clamp(List.SelectedIndex + delta, 0, n - 1);
        List.SelectedIndex = i;
        List.ScrollIntoView(List.SelectedItem);
    }
}
