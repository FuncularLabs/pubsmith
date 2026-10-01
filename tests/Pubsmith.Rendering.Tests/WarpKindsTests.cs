using Pubsmith.Core;
using SkiaSharp;

namespace Pubsmith.Rendering.Tests;

// Every warp the importer can produce draws reshaped text (not blank, not straight), and the curve flattening
// behind the envelope warps follows every kind of path segment a font or shape can contain.
public class WarpKindsTests
{
    public static TheoryData<WarpKind> Warps()
    {
        var data = new TheoryData<WarpKind>();
        foreach (var k in Enum.GetValues<WarpKind>()) if (k != WarpKind.None) data.Add(k);
        return data;
    }

    private static SKBitmap Render(TextWarp? warp)
    {
        var text = new TextElement
        {
            Bounds = new Box(10, 10, 280, 120), Insets = new Insets(0, 0, 0, 0), Fit = TextFit.Stretch, Warp = warp,
            Paragraphs = [new Paragraph { Runs = [new TextRun("SAMPLE", new TextStyle { Family = "Arial Black", Size = 36 })] }],
        };
        return Exporter.RenderBitmap(new Page(300, 140) { Elements = [text] }, 72, new RenderContext());
    }

    [Theory]
    [MemberData(nameof(Warps))]
    public void EveryWarp_DrawsTheTextReshaped(WarpKind kind)
    {
        using var straight = Render(null);
        using var warped = Render(new TextWarp(kind));
        int ink = 0, differ = 0;
        for (var y = 0; y < warped.Height; y++)
            for (var x = 0; x < warped.Width; x++)
            {
                var inked = warped.GetPixel(x, y).Red < 128;
                if (inked) ink++;
                if (inked != straight.GetPixel(x, y).Red < 128) differ++;
            }
        Assert.True(ink > 500, $"{kind}: only {ink} inked pixels");
        Assert.True(differ > 300, $"{kind}: only {differ} pixels differ from straight text");
    }

    [Fact]
    public void Bend_FlattensEverySegmentKindAlongTheMap()
    {
        using var builder = new SKPathBuilder();
        builder.MoveTo(0, 0);
        builder.LineTo(10, 0);
        builder.CubicTo(20, 0, 30, 10, 30, 20);
        builder.QuadTo(15, 30, 0, 20);
        builder.Close();
        builder.AddOval(SKRect.Create(40, 0, 20, 20), SKPathDirection.Clockwise);   // conic segments
        using var path = builder.Detach();

        using var bent = WordArt.Bend(path, p => new SKPoint(p.X + 100, p.Y * 2));

        var (s, b) = (path.TightBounds, bent.Bounds);   // tight: the curves themselves, not their control points
        Assert.Equal(s.Left + 100, b.Left, 0.5);
        Assert.Equal(s.Right + 100, b.Right, 0.5);
        Assert.Equal(s.Top * 2, b.Top, 0.5);
        Assert.Equal(s.Bottom * 2, b.Bottom, 0.5);
        Assert.True(bent.PointCount > path.PointCount, "curves should be flattened into many points");
    }
}
