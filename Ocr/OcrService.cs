using System.Runtime.InteropServices.WindowsRuntime;
using ClipBridge.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace ClipBridge.Ocr;

/// <summary>Text recognition with the OCR engine built into Windows 10/11.</summary>
public static class OcrService
{
    private static OcrEngine? _engine;
    private static bool _tried;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static bool Available { get { EnsureEngine(); return _engine != null; } }

    private static void EnsureEngine()
    {
        if (_tried) return;
        _tried = true;
        try
        {
            _engine = OcrEngine.TryCreateFromUserProfileLanguages()
                      ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
            Log.Write(_engine == null ? "OCR: no engine/language available" : $"OCR ready: {_engine.RecognizerLanguage.DisplayName}");
        }
        catch (Exception ex) { Log.Write("OCR init failed: " + ex.Message); }
    }

    public static async Task<string?> RecognizeAsync(byte[] png)
    {
        EnsureEngine();
        if (_engine == null) return null;
        await Gate.WaitAsync();
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(png.AsBuffer());
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            uint w = decoder.PixelWidth, h = decoder.PixelHeight, max = OcrEngine.MaxImageDimension;
            if (w < 20 || h < 20) return "";
            SoftwareBitmap bmp;
            if (w > max || h > max)
            {
                var s = Math.Min((double)max / w, (double)max / h);
                var tr = new BitmapTransform { ScaledWidth = (uint)(w * s), ScaledHeight = (uint)(h * s), InterpolationMode = BitmapInterpolationMode.Fant };
                bmp = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, tr,
                    ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            }
            else bmp = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            using (bmp)
            {
                var result = await _engine.RecognizeAsync(bmp);
                return string.Join("\n", result.Lines.Select(l => l.Text)).Trim();
            }
        }
        catch (Exception ex) { Log.Write("OCR failed: " + ex.Message); return null; }
        finally { Gate.Release(); }
    }
}
