using Pubsmith.Core;
using SkiaSharp;

namespace Pubsmith.Rendering.Tests;

// Shadows (any element) and WordArt warps (text laid along a path or bent into an envelope).
public class WarpAndShadowTests
{
    private static SKBitmap Render(params Element[] elements) =>
        Exporter.RenderBitmap(new Page(300, 200) { Elements = elements }, 72, new RenderContext());

    [Fact]
    public void Shadow_DrawsAnOffsetSilhouette()
    {
        using var bmp = Render(new ShapeElement
        {
            Bounds = new Box(50, 50, 100, 50), Fill = new Rgba(0, 0, 255),
            Shadow = new Shadow(new Rgba(0, 0, 0), 6, 6),
        });
        Assert.Equal(SKColors.Blue, bmp.GetPixel(100, 75));     // the shape itself, on top
        Assert.Equal(SKColors.Black, bmp.GetPixel(153, 103));   // shadow beyond the bottom-right corner
        Assert.Equal(SKColors.White, bmp.GetPixel(47, 47));     // nothing up-left
    }

    [Fact]
    public void Shadow_FollowsTextGlyphs_NotTheFrame()
    {
        using var bmp = Render(new TextElement
        {
            Bounds = new Box(20, 20, 260, 160), Insets = new Insets(0, 0, 0, 0), Fit = TextFit.Stretch,
            Shadow = new Shadow(new Rgba(255, 0, 0), 4, 4),
            Paragraphs = [new Paragraph { Runs = [new TextRun("I I", new TextStyle { Family = "Arial", Color = new Rgba(0, 0, 0) })] }],
        });
        // The gap between the two "I"s stays white: the shadow is glyph-shaped.
        Assert.Equal(SKColors.White, bmp.GetPixel(150, 100));
        var red = 0;
        for (var y = 0; y < bmp.Height; y++) for (var x = 0; x < bmp.Width; x++) if (bmp.GetPixel(x, y) is { Red: > 200, Green: < 60 }) red++;
        Assert.True(red > 200, $"only {red} shadow pixels");
    }

    [Fact]
    public void ShadowOpacity_IsApplied()
    {
        using var bmp = Render(new ShapeElement
        {
            Bounds = new Box(50, 50, 100, 50), Fill = new Rgba(0, 0, 255),
            Shadow = new Shadow(new Rgba(0, 0, 0, 128), 10, 10),
        });
        var p = bmp.GetPixel(155, 105);
        Assert.InRange(p.Red, 115, 140);   // 50 % black over white
    }

    private static TextElement Warped(TextWarp warp, double w = 240, double h = 120) => new()
    {
        Bounds = new Box(30, 40, w, h), Insets = new Insets(0, 0, 0, 0), Fit = TextFit.Stretch, Warp = warp,
        Paragraphs = [new Paragraph { Runs = [new TextRun("SAMPLE TEXT", new TextStyle { Family = "Arial Black", Color = new Rgba(0, 0, 0) })] }],
    };

    private static (int Top, int Bottom) InkRows(SKBitmap bmp, int x0, int x1)
    {
        int top = int.MaxValue, bottom = -1;
        for (var y = 0; y < bmp.Height; y++) for (var x = x0; x < x1; x++)
            if (bmp.GetPixel(x, y).Red < 128) { top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
        return (top, bottom);
    }

    [Fact]
    public void ArchUp_MiddleIsHigherThanTheEnds()
    {
        using var bmp = Render(Warped(new TextWarp(WarpKind.ArchUp)));
        var left = InkRows(bmp, 30, 70); var mid = InkRows(bmp, 130, 170); var right = InkRows(bmp, 230, 270);
        Assert.True(mid.Top < left.Top - 10 && mid.Top < right.Top - 10, $"mid {mid.Top}, left {left.Top}, right {right.Top}");
        // As Publisher draws it (golden wordart__arch): the baseline runs on the frame's ellipse, glyphs point
        // outward, so at the top of the arch the glyphs rise above the frame (frame top = 40).
        Assert.True(mid.Top < 40, $"mid top {mid.Top}");
    }

    [Fact]
    public void ArchDown_MiddleIsLowerThanTheEnds()
    {
        using var bmp = Render(Warped(new TextWarp(WarpKind.ArchDown)));
        var left = InkRows(bmp, 30, 70); var mid = InkRows(bmp, 130, 170);
        Assert.True(mid.Bottom > left.Bottom + 10, $"mid {mid.Bottom}, left {left.Bottom}");   // straight text: equal
    }

    [Fact]
    public void Circle_WrapsAroundTheFrameEllipse()
    {
        // Frame 40..190 vertically. Baseline on the ellipse, glyphs outward: ink goes above the top and below the bottom.
        using var bmp = Render(Warped(new TextWarp(WarpKind.Circle), 150, 150));
        var mid = InkRows(bmp, 90, 120);
        Assert.True(mid.Top < 40 && mid.Bottom > 190, $"top {mid.Top}, bottom {mid.Bottom}");
        Assert.Equal(SKColors.White, bmp.GetPixel(105, 115));   // the centre stays empty
    }

    [Fact]
    public void CanDown_BendsTheBlockDownInTheMiddle()
    {
        using var bmp = Render(Warped(new TextWarp(WarpKind.CanDown)));
        var left = InkRows(bmp, 32, 60); var mid = InkRows(bmp, 135, 165);
        Assert.True(mid.Top > left.Top + 5, $"mid {mid.Top}, left {left.Top}");
    }

    [Fact]
    public void SlantUp_RightSideIsHigher()
    {
        using var bmp = Render(Warped(new TextWarp(WarpKind.SlantUp)));
        var left = InkRows(bmp, 32, 60); var right = InkRows(bmp, 240, 268);
        Assert.True(right.Top < left.Top - 15, $"right {right.Top}, left {left.Top}");
    }

    [Fact]
    public void PlainWarp_IsTheSameAsNoWarp()
    {
        using var a = Render(Warped(new TextWarp(WarpKind.None)));
        using var b = Render(Warped(new TextWarp(WarpKind.None)) with { Warp = null });
        Assert.Equal(0, ImageComparer.Score(a, b), 3);
    }
}
