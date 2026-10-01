using System.Diagnostics;
using Pubsmith.Core;
using SkiaSharp;

namespace Pubsmith.Rendering.Tests;

// WI-005 AC1: a shadow no longer costs a page-sized layer. Time is compared within one run (shadowed against
// unshadowed, each the fastest of five), so a slow or busy machine slows both; PDF output is checked exactly.
public sealed class ShadowCostTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pubsmith-shadowcost-").FullName;

    public ShadowCostTests()
    {
        using var bmp = new SKBitmap(60, 40);
        for (var y = 0; y < 40; y++) for (var x = 0; x < 60; x++) bmp.SetPixel(x, y, new SKColor((byte)(x * 4), (byte)(y * 6), 160));
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(_dir, "picture.png"), data.ToArray());
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly Shadow Opaque = new(new Rgba(0, 0, 0), 2, 2);
    private static readonly Shadow Translucent = new(new Rgba(0, 0, 0, 128), 2, 2);

    private static Box Cell(int i, double w, double h, int perRow) => new(10 + i % perRow * (w + 2), 10 + i / perRow * (h + 2), w, h);

    private static IEnumerable<Element> Elements(string kind, bool shadowed) => kind switch
    {
        "opaque rectangles" => Enumerable.Range(0, 400).Select(i => (Element)new ShapeElement
        {
            Bounds = Cell(i, 9, 9, 50), Fill = new Rgba(200, 0, 0), Stroke = new Stroke(new Rgba(0, 0, 0), 1), Shadow = shadowed ? Opaque : null,
        }),
        "translucent rectangles" => Enumerable.Range(0, 400).Select(i => (Element)new ShapeElement
        {
            Bounds = Cell(i, 9, 9, 50), Fill = new Rgba(200, 0, 0, 128), Stroke = new Stroke(new Rgba(0, 0, 0), 1), Shadow = shadowed ? Opaque : null,
        }),
        "text boxes" => Enumerable.Range(0, 100).Select(i => (Element)new TextElement
        {
            Bounds = Cell(i, 58, 36, 10), Shadow = shadowed ? Translucent : null,
            Paragraphs = [new Paragraph { Runs = [new TextRun(new string('t', 1000), new TextStyle { Family = "Arial", Size = 10 })] }],
        }),
        "pictures" => Enumerable.Range(0, 100).Select(i => (Element)new ImageElement
        {
            Bounds = Cell(i, 50, 34, 10), Source = "picture.png", Shadow = shadowed ? Translucent : null,
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private double FastestOfFive(Page page)
    {
        using (Exporter.RenderBitmap(page, 150, new RenderContext(_dir))) { }   // warm-up
        var best = double.MaxValue;
        for (var i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            using (Exporter.RenderBitmap(page, 150, new RenderContext(_dir))) { }
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    [Theory]
    [InlineData("opaque rectangles")]
    [InlineData("translucent rectangles")]
    [InlineData("text boxes")]
    [InlineData("pictures")]
    public void ShadowedPages_CostAboutTheirElements_InPng(string kind)
    {
        var plain = FastestOfFive(new Page(612, 792) { Elements = [.. Elements(kind, false)] });
        var shadowed = FastestOfFive(new Page(612, 792) { Elements = [.. Elements(kind, true)] });
        Assert.True(shadowed <= 4 * plain, $"{kind}: {shadowed:F0} ms shadowed against {plain:F0} ms plain ({shadowed / plain:F1}x)");
    }

    private byte[] Pdf(params Element[] elements)
    {
        using var s = new MemoryStream();
        Exporter.ToPdf(new PubsmithDocument { Pages = [new Page(612, 792) { Elements = elements }] }, new RenderContext(_dir), s);
        return s.ToArray();
    }

    [Fact]
    public void AShadowedOpaqueShape_WritesNoImage_InPdf()
    {
        ShapeElement Rect(int i, ShapeKind kind, bool shadowed) => new()
        {
            Kind = kind, Bounds = Cell(i, 9, 9, 50), Fill = new Rgba(200, 0, 0), Stroke = new Stroke(new Rgba(0, 0, 0), 1), Shadow = shadowed ? Opaque : null,
        };
        var plain = Pdf([.. Enumerable.Range(0, 500).Select(i => Rect(i, ShapeKind.Rectangle, false))]);
        var shadowed = Pdf([.. Enumerable.Range(0, 500).Select(i => Rect(i, ShapeKind.Rectangle, true))]);
        Assert.Empty(PdfImages.In(shadowed));
        Assert.True(shadowed.Length - plain.Length < 64 * 500, $"{(shadowed.Length - plain.Length) / 500.0:F0} bytes per shadow");

        var ellipses = Pdf([.. Enumerable.Range(0, 500).Select(i => Rect(i, ShapeKind.Ellipse, true))]);
        Assert.Empty(PdfImages.In(ellipses));

        // A half-point line: 2 device pixels at the PDF's 300 dpi (a silhouette), 1 at 150 dpi (it would be layered).
        var thin = Pdf([.. Enumerable.Range(0, 100).Select(i => Rect(i, ShapeKind.Rectangle, true) with { Stroke = new Stroke(new Rgba(0, 0, 0), 0.5) })]);
        Assert.Empty(PdfImages.In(thin));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WordArt_BuildsItsGlyphsOnce(bool shadowed)
    {
        var wordArt = WordArt(WarpKind.ArchUp) with { Shadow = shadowed ? Translucent : null };
        var ctx = new RenderContext(_dir);
        using (Exporter.RenderBitmap(new Page(612, 792) { Elements = [wordArt] }, 72, ctx)) { }
        Assert.Equal(1, ctx.GlyphBuilds);
    }

    public static TheoryData<string> LayeredKinds() =>
        ["translucent shape with a 6 pt line", "translucent shape with a 6 pt line, rotated 30 degrees", "text box", "picture", "WordArt envelope", "WordArt arch"];

    private static TextElement WordArt(WarpKind kind) => new()
    {
        Bounds = new Box(200, 300, 180, 90), Insets = new Insets(0, 0, 0, 0), Fit = TextFit.Stretch, Warp = new TextWarp(kind),
        Outline = new Stroke(new Rgba(0, 0, 0), 2), Shadow = Translucent,
        Paragraphs = [new Paragraph { Runs = [new TextRun("WordArt", new TextStyle { Family = "Arial Black", Color = new Rgba(220, 120, 0) })] }],
    };

    private static Element Layered(string kind) => kind switch
    {
        "translucent shape with a 6 pt line" => new ShapeElement
        {
            Bounds = new Box(200, 300, 150, 80), Fill = new Rgba(0, 90, 200, 128), Stroke = new Stroke(new Rgba(0, 0, 0), 6), Shadow = Translucent,
        },
        "translucent shape with a 6 pt line, rotated 30 degrees" => (Layered("translucent shape with a 6 pt line") with { Rotation = 30 }),
        "text box" => new TextElement
        {
            Bounds = new Box(200, 300, 150, 80), Shadow = Translucent,
            Paragraphs = [new Paragraph { Runs = [new TextRun("A text box with a shadow, wrapping over a few lines.", new TextStyle { Family = "Arial", Size = 12 })] }],
        },
        "picture" => new ImageElement { Bounds = new Box(200, 300, 120, 80), Source = "picture.png", Stroke = new Stroke(new Rgba(0, 0, 0), 2), Shadow = Translucent },
        "WordArt envelope" => WordArt(WarpKind.CanUp),
        "WordArt arch" => WordArt(WarpKind.ArchUp),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>AC1b's layer size in points: computed here from the element, never from the renderer's own helper.</summary>
    private (double Width, double Height) Bound(Element e)
    {
        var (w, h) = Unrotated(e);
        if (e.Rotation == 0) return (w, h);
        // A rotated layer is written as the bounding box of the turned rectangle (the 2 pt slack turns with it).
        var a = e.Rotation * Math.PI / 180;
        var (c, s) = (Math.Abs(Math.Cos(a)), Math.Abs(Math.Sin(a)));
        return (w * c + h * s + 2, w * s + h * c + 2);
    }

    private (double Width, double Height) Unrotated(Element e)
    {
        switch (e)
        {
            case ShapeElement { Stroke: var s }: { var m = 2 * (2 * (s?.Width ?? 0) + 1); return (Math.Abs(e.Bounds.Width) + m + 2, Math.Abs(e.Bounds.Height) + m + 2); }
            case ImageElement { Stroke: var s }: { var m = 2 * (2 * (s?.Width ?? 0) + 1); return (Math.Abs(e.Bounds.Width) + m + 2, Math.Abs(e.Bounds.Height) + m + 2); }
            case TextElement { Fit: TextFit.Stretch } t:
            {
                // WordArt: its ink box, measured from a 72-dpi raster of the element alone.
                using var bmp = Exporter.RenderBitmap(new Page(612, 792) { Elements = [t with { Shadow = null }] }, 72, new RenderContext(_dir));
                int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
                for (var y = 0; y < bmp.Height; y++) for (var x = 0; x < bmp.Width; x++)
                    if (bmp.GetPixel(x, y) != SKColors.White) { left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
                var m = 2 * (2 * (t.Outline?.Width ?? 0) + 1);
                return (right - left + 1 + m + 2, bottom - top + 1 + m + 2);
            }
            default: return (Math.Abs(e.Bounds.Width) + 2, Math.Abs(e.Bounds.Height) + 2);
        }
    }

    [Theory]
    [MemberData(nameof(LayeredKinds))]
    public void EveryLayeredShadow_WritesNoMoreThanItsLayer_InPdf(string kind)
    {
        var element = Layered(kind);
        var (w, h) = Bound(element);
        var images = PdfImages.In(Pdf(element));
        Assert.NotEmpty(images);   // a layered shadow is written as an image
        Assert.All(images, i => Assert.True(i.Width <= w && i.Height <= h, $"{kind}: image {i.Width} x {i.Height}, layer at most {w:F0} x {h:F0}"));
    }
}
