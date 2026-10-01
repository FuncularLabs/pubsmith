using Pubsmith.Core;
using SkiaSharp;

namespace Pubsmith.Rendering.Tests;

// Oval picture masks and WordArt-style stretched text (common on labels).
public class MaskAndStretchTests
{
    [Fact]
    public void EllipseMask_HidesTheCorners()
    {
        var quad = Fixtures.QuadrantImage();
        try
        {
            var page = new Page(100, 100) { Elements = [new ImageElement { Bounds = new Box(0, 0, 100, 100), Source = quad, Mask = ShapeKind.Ellipse }] };
            using var bmp = Exporter.RenderBitmap(page, 72, new RenderContext());
            Assert.Equal(SKColors.White, bmp.GetPixel(3, 3));                        // corner outside the oval
            Assert.True(Fixtures.IsNear(bmp.GetPixel(30, 30), SKColors.Red));        // inside, top-left quadrant
            Assert.True(Fixtures.IsNear(bmp.GetPixel(70, 70), SKColors.Yellow));     // inside, bottom-right quadrant
        }
        finally { File.Delete(quad); }
    }

    [Fact]
    public void EllipseMask_BorderFollowsTheOval()
    {
        var quad = Fixtures.QuadrantImage();
        try
        {
            var page = new Page(100, 100)
            {
                Elements = [new ImageElement { Bounds = new Box(10, 10, 80, 80), Source = quad, Mask = ShapeKind.Ellipse, Stroke = new Stroke(new Rgba(0, 0, 255), 4) }],
            };
            using var bmp = Exporter.RenderBitmap(page, 72, new RenderContext());
            Assert.True(Fixtures.IsNear(bmp.GetPixel(50, 10), SKColors.Blue));      // top of the oval
            Assert.Equal(SKColors.White, bmp.GetPixel(11, 11));                       // frame corner: no square border
        }
        finally { File.Delete(quad); }
    }

    private static SKRectI InkBox(SKBitmap bmp)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).Red < 200 || bmp.GetPixel(x, y).Blue < 200)
                { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
        return new SKRectI(minX, minY, maxX, maxY);
    }

    private static TextElement Stretched(string text, Stroke? outline = null) => new()
    {
        Bounds = new Box(20, 30, 200, 60), Insets = new Insets(0, 0, 0, 0), Fit = TextFit.Stretch, Outline = outline,
        Paragraphs = [new Paragraph { Runs = [new TextRun(text, new TextStyle { Family = "Arial", Size = 12, Color = new Rgba(200, 0, 0) })] }],
    };

    [Fact]
    public void Stretch_FillsTheFrame()
    {
        using var bmp = Exporter.RenderBitmap(new Page(240, 120) { Elements = [Stretched("SAMPLE")] }, 72, new RenderContext());
        var ink = InkBox(bmp);
        Assert.InRange(ink.Left, 19, 22);
        Assert.InRange(ink.Right, 217, 220);
        Assert.InRange(ink.Top, 29, 32);
        Assert.InRange(ink.Bottom, 87, 90);
    }

    [Fact]
    public void Outline_IsDrawnAroundTheGlyphs()
    {
        using var bmp = Exporter.RenderBitmap(new Page(240, 120) { Elements = [Stretched("I", new Stroke(new Rgba(0, 0, 255), 3))] }, 72, new RenderContext());
        var blue = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y) is { Blue: > 200, Red: < 80, Green: < 80 }) blue++;
        Assert.True(blue > 100, $"only {blue} outline pixels");
    }
}
