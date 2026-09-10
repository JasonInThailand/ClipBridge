using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipBridge.Core;
using ClipBridge.Input;
using ClipBridge.Models;

namespace ClipBridge.UI;

public sealed class HudCell
{
    public string Slot { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Text { get; init; } = "";
    public BitmapImage? Thumb { get; init; }
    public Brush SlotBrush { get; init; } = Brushes.Gray;
    public Visibility ThumbVis => Thumb != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TextVis => Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
}

public partial class HudWindow : Window
{
    private readonly ClipService _svc;
    private readonly Dictionary<string, BitmapImage> _thumbs = new();

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public static void MakeToolWindow(IntPtr hwnd, bool noActivate = false)
    {
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW;
        if (noActivate) ex |= WS_EX_NOACTIVATE;
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
    }

    public HudWindow(ClipService svc)
    {
        _svc = svc;
        InitializeComponent();
        SourceInitialized += (_, _) => MakeToolWindow(new WindowInteropHelper(this).Handle, noActivate: true);
    }

    public void ShowHud()
    {
        Refresh();
        Native.GetCursorPos(out var pt);
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(pt.X, pt.Y));
        var wa = screen.WorkingArea;
        Show();
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var wPx = ActualWidth * dpi.DpiScaleX;
        Left = (wa.Left + (wa.Width - wPx) / 2) / dpi.DpiScaleX;
        Top = (wa.Top + 14) / dpi.DpiScaleY;
    }

    public void HideHud() => Hide();

    public void Refresh()
    {
        var slots = _svc.Slots();
        var cells = new List<HudCell>(9);
        for (int i = 1; i <= 9; i++)
        {
            var it = slots.ElementAtOrDefault(i - 1);
            if (it == null)
            {
                cells.Add(new HudCell { Slot = i.ToString(), Kind = "empty", SlotBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3D, 0x44)) });
                continue;
            }
            BitmapImage? thumb = null;
            if (it.Kind == ClipKind.Image && it.Thumb != null && !_thumbs.TryGetValue(it.Id, out thumb))
            {
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = new MemoryStream(it.Thumb); bi.EndInit(); bi.Freeze();
                    thumb = bi; _thumbs[it.Id] = bi;
                }
                catch { }
            }
            var text = it.Kind switch
            {
                ClipKind.Files => string.Join(", ", it.Files.Select(f => f.Name)),
                ClipKind.Image => "",
                _ => it.Text.Replace("\r\n", " ").Replace('\n', ' ').Trim(),
            };
            if (text.Length > 90) text = text[..90] + "…";
            var kind = it.Kind switch { ClipKind.Image => "image", ClipKind.Files => "files", _ => "text" };
            if (it.Pinned) kind += " · pinned";
            cells.Add(new HudCell
            {
                Slot = i.ToString(), Kind = kind, Text = text, Thumb = thumb,
                SlotBrush = it.Pinned ? new SolidColorBrush(Color.FromRgb(0xC7, 0x8A, 0x1A)) : new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
            });
        }
        Cells.ItemsSource = cells;
        Foot.Text = _svc.TypeMode ? "TYPE MODE: Ctrl+Alt+number types the text as keystrokes" : "Ctrl+Alt+number pastes  ·  +Shift pastes plain text  ·  Ctrl+Alt+V opens the list";
    }
}
