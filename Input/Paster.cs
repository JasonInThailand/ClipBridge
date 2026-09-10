using ClipBridge.Clip;
using ClipBridge.Core;
using ClipBridge.Models;

namespace ClipBridge.Input;

/// <summary>Delivers an item into the active application, either by clipboard + Ctrl+V or by typing keystrokes.</summary>
public static class Paster
{
    private static int _busy;

    /// <param name="target">Window to focus before pasting, or IntPtr.Zero to use whatever is in front.</param>
    public static async Task DeliverAsync(ClipService svc, ClipItem item, Transform t, bool plain, bool typeIt, IntPtr target)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var hasText = !string.IsNullOrEmpty(item.Text);
            if (typeIt && hasText)
            {
                var text = Transforms.Apply(item.Text, t);
                await WaitModifiersUpAsync();
                Focus(target);
                await Task.Delay(60);
                await TypeTextAsync(text);
                return;
            }

            byte[]? png = item.Kind == ClipKind.Image ? svc.Store.GetImage(item.Id) : null;
            if (!ClipboardIO.Write(item, png, t, plain)) { Log.Write("paste: clipboard write failed"); return; }
            await WaitModifiersUpAsync();
            Focus(target);
            await Task.Delay(60);
            Native.Send(Native.Key(Native.VK_CONTROL, false), Native.Key(Native.VK_V, false), Native.Key(Native.VK_V, true), Native.Key(Native.VK_CONTROL, true));
            Log.Write($"paste: sent Ctrl+V for {item.Id[..8]} to foreground 0x{Native.GetForegroundWindow():X}");
        }
        catch (Exception ex) { Log.Write("paste failed: " + ex); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>Only puts the item on the clipboard, no paste keystroke.</summary>
    public static void CopyOnly(ClipService svc, ClipItem item, Transform t = Transform.None, bool plain = false)
    {
        byte[]? png = item.Kind == ClipKind.Image ? svc.Store.GetImage(item.Id) : null;
        ClipboardIO.Write(item, png, t, plain);
    }

    private static async Task WaitModifiersUpAsync()
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(1500);
        while (DateTime.UtcNow < deadline)
        {
            if (!Native.IsDown(Native.VK_MENU) && !Native.IsDown(Native.VK_SHIFT) && !Native.IsDown(Native.VK_CONTROL)
                && !Native.IsDown(Native.VK_LWIN) && !Native.IsDown(Native.VK_RWIN)) return;
            await Task.Delay(15);
        }
        // User is still holding keys; release them virtually so the paste chord is clean.
        Native.Send(Native.Key(Native.VK_MENU, true), Native.Key(Native.VK_SHIFT, true), Native.Key(Native.VK_CONTROL, true),
            Native.Key(Native.VK_LWIN, true), Native.Key(Native.VK_RWIN, true));
        await Task.Delay(30);
    }

    public static void Focus(IntPtr target)
    {
        if (target == IntPtr.Zero || !Native.IsWindow(target)) return;
        if (Native.GetForegroundWindow() == target) return;
        Native.SetForegroundWindow(target);
        if (Native.GetForegroundWindow() == target) return;
        // Focus-stealing fallback: temporarily attach to the foreground thread.
        var fg = Native.GetForegroundWindow();
        var fgThread = Native.GetWindowThreadProcessId(fg, out _);
        var me = Native.GetCurrentThreadId();
        if (fgThread != me) Native.AttachThreadInput(me, fgThread, true);
        try { Native.BringWindowToTop(target); Native.SetForegroundWindow(target); }
        finally { if (fgThread != me) Native.AttachThreadInput(me, fgThread, false); }
    }

    public static async Task TypeTextAsync(string text)
    {
        var batch = new List<Native.INPUT>(64);
        text = text.Replace("\r\n", "\n");
        foreach (var c in text)
        {
            switch (c)
            {
                case '\n': batch.Add(Native.Key(Native.VK_RETURN, false)); batch.Add(Native.Key(Native.VK_RETURN, true)); break;
                case '\t': batch.Add(Native.Key(Native.VK_TAB, false)); batch.Add(Native.Key(Native.VK_TAB, true)); break;
                case '\r': break;
                default: batch.Add(Native.Unicode(c, false)); batch.Add(Native.Unicode(c, true)); break;
            }
            if (batch.Count >= 40)
            {
                Native.Send(batch.ToArray()); batch.Clear();
                await Task.Delay(6);
            }
        }
        if (batch.Count > 0) Native.Send(batch.ToArray());
    }
}
