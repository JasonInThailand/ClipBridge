using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media.Imaging;
using ClipBridge.Core;
using ClipBridge.Models;
using WpfClipboard = System.Windows.Clipboard;
using WinFormsClipboard = System.Windows.Forms.Clipboard;

namespace ClipBridge.Clip;

/// <summary>Reads the Windows clipboard into a ClipItem and writes ClipItems back. Must be called on an STA thread.</summary>
public static class ClipboardIO
{
    public const string MarkerFormat = "ClipBridge.Marker";

    public static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    public static string Sha(string s) => Sha(System.Text.Encoding.UTF8.GetBytes(s));

    private static System.Windows.IDataObject? GetDataObject()
    {
        for (int i = 0; i < 8; i++)
        {
            try { return WpfClipboard.GetDataObject(); }
            catch (ExternalException) { Thread.Sleep(50 + i * 30); }
            catch (Exception ex) { Log.Write("clipboard read: " + ex.Message); Thread.Sleep(50); }
        }
        return null;
    }

    public static ClipItem? Read(int maxImageBytes, string origin)
    {
        var data = GetDataObject();
        if (data == null) return null;
        if (data.GetDataPresent(MarkerFormat)) return null;

        var now = ClipItem.NowMs();

        // 1) Files copied in Explorer
        if (data.GetDataPresent(DataFormats.FileDrop))
        {
            var paths = SafeGet(data, DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length == 0) return null;
            var item = new ClipItem { Kind = ClipKind.Files, CreatedUtc = now, ModifiedUtc = now, Origin = origin };
            foreach (var p in paths)
            {
                long size = 0;
                try { if (File.Exists(p)) size = new FileInfo(p).Length; else if (Directory.Exists(p)) size = -1; } catch { }
                item.Files.Add(new ClipFile { Name = Path.GetFileName(p.TrimEnd('\\')), LocalPath = p, Size = size });
            }
            item.Text = string.Join("\n", paths);
            item.Hash = Sha("F:" + string.Join("|", paths));
            item.SizeBytes = item.Files.Sum(f => Math.Max(0, f.Size));
            return item;
        }

        // 2) Text (preferred over image when both exist, e.g. Excel cells)
        if (data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text))
        {
            var text = SafeGet(data, DataFormats.UnicodeText) as string ?? SafeGet(data, DataFormats.Text) as string ?? "";
            if (text.Trim().Length > 0)
            {
                string? html = null, rtf = null;
                if (data.GetDataPresent(DataFormats.Html)) html = SafeGet(data, DataFormats.Html) as string;
                if (data.GetDataPresent(DataFormats.Rtf)) rtf = SafeGet(data, DataFormats.Rtf) as string;
                if (html != null && html.Length > 2_000_000) html = null;
                if (rtf != null && rtf.Length > 2_000_000) rtf = null;
                return new ClipItem
                {
                    Kind = ClipKind.Text, Text = text, Html = html, Rtf = rtf, Hash = Sha("T:" + text),
                    CreatedUtc = now, ModifiedUtc = now, Origin = origin, SizeBytes = text.Length * 2L,
                };
            }
        }

        // 3) Image
        var png = TryGetPng(data) ?? TryGetBitmapAsPng(data);
        if (png != null)
        {
            if (png.Length > maxImageBytes) { Log.Write($"image skipped: {png.Length} bytes over cap"); return null; }
            var (w, h, thumb) = MakeThumb(png);
            return new ClipItem
            {
                Kind = ClipKind.Image, ImagePng = png, Thumb = thumb, ImageWidth = w, ImageHeight = h, Hash = Sha(png),
                CreatedUtc = now, ModifiedUtc = now, Origin = origin, SizeBytes = png.Length, Text = "",
            };
        }
        return null;
    }

    private static object? SafeGet(System.Windows.IDataObject d, string fmt)
    {
        try { return d.GetData(fmt); } catch (Exception ex) { Log.Write($"GetData({fmt}): {ex.Message}"); return null; }
    }

    private static byte[]? TryGetPng(System.Windows.IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent("PNG")) return null;
            if (data.GetData("PNG") is MemoryStream ms)
            {
                var bytes = ms.ToArray();
                if (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50) return bytes;
            }
        }
        catch { }
        return null;
    }

    private static byte[]? TryGetBitmapAsPng(System.Windows.IDataObject data)
    {
        try
        {
            if (!(data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent(DataFormats.Dib))) return null;
            using var img = WinFormsClipboard.GetImage();
            if (img != null)
            {
                using var ms = new MemoryStream();
                img.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }
        catch (Exception ex) { Log.Write("winforms image: " + ex.Message); }
        try
        {
            var src = WpfClipboard.GetImage();
            if (src == null) return null;
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch (Exception ex) { Log.Write("wpf image: " + ex.Message); return null; }
    }

    public static (int w, int h, byte[]? thumb) MakeThumb(byte[] png, int maxW = 260, int maxH = 150)
    {
        try
        {
            using var ms = new MemoryStream(png);
            using var img = Image.FromStream(ms);
            var scale = Math.Min(1.0, Math.Min((double)maxW / img.Width, (double)maxH / img.Height));
            var tw = Math.Max(1, (int)(img.Width * scale)); var th = Math.Max(1, (int)(img.Height * scale));
            using var bmp = new Bitmap(tw, th);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(img, 0, 0, tw, th);
            }
            using var outMs = new MemoryStream();
            bmp.Save(outMs, ImageFormat.Png);
            return (img.Width, img.Height, outMs.ToArray());
        }
        catch (Exception ex) { Log.Write("thumb: " + ex.Message); return (0, 0, null); }
    }

    /// <summary>Puts an item on the clipboard, tagged with our marker so the monitor ignores it.</summary>
    public static bool Write(ClipItem item, byte[]? imagePng, Transform t = Transform.None, bool plain = false)
    {
        var dobj = new System.Windows.DataObject();
        dobj.SetData(MarkerFormat, new MemoryStream(new byte[] { 1 }));
        switch (item.Kind)
        {
            case ClipKind.Text:
            {
                var text = Transforms.Apply(item.Text, t);
                dobj.SetData(DataFormats.UnicodeText, text);
                if (!plain && t == Transform.None)
                {
                    if (!string.IsNullOrEmpty(item.Html)) dobj.SetData(DataFormats.Html, item.Html);
                    if (!string.IsNullOrEmpty(item.Rtf)) dobj.SetData(DataFormats.Rtf, item.Rtf);
                }
                break;
            }
            case ClipKind.Image:
            {
                if (imagePng == null) { dobj.SetData(DataFormats.UnicodeText, item.Text); break; }
                if (plain && !string.IsNullOrEmpty(item.Text)) { dobj.SetData(DataFormats.UnicodeText, Transforms.Apply(item.Text, t)); break; }
                dobj.SetData("PNG", new MemoryStream(imagePng));
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = new MemoryStream(imagePng); bi.EndInit(); bi.Freeze();
                    dobj.SetImage(bi);
                }
                catch (Exception ex) { Log.Write("set bitmap: " + ex.Message); }
                break;
            }
            case ClipKind.Files:
            {
                var paths = item.Files.Select(f => f.LocalPath).Where(p => !string.IsNullOrEmpty(p) && (File.Exists(p) || Directory.Exists(p))).ToList();
                if (paths.Count == item.Files.Count && paths.Count > 0 && !plain)
                {
                    var sc = new StringCollection();
                    sc.AddRange(paths.ToArray());
                    dobj.SetFileDropList(sc);
                }
                else dobj.SetData(DataFormats.UnicodeText, string.Join("\n", item.Files.Select(f => f.Name)));
                break;
            }
        }
        for (int i = 0; i < 8; i++)
        {
            try { WpfClipboard.SetDataObject(dobj, true); return true; }
            catch (ExternalException) { Thread.Sleep(50 + i * 30); }
            catch (Exception ex) { Log.Write("clipboard write: " + ex.Message); Thread.Sleep(50); }
        }
        return false;
    }
}
