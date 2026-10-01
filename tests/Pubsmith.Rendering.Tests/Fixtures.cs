using Pubsmith.Core;
using Pubsmith.Rendering;
using SkiaSharp;

namespace Pubsmith.Rendering.Tests;

internal static class Fixtures
{
    public static string Dir { get; } = Path.Combine(AppContext.BaseDirectory, "fixtures");
    public static string Golden(string name) => Path.Combine(Dir, "golden", name + ".png");
    public static string Asset(string name) => Path.Combine(Dir, "assets", name);

    /// <summary>A context whose relative image paths resolve against the fixtures folder.</summary>
    public static RenderContext Context() => new(baseDirectory: Dir);

    public static SKColor Pixel(SKBitmap bmp, double xPt, double yPt, double dpi) =>
        bmp.GetPixel((int)(xPt / 72 * dpi), (int)(yPt / 72 * dpi));

    /// <summary>Writes a PNG split into four solid quadrants: TL red, TR green, BL blue, BR yellow.</summary>
    public static string QuadrantImage(int size = 200)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pubsmith-quad-{Guid.NewGuid():N}.png");
        using var bmp = new SKBitmap(size, size);
        using (var c = new SKCanvas(bmp))
        {
            var h = size / 2;
            using var p = new SKPaint();
            p.Color = SKColors.Red; c.DrawRect(0, 0, h, h, p);
            p.Color = SKColors.Lime; c.DrawRect(h, 0, h, h, p);
            p.Color = SKColors.Blue; c.DrawRect(0, h, h, h, p);
            p.Color = SKColors.Yellow; c.DrawRect(h, h, h, h, p);
        }
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    public static bool IsNear(SKColor actual, SKColor expected, int tolerance = 40) =>
        Math.Abs(actual.Red - expected.Red) <= tolerance &&
        Math.Abs(actual.Green - expected.Green) <= tolerance &&
        Math.Abs(actual.Blue - expected.Blue) <= tolerance;
}
