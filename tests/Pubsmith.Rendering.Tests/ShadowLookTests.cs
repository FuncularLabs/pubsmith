using Pubsmith.Core;
using SkiaSharp;

namespace Pubsmith.Rendering.Tests;

// WI-005 AC2: every kind of shadow looks as it did when each shadow was the element drawn through a colour-filtered
// layer. That algorithm is kept here as the oracle; the renderer may draw shadows any cheaper way that matches it.
public sealed class ShadowLookTests : IDisposable
{
    private const double Dpi = 150;
    private readonly string _dir = Directory.CreateTempSubdirectory("pubsmith-shadow-").FullName;

    public ShadowLookTests()
    {
        // An opaque picture and one with transparency, for the picture rows.
        using (var bmp = new SKBitmap(60, 40))
        {
            for (var y = 0; y < 40; y++) for (var x = 0; x < 60; x++) bmp.SetPixel(x, y, new SKColor((byte)(x * 4), (byte)(y * 6), 160));
            Save(bmp, "picture.png");
        }
        using (var bmp = new SKBitmap(new SKImageInfo(60, 40, SKColorType.Rgba8888, SKAlphaType.Unpremul)))
        {
            for (var y = 0; y < 40; y++) for (var x = 0; x < 60; x++) bmp.SetPixel(x, y, new SKColor(200, 30, 30, (byte)(x * 4)));
            Save(bmp, "alpha.png");
        }
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Save(SKBitmap bmp, string name)
    {
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        using var f = File.Create(Path.Combine(_dir, name));
        data.SaveTo(f);
    }

    private static readonly TextStyle Text = new() { Family = "Arial", Size = 14, Color = new Rgba(20, 20, 120) };
    private static readonly Stroke Line = new(new Rgba(0, 0, 0), 3);

    private static TextElement WordArt(WarpKind kind) => new()
    {
        Bounds = new Box(60, 50, 180, 90), Insets = new Insets(0, 0, 0, 0), Fit = TextFit.Stretch, Warp = new TextWarp(kind),
        Outline = new Stroke(new Rgba(0, 0, 0), 2), Shadow = new Shadow(new Rgba(0, 0, 0, 160), 5, 5),
        Paragraphs = [new Paragraph { Runs = [new TextRun("WordArt", Text with { Family = "Arial Black", Color = new Rgba(220, 120, 0) })] }],
    };

    public static TheoryData<string> Kinds() =>
    [
        "opaque shape", "outline-only shape", "ellipse", "rotated opaque shape", "translucent fill and line under a translucent shadow",
        "alpha-0 fill with an opaque line", "rotated picture", "oval-masked picture with a line", "picture with transparency",
        "rotated text box", "WordArt envelope with an outline", "WordArt arch with an outline", "WordArt circle with an outline",
        "negative-size translucent shape", "negative-size picture with a line", "negative-size opaque shape with a line",
        "quarter-point line on a rectangle", "half-point line on a rectangle", "quarter-point line on an ellipse", "half-point line on an ellipse",
        "translucent ellipse with a line", "opaque shape with a coloured translucent shadow", "opaque fill with a translucent line",
        "shape with neither fill nor line", "WordArt arch with an 8 pt outline", "opaque ellipse with Publisher's grey shadow",
        "opaque ellipse with a translucent line", "alpha-0 fill and no line",
    ];

    /// <summary>A bounded layer can differ from a page-sized one at a few edge pixels (WI-005 AC2, measured worst 0.170).</summary>
    private static double Bound(string kind) => kind == "translucent ellipse with a line" ? 0.2 : 0.1;

    private static Element Make(string kind) => kind switch
    {
        "opaque shape" => new ShapeElement { Bounds = new Box(60, 50, 120, 70), Fill = new Rgba(0, 90, 200), Stroke = Line, Shadow = new Shadow(new Rgba(0, 0, 0), 6, 6) },
        "outline-only shape" => new ShapeElement { Bounds = new Box(60, 50, 120, 70), Stroke = Line, Shadow = new Shadow(new Rgba(0, 0, 0, 128), 6, 6) },
        "ellipse" => new ShapeElement { Kind = ShapeKind.Ellipse, Bounds = new Box(60, 50, 120, 70), Fill = new Rgba(0, 150, 60), Stroke = Line, Shadow = new Shadow(new Rgba(0, 0, 0, 200), 6, 6) },
        "rotated opaque shape" => new ShapeElement { Bounds = new Box(60, 50, 120, 70), Rotation = 25, Fill = new Rgba(0, 90, 200), Stroke = Line, Shadow = new Shadow(new Rgba(0, 0, 0), 6, 6) },
        "translucent fill and line under a translucent shadow" => new ShapeElement
        {
            Bounds = new Box(60, 50, 120, 70), Fill = new Rgba(0, 90, 200, 128), Stroke = new Stroke(new Rgba(0, 0, 0, 128), 6), Shadow = new Shadow(new Rgba(0, 0, 0, 128), 6, 6),
        },
        "alpha-0 fill with an opaque line" => new ShapeElement { Bounds = new Box(60, 50, 120, 70), Fill = new Rgba(0, 90, 200, 0), Stroke = Line, Shadow = new Shadow(new Rgba(0, 0, 0), 6, 6) },
        "rotated picture" => new ImageElement { Bounds = new Box(60, 50, 120, 80), Rotation = 30, Source = "picture.png", Shadow = new Shadow(new Rgba(0, 0, 0, 160), 6, 6) },
        "oval-masked picture with a line" => new ImageElement { Bounds = new Box(60, 50, 120, 80), Source = "picture.png", Mask = ShapeKind.Ellipse, Stroke = Line, Shadow = new Shadow(new Rgba(0, 0, 0, 160), 6, 6) },
        "picture with transparency" => new ImageElement { Bounds = new Box(60, 50, 120, 80), Source = "alpha.png", Shadow = new Shadow(new Rgba(0, 0, 0, 200), 6, 6) },
        "rotated text box" => new TextElement
        {
            Bounds = new Box(60, 50, 160, 90), Rotation = 20, Shadow = new Shadow(new Rgba(0, 0, 0, 160), 3, 3),
            Paragraphs = [new Paragraph { Runs = [new TextRun("A rotated text box whose words wrap over a few lines, with a shadow.", Text)] }],
        },
        "WordArt envelope with an outline" => WordArt(WarpKind.CanUp),
        "WordArt arch with an outline" => WordArt(WarpKind.ArchUp),
        "WordArt circle with an outline" => WordArt(WarpKind.Circle),
        // doc.json allows a negative width or height; the element is still drawn (Skia sorts the rectangle).
        "negative-size translucent shape" => new ShapeElement { Bounds = new Box(180, 120, -120, -70), Fill = new Rgba(0, 90, 200, 128), Stroke = Line, Shadow = new Shadow(new Rgba(0, 0, 0, 128), 6, 6) },
        "negative-size opaque shape with a line" => new ShapeElement { Bounds = new Box(180, 120, -120, -70), Fill = new Rgba(0, 90, 200), Stroke = Line, Shadow = new Shadow(new Rgba(0, 0, 0), 6, 6) },
        "quarter-point line on a rectangle" => Thin(ShapeKind.Rectangle, 0.25),
        "half-point line on a rectangle" => Thin(ShapeKind.Rectangle, 0.5),
        "quarter-point line on an ellipse" => Thin(ShapeKind.Ellipse, 0.25),
        "half-point line on an ellipse" => Thin(ShapeKind.Ellipse, 0.5),
        "opaque shape with a coloured translucent shadow" => new ShapeElement { Bounds = new Box(60, 50, 120, 70), Fill = new Rgba(250, 220, 0), Stroke = Line, Shadow = new Shadow(new Rgba(40, 80, 200, 160), 6, 6) },
        "opaque fill with a translucent line" => new ShapeElement { Bounds = new Box(60, 50, 120, 70), Fill = new Rgba(0, 90, 200), Stroke = new Stroke(new Rgba(0, 0, 0, 128), 4), Shadow = new Shadow(new Rgba(0, 0, 0), 6, 6) },
        // The importer emits such shapes (an unfilled frame with its line off): they draw nothing and cast nothing.
        "shape with neither fill nor line" => new ShapeElement { Bounds = new Box(60, 50, 120, 70), Shadow = new Shadow(new Rgba(0, 0, 0), 6, 6) },
        "WordArt arch with an 8 pt outline" => WordArt(WarpKind.ArchUp) with { Outline = new Stroke(new Rgba(0, 0, 0), 8) },
        "opaque ellipse with Publisher's grey shadow" => new ShapeElement { Kind = ShapeKind.Ellipse, Bounds = new Box(60, 50, 120, 70), Fill = new Rgba(250, 220, 0), Stroke = Line, Shadow = new Shadow(new Rgba(128, 128, 128), 6, 6) },
        "opaque ellipse with a translucent line" => new ShapeElement { Kind = ShapeKind.Ellipse, Bounds = new Box(60, 50, 120, 70), Fill = new Rgba(0, 90, 200), Stroke = new Stroke(new Rgba(0, 0, 0, 128), 4), Shadow = new Shadow(new Rgba(0, 0, 0), 6, 6) },
        "alpha-0 fill and no line" => new ShapeElement { Bounds = new Box(60, 50, 120, 70), Fill = new Rgba(0, 90, 200, 0), Shadow = new Shadow(new Rgba(0, 0, 0), 6, 6) },
        "translucent ellipse with a line" => new ShapeElement { Kind = ShapeKind.Ellipse, Bounds = new Box(60, 50, 120, 70), Fill = new Rgba(0, 150, 60, 128), Stroke = Line, Shadow = new Shadow(new Rgba(0, 0, 0, 128), 6, 6) },
        "negative-size picture with a line" => new ImageElement { Bounds = new Box(180, 130, -120, -80), Source = "picture.png", Stroke = Line, Shadow = new Shadow(new Rgba(0, 0, 0, 160), 6, 6) },
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static ShapeElement Thin(ShapeKind kind, double width) => new()
    {
        Kind = kind, Bounds = new Box(60, 50, 120, 70), Fill = new Rgba(250, 220, 0), Stroke = new Stroke(new Rgba(0, 0, 0), width),
        Shadow = new Shadow(new Rgba(0, 0, 0), 4, 4),
    };

    /// <summary>
    /// The previous shadow: the element (drawn by the public renderer, shadow removed) through a layer that recolours
    /// it, offset; then the element. Only public API, so the oracle does not depend on the code under test's insides.
    /// </summary>
    private static SKBitmap Oracle(Page page, RenderContext ctx, double dpi = Dpi)
    {
        var (w, h) = Exporter.PixelSize(page, dpi);
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        canvas.Scale((float)(dpi / 72));
        foreach (var e in page.Elements)
        {
            var alone = new Page(page.Width, page.Height) { Elements = [e with { Shadow = null }] };
            if (e.Shadow is { } s)
            {
                using var filter = SKColorFilter.CreateBlendMode(new SKColor(s.Color.R, s.Color.G, s.Color.B), SKBlendMode.SrcIn);
                using var layer = new SKPaint { ColorFilter = filter, Color = new SKColor(0, 0, 0, s.Color.A) };
                canvas.Save();
                canvas.Translate((float)s.OffsetX, (float)s.OffsetY);
                canvas.SaveLayer(layer);
                PageRenderer.Render(canvas, alone, ctx);
                canvas.Restore();
                canvas.Restore();
            }
            PageRenderer.Render(canvas, alone, ctx);
        }
        return bmp;
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void EveryKindOfShadow_LooksAsBefore(string kind)
    {
        var page = new Page(300, 200) { Elements = [Make(kind)] };
        using var expected = Oracle(page, new RenderContext(_dir));
        using var actual = Exporter.RenderBitmap(page, Dpi, new RenderContext(_dir));
        var score = ImageComparer.Score(expected, actual);
        Assert.True(score <= Bound(kind), $"{kind}: score {score:F3}");
    }

    public static TheoryData<string, double> KindsOnACallersCanvas()
    {
        var data = new TheoryData<string, double>();
        foreach (string kind in Kinds()) foreach (var dpi in new[] { 72.0, 96.0 }) data.Add(kind, dpi);
        return data;
    }

    [Theory]
    [MemberData(nameof(KindsOnACallersCanvas))]
    public void EveryKindOfShadow_OnACallersOwnCanvas_LooksAsBefore(string kind, double dpi)
    {
        // Through the public PageRenderer.Render on a raster canvas the renderer did not make: judged at its own scale.
        var page = new Page(300, 200) { Elements = [Make(kind)] };
        using var expected = Oracle(page, new RenderContext(_dir), dpi);
        var (w, h) = Exporter.PixelSize(page, dpi);
        using var actual = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(actual))
        {
            canvas.Clear(SKColors.White);
            canvas.Scale((float)(dpi / 72));
            PageRenderer.Render(canvas, page, new RenderContext(_dir));
        }
        var score = ImageComparer.Score(expected, actual);
        Assert.True(score <= Bound(kind), $"{kind} at {dpi} dpi: score {score:F3}");
    }

    [Fact]
    public void ShadowsAreDrawnElementByElement()
    {
        // Every route a shadow takes (an opaque shape's silhouette; the layer of a translucent shape, a picture, a text
        // box, WordArt), each cast up and left onto the opaque shape drawn just before it: drawn after that shape, the
        // shadow shows on top of it.
        var back = new Shadow(new Rgba(0, 0, 0, 160), -30, -30);
        static ShapeElement Under(double x, double y) => new() { Bounds = new Box(x, y, 100, 70), Fill = new Rgba(250, 220, 0) };
        var page = new Page(420, 300)
        {
            Elements =
            [
                Under(20, 20), new ShapeElement { Bounds = new Box(60, 50, 100, 45), Fill = new Rgba(0, 90, 200), Stroke = Line, Shadow = back },
                Under(220, 20), new ShapeElement { Bounds = new Box(260, 50, 100, 45), Fill = new Rgba(0, 90, 200, 128), Stroke = Line, Shadow = back },
                Under(20, 120), new ImageElement { Bounds = new Box(60, 150, 90, 60), Source = "picture.png", Shadow = back },
                Under(220, 120), new TextElement
                {
                    Bounds = new Box(260, 150, 140, 60), Shadow = back, Paragraphs = [new Paragraph { Runs = [new TextRun("Shadowed words over a shape", Text)] }],
                },
                Under(20, 220), WordArt(WarpKind.ArchUp) with { Bounds = new Box(60, 230, 160, 60), Shadow = back },
            ],
        };
        using var expected = Oracle(page, new RenderContext(_dir));
        using var actual = Exporter.RenderBitmap(page, Dpi, new RenderContext(_dir));
        var score = ImageComparer.Score(expected, actual);
        Assert.True(score <= 0.1, $"score {score:F3}");
    }

    [Fact]
    public void AfterAPdf_ACallersCanvas_IsJudgedAtItsOwnScale()
    {
        // The PDF mark is cleared when ToPdf returns: the same context then draws a thin line on a 72-dpi canvas as
        // before. A half-point line is 2.1 device pixels at the PDF's 300 dpi (a silhouette) but 0.5 at 72 (layered).
        var ctx = new RenderContext(_dir);
        var page = new Page(300, 200) { Elements = [Make("half-point line on an ellipse")] };
        using (var pdf = new MemoryStream()) Exporter.ToPdf(new PubsmithDocument { Pages = [page] }, ctx, pdf);
        using var expected = Oracle(page, new RenderContext(_dir), 72);
        var (w, h) = Exporter.PixelSize(page, 72);
        using var actual = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(actual))
        {
            canvas.Clear(SKColors.White);
            PageRenderer.Render(canvas, page, ctx);
        }
        var score = ImageComparer.Score(expected, actual);
        Assert.True(score <= 0.1, $"score {score:F3}");
    }

    [Fact]
    public void TheEllipseSilhouette_ForAPdfCanvas_IsAsFineAsAt300Dpi()
    {
        // The scale the renderer itself chooses for a PDF canvas (a recording canvas has no pixels of its own).
        var ctx = new RenderContext(_dir) { Target = RenderTarget.Pdf };
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(SKRect.Create(300, 200));
        var r = SKRect.Create(60, 50, 120, 70);
        using var forPdf = PageRenderer.EllipseSilhouette(r, 3, PageRenderer.SilhouetteScale(canvas, ctx));
        using var fine = PageRenderer.EllipseSilhouette(r, 3, 16);
        double Area(SKPath path)
        {
            using var bmp = new SKBitmap(2400, 1600);
            using (var c = new SKCanvas(bmp))
            {
                c.Clear(SKColors.Transparent);
                c.Scale(8);
                using var paint = new SKPaint { IsAntialias = false, Color = SKColors.Black };
                c.DrawPath(path, paint);
            }
            var n = 0;
            foreach (var px in bmp.Pixels) if (px.Alpha > 0) n++;
            return n;
        }
        var (a, b) = (Area(forPdf!), Area(fine!));
        Assert.True(Math.Abs(a - b) / b <= 0.001, $"{a} against {b} pixels ({100 * Math.Abs(a - b) / b:F3} %)");
    }

    [Fact]
    public void TheOracle_SeesADifferentShadow()
    {
        // The comparison has teeth: the same element with its shadow moved 3 pt scores well above the bound.
        var element = Make("opaque shape");
        var moved = element with { Shadow = element.Shadow! with { OffsetX = 9 } };
        using var expected = Oracle(new Page(300, 200) { Elements = [element] }, new RenderContext(_dir));
        using var actual = Exporter.RenderBitmap(new Page(300, 200) { Elements = [moved] }, Dpi, new RenderContext(_dir));
        Assert.True(ImageComparer.Score(expected, actual) > 0.5);
    }
}
