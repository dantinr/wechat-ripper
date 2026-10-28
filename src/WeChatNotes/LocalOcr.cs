using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace WeChatNotes;

public sealed class LocalOcr
{
    private readonly OcrEngine engine;
    public string Language => engine.RecognizerLanguage.DisplayName;
    public LocalOcr()
    {
        // Do not silently select English-only OCR for Chinese conversations.
        engine = OcrEngine.TryCreateFromLanguage(new Language("zh-Hans"))
            ?? throw new NotSupportedException("未找到 Windows 简体中文文字识别组件。请在 Windows 语言设置中安装简体中文的文字识别组件后重启程序。");
    }

    public async Task<string> ReadAsync(Bitmap source)
        => string.Join('\n', (await ReadLinesAsync(source)).Select(line => line.Text));

    internal sealed record Line(string Text, Rectangle Bounds);
    internal async Task<Line[]> ReadLinesAsync(Bitmap source)
    {
        double scale = Math.Min(source.Width < 1500 ? 2.0 : 1.0, (double)OcrEngine.MaxImageDimension / Math.Max(source.Width, source.Height));
        using var resized = new Bitmap(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(resized))
        {
            graphics.Clear(Color.White);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(source, new Rectangle(0, 0, resized.Width, resized.Height));
        }
        var data = resized.LockBits(new Rectangle(0, 0, resized.Width, resized.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, resized.PixelFormat);
        var bytes = new byte[resized.Width * resized.Height * 4];
        try { Marshal.Copy(data.Scan0, bytes, 0, bytes.Length); }
        finally { resized.UnlockBits(data); }
        using var software = SoftwareBitmap.CreateCopyFromBuffer(bytes.AsBuffer(), BitmapPixelFormat.Bgra8, resized.Width, resized.Height, BitmapAlphaMode.Ignore);
        var result = await engine.RecognizeAsync(software);
        return result.Lines.Select(line =>
        {
            double left = line.Words.Min(w => w.BoundingRect.Left), top = line.Words.Min(w => w.BoundingRect.Top);
            double right = line.Words.Max(w => w.BoundingRect.Right), bottom = line.Words.Max(w => w.BoundingRect.Bottom);
            return new Line(JoinWords(line.Words.Select(w => w.Text)), Rectangle.FromLTRB((int)(left / scale), (int)(top / scale), (int)Math.Ceiling(right / scale), (int)Math.Ceiling(bottom / scale)));
        }).ToArray();
    }

    private static string JoinWords(IEnumerable<string> words)
    {
        var result = new System.Text.StringBuilder();
        string previous = "";
        foreach (string word in words)
        {
            if (previous.Length > 0 && word.Length > 0 && char.IsAsciiLetterOrDigit(previous[^1]) && char.IsAsciiLetterOrDigit(word[0])) result.Append(' ');
            result.Append(word);
            previous = word;
        }
        return result.ToString();
    }
}
