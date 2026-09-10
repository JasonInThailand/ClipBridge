using System.Windows.Interop;
using System.Windows.Threading;
using ClipBridge.Core;

namespace ClipBridge.Input;

/// <summary>Hidden message-only window: receives clipboard updates and global hotkeys.</summary>
public sealed class MessageWindow : IDisposable
{
    public const int HkPasteBase = 1;       // 1..9
    public const int HkPlainBase = 11;      // 11..19
    public const int HkList = 20, HkTypeMode = 21, HkQueue = 22, HkQueueNext = 23;

    private readonly HwndSource _src;
    private readonly DispatcherTimer _debounce;
    private uint _lastSeq;

    public IntPtr Handle => _src.Handle;
    public event Action? ClipboardChanged;
    public event Action<int>? Hotkey;
    public List<string> FailedHotkeys { get; } = new();

    public MessageWindow()
    {
        var p = new HwndSourceParameters("ClipBridgeMessageWindow")
        {
            WindowStyle = 0, ExtendedWindowStyle = 0, Width = 0, Height = 0, ParentWindow = Native.HWND_MESSAGE,
        };
        _src = new HwndSource(p);
        _src.AddHook(WndProc);
        _debounce = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(120) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            var seq = Native.GetClipboardSequenceNumber();
            if (seq == _lastSeq) return;
            _lastSeq = seq;
            ClipboardChanged?.Invoke();
        };
        if (!Native.AddClipboardFormatListener(Handle)) Log.Write("AddClipboardFormatListener failed");
        _lastSeq = Native.GetClipboardSequenceNumber();
    }

    public void RegisterHotkeys()
    {
        uint ca = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT;
        for (int n = 1; n <= 9; n++)
        {
            Reg(HkPasteBase + n - 1, ca, (uint)('0' + n), $"Ctrl+Alt+{n}");
            Reg(HkPlainBase + n - 1, ca | Native.MOD_SHIFT, (uint)('0' + n), $"Ctrl+Alt+Shift+{n}");
        }
        Reg(HkList, ca, 'V', "Ctrl+Alt+V");
        Reg(HkTypeMode, ca, 'T', "Ctrl+Alt+T");
        Reg(HkQueue, ca, 'Q', "Ctrl+Alt+Q");
        Reg(HkQueueNext, ca, 'P', "Ctrl+Alt+P");
    }

    private void Reg(int id, uint mods, uint vk, string name)
    {
        if (!Native.RegisterHotKey(Handle, id, mods, vk))
        {
            FailedHotkeys.Add(name);
            Log.Write($"hotkey {name} could not be registered (in use by another app?)");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_CLIPBOARDUPDATE:
                _debounce.Stop(); _debounce.Start();
                handled = true;
                break;
            case Native.WM_HOTKEY:
                Hotkey?.Invoke(wParam.ToInt32());
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        for (int id = 1; id <= 23; id++) Native.UnregisterHotKey(Handle, id);
        Native.RemoveClipboardFormatListener(Handle);
        _src.Dispose();
    }
}
