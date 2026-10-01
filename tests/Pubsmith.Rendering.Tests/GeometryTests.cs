using Pubsmith.Core;
using SkiaSharp;

namespace Pubsmith.Rendering.Tests;

// AC7 (crop) and AC8 (rotation), probed with pixels rather than fidelity scores.
public class GeometryTests
{
    [Fact]
    public void CropFractions_ShowOnlyCroppedRegion()
    {
        var quad = Fixtures.QuadrantImage();
        try
        {
            // Crop away the left half and the top half: only the bottom-right (yellow) quadrant remains,
            // and it must fill the whole 100x100 pt frame.
            var page = new Page(100, 100)
            {
                Elements = [new ImageElement { Bounds = new Box(0, 0, 100, 100), Source = quad, Crop = new Crop(0.5, 0.5, 0, 0) }],
            };
            using var bmp = Exporter.RenderBitmap(page, 72, new RenderContext());

            foreach (var (x, y) in new[] { (10, 10), (90, 10), (10, 90), (90, 90), (50, 50) })
                Assert.True(Fixtures.IsNear(bmp.GetPixel(x, y), SKColors.Yellow), $"pixel {x},{y} = {bmp.GetPixel(x, y)}");
        }
        finally { File.Delete(quad); }
    }

    [Fact]
    public void AsymmetricCrop_MapsFractionsNotPoints()
    {
        var quad = Fixtures.QuadrantImage();
        try
        {
            // Remove 25% from the left only: the left 1/3 of the frame shows red (the remaining 25% of the
            // red half out of 75% visible width), the rest shows green.
            var page = new Page(120, 60)
            {
                Elements = [new ImageElement { Bounds = new Box(0, 0, 120, 60), Source = quad, Crop = new Crop(0.25, 0, 0, 0.5) }],
            };
            using var bmp = Exporter.RenderBitmap(page, 72, new RenderContext());

            Assert.True(Fixtures.IsNear(bmp.GetPixel(20, 30), SKColors.Red));
            Assert.True(Fixtures.IsNear(bmp.GetPixel(60, 30), SKColors.Lime));
            Assert.True(Fixtures.IsNear(bmp.GetPixel(110, 30), SKColors.Lime));
        }
        finally { File.Delete(quad); }
    }

    [Fact]
    public void Rotate90_TallBarBecomesWide_AboutCentre()
    {
        // A 20x100 black bar centred at (100,100), rotated 90 degrees, covers x 50..150, y 90..110.
        using var bmp = Exporter.RenderBitmap(Bar(90), 72, new RenderContext());

        Assert.Equal(SKColors.Black, bmp.GetPixel(60, 100));
        Assert.Equal(SKColors.Black, bmp.GetPixel(140, 100));
        Assert.Equal(SKColors.White, bmp.GetPixel(100, 60));
        Assert.Equal(SKColors.White, bmp.GetPixel(100, 140));
    }

    [Fact]
    public void Rotate15_IsClockwise()
    {
        // Clockwise rotation (y down) moves the top end of a vertical bar to the right.
        using var bmp = Exporter.RenderBitmap(Bar(15), 72, new RenderContext());

        Assert.Equal(SKColors.Black, bmp.GetPixel(115, 58));
        Assert.Equal(SKColors.White, bmp.GetPixel(85, 58));
    }

    [Fact]
    public void Ellipse_FillsCentreNotCorners()
    {
        var page = new Page(100, 100)
        {
            Elements = [new ShapeElement { Bounds = new Box(0, 0, 100, 100), Kind = ShapeKind.Ellipse, Fill = new Rgba(0, 0, 255) }],
        };
        using var bmp = Exporter.RenderBitmap(page, 72, new RenderContext());
        Assert.Equal(SKColors.Blue, bmp.GetPixel(50, 50));
        Assert.Equal(SKColors.White, bmp.GetPixel(3, 3));
    }

    [Fact]
    public void Stroke_IsDrawnCentredOnTheFrameEdge()
    {
        var page = new Page(100, 100)
        {
            Elements = [new ShapeElement { Bounds = new Box(20, 20, 60, 60), Stroke = new Stroke(new Rgba(255, 0, 0), 10) }],
        };
        using var bmp = Exporter.RenderBitmap(page, 72, new RenderContext());
        Assert.Equal(SKColors.Red, bmp.GetPixel(17, 50));    // outside half of the stroke
        Assert.Equal(SKColors.Red, bmp.GetPixel(23, 50));    // inside half
        Assert.Equal(SKColors.White, bmp.GetPixel(50, 50));  // no fill
    }

    private static Page Bar(double rotation) => new(200, 200)
    {
        Elements = [new ShapeElement { Bounds = new Box(90, 50, 20, 100), Fill = new Rgba(0, 0, 0), Rotation = rotation }],
    };
}
