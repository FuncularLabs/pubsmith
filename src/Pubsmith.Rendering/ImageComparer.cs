using SkiaSharp;

namespace Pubsmith.Rendering;

/// <summary>
/// Visual difference score, 0 (identical) to 100 (black vs white). Mirrors the ImageMagick pipeline used for the
/// LibreOffice baseline (and by tools/oracle/Measure-PubLayoutFidelity.ps1), so scores are comparable: flatten onto white, resize both to 800x800 ignoring aspect, grayscale (Rec.709), gaussian blur sigma 1.5,
/// then root-mean-square error.
/// </summary>
public static class ImageComparer
{
    private const int Size = 800;
    private const double Sigma = 1.5;

    public static double Score(string pathA, string pathB)
    {
        using var a = Decode(pathA);
        using var b = Decode(pathB);
        return Score(a, b);
    }

    public static double Score(SKBitmap a, SKBitmap b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var ga = Prepare(a);
        var gb = Prepare(b);
        double sum = 0;
        for (var i = 0; i < ga.Length; i++)
        {
            var d = ga[i] - gb[i];
            sum += d * d;
        }
        return Math.Sqrt(sum / ga.Length) * 100;
    }

    private static SKBitmap Decode(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Image not found.", path);
        return SKBitmap.Decode(path) ?? throw new InvalidDataException($"'{path}' is not a readable image.");
    }

    private static double[] Prepare(SKBitmap src)
    {
        var info = new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        using (var image = SKImage.FromBitmap(src))
        {
            canvas.DrawImage(image, SKRect.Create(Size, Size), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }
        using var pixmap = surface.PeekPixels();
        var bytes = pixmap.GetPixelSpan();

        var gray = new double[Size * Size];
        for (var i = 0; i < gray.Length; i++)
        {
            var o = i * 4;   // opaque after the white clear, so premultiplied == straight
            gray[i] = (0.2126 * bytes[o] + 0.7152 * bytes[o + 1] + 0.0722 * bytes[o + 2]) / 255.0;
        }
        return Blur(gray);
    }

    private static double[] Blur(double[] img)
    {
        var radius = (int)Math.Ceiling(3 * Sigma);
        var kernel = new double[2 * radius + 1];
        double total = 0;
        for (var k = -radius; k <= radius; k++) total += kernel[k + radius] = Math.Exp(-(k * k) / (2 * Sigma * Sigma));
        for (var k = 0; k < kernel.Length; k++) kernel[k] /= total;

        var tmp = new double[img.Length];
        var outp = new double[img.Length];
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                double s = 0;
                for (var k = -radius; k <= radius; k++) s += kernel[k + radius] * img[y * Size + Math.Clamp(x + k, 0, Size - 1)];
                tmp[y * Size + x] = s;
            }
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                double s = 0;
                for (var k = -radius; k <= radius; k++) s += kernel[k + radius] * tmp[Math.Clamp(y + k, 0, Size - 1) * Size + x];
                outp[y * Size + x] = s;
            }
        return outp;
    }
}
