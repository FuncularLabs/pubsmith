using Pubsmith.Core;
using SkiaSharp;

namespace Pubsmith.Rendering.Tests;

// WI-006 AC1, AC6 (PNG bytes) and AC8: what a document states exactly (pages, PNG pixels, a PDF's typefaces, font
// names) is checked before anything is written, by the exporters for their own call and by RenderLimits.Check for a
// whole command; the check computes exactly what the exporters add, and charges nothing.
public sealed class CountedLimitTests
{
    // Every limit that is not under test is out of reach, so only the counted limits can refuse.
    private static readonly RenderLimits Open = RenderLimits.Default with
    {
        MaxPages = int.MaxValue, MaxPngPixels = long.MaxValue, MaxPdfTypefaces = int.MaxValue, MaxFontNames = int.MaxValue,
        MaxTextBoxCharacters = int.MaxValue, MaxTextBoxRuns = int.MaxValue, MaxWordArtGlyphs = int.MaxValue,
        MaxDrawTime = TimeSpan.MaxValue, MaxPdfBytes = long.MaxValue, MaxPngBytes = long.MaxValue,
    };

    private static readonly string[] CountedLimits = [RenderLimitException.Pages, RenderLimitException.PngPixels, RenderLimitException.PdfTypefaces, RenderLimitException.FontNames];

    private static readonly (string Family, bool Bold, bool Italic)[] Styles =
        [("Arial", false, false), ("Arial", true, false), ("Arial", false, true), ("Times New Roman", false, false)];

    private static Page TextPage(double w = 100, double h = 100, params (string Family, bool Bold, bool Italic)[] styles) => new(w, h)
    {
        Elements =
        [
            new TextElement
            {
                Bounds = new Box(0, 0, w, h),
                Paragraphs = [new Paragraph { Runs = (styles.Length == 0 ? [Styles[0]] : styles).Select(s => new TextRun("Ab ", new TextStyle { Family = s.Family, Bold = s.Bold, Italic = s.Italic })).ToList() }],
            },
        ],
    };

    private static TextElement WordArt(string family) => new()
    {
        Bounds = new Box(0, 0, 50, 20), Fit = TextFit.Stretch,
        Paragraphs = [new Paragraph { Runs = [new TextRun("W", new TextStyle { Family = family })] }],
    };

    private static PubsmithDocument Doc(params Page[] pages) => new() { Pages = pages };

    private static RenderContext Ctx(RenderLimits limits, FontResolver? fonts = null) => new(null, limits, fonts: fonts);

    private static long PngSize(Page page, double dpi)
    {
        using var s = new MemoryStream();
        Exporter.ToPng(page, dpi, new RenderContext(), s);
        return s.Length;
    }

    // ---------- refused before writing; rendered at the limit ----------

    [Theory]
    [InlineData("pages, PDF", 0)]
    [InlineData("pages, PNG", 3)]
    [InlineData("PNG pixels", 2)]
    [InlineData("PDF typefaces", 0)]
    [InlineData("PNG bytes", 2)]   // PNG bytes are known only once the page is drawn and encoded
    public void OverEachLimit_IsRefusedBeforeWriting(string row, int pagesDrawn)
    {
        var (ex, written, drawn) = Refuse(row, atTheLimit: false);
        Assert.NotNull(ex);
        Assert.Equal(0, written);
        Assert.Equal(pagesDrawn, drawn);   // a counted limit refuses before the page is drawn
        var (limit, value) = Expected(row);
        Assert.Equal(limit, ex.Limit);
        Assert.Equal(value, ex.Value);
        Assert.Null(ex.Page);
        Assert.Null(ex.Element);
        Assert.Contains(limit, ex.Message);
    }

    [Theory]
    [InlineData("pages, PDF", 4)]
    [InlineData("pages, PNG", 4)]
    [InlineData("PNG pixels", 3)]
    [InlineData("PDF typefaces", 1)]
    [InlineData("PNG bytes", 2)]
    public void AtEachLimit_IsRendered(string row, int pagesDrawn)
    {
        var (ex, written, drawn) = Refuse(row, atTheLimit: true);
        Assert.Null(ex);
        Assert.True(written > 0);
        Assert.Equal(pagesDrawn, drawn);
    }

    private static (string Limit, long Value) Expected(string row) => row switch
    {
        "pages, PDF" => (RenderLimitException.Pages, 4),
        "pages, PNG" => (RenderLimitException.Pages, 4),
        "PNG pixels" => (RenderLimitException.PngPixels, 30_000),
        "PDF typefaces" => (RenderLimitException.PdfTypefaces, 3),
        "PNG bytes" => (RenderLimitException.PngBytes, 2 * PngSize(Drawn(0), 72)),
        _ => throw new ArgumentException(row),
    };

    // A page whose drawing is visible in its context: it looks up a picture of its own (missing, so drawn as a placeholder).
    private static Page Drawn(int k, params (string Family, bool Bold, bool Italic)[] styles)
    {
        var page = TextPage(100, 100, styles);
        return page with { Elements = [.. page.Elements, new ImageElement { Bounds = new Box(0, 0, 10, 10), Source = $"no-such-picture-{k}.png" }] };
    }

    // Runs the row's last export one unit over its limit (or exactly at it); returns its refusal, what that export
    // wrote, and how many pages the context drew (the pictures it looked up).
    private static (RenderLimitException? Refusal, long Written, int PagesDrawn) Refuse(string row, bool atTheLimit)
    {
        var over = atTheLimit ? 0 : 1;
        var s = new MemoryStream();
        RenderLimitException? Try(Action a) { try { a(); return null; } catch (RenderLimitException e) { return e; } }
        switch (row)
        {
            case "pages, PDF":
            {
                using var ctx = Ctx(Open with { MaxPages = 4 - over });
                return (Try(() => Exporter.ToPdf(Doc(Drawn(0), Drawn(1), Drawn(2), Drawn(3)), ctx, s)), s.Length, ctx.Pictures.Reads);
            }
            case "pages, PNG":
            {
                using var ctx = Ctx(Open with { MaxPages = 4 - over });
                for (var i = 0; i < 3; i++) Exporter.ToPng(Drawn(i), 72, ctx, new MemoryStream());
                return (Try(() => Exporter.ToPng(Drawn(3), 72, ctx, s)), s.Length, ctx.Pictures.Reads);
            }
            case "PNG pixels":
            {
                using var ctx = Ctx(Open with { MaxPngPixels = 30_000 - over });   // 100 x 100 pt at 72 dpi: 10,000 pixels a page
                for (var i = 0; i < 2; i++) Exporter.ToPng(Drawn(i), 72, ctx, new MemoryStream());
                return (Try(() => Exporter.ToPng(Drawn(2), 72, ctx, s)), s.Length, ctx.Pictures.Reads);
            }
            case "PDF typefaces":
            {
                using var ctx = Ctx(Open with { MaxPdfTypefaces = 3 - over });
                return (Try(() => Exporter.ToPdf(Doc(Drawn(0, Styles[0], Styles[1], Styles[2])), ctx, s)), s.Length, ctx.Pictures.Reads);
            }
            case "PNG bytes":
            {
                using var ctx = Ctx(Open with { MaxPngBytes = 2 * PngSize(Drawn(0), 72) - over });
                Exporter.ToPng(Drawn(0), 72, ctx, new MemoryStream());
                return (Try(() => Exporter.ToPng(Drawn(1), 72, ctx, s)), s.Length, ctx.Pictures.Reads);
            }
            default: throw new ArgumentException(row);
        }
    }

    [Fact]
    public void WordArtFonts_AreNotCountedAsPdfTypefaces()
    {
        // WordArt is drawn as paths: its fonts are not embedded.
        var page = TextPage(100, 100, Styles[0], Styles[1]) with { Elements = [.. TextPage(100, 100, Styles[0], Styles[1]).Elements, WordArt("Times New Roman"), WordArt("Courier New")] };
        using var ctx = Ctx(Open with { MaxPdfTypefaces = 2 });
        RenderLimits.Check(Doc(page), new RenderOutputs(Pdf: true, PngDpi: null), ctx);
        using var s = new MemoryStream();
        Exporter.ToPdf(Doc(page), ctx, s);
        Assert.True(s.Length > 0);
    }

    // ---------- the check is exactly the exporters' count, and charges nothing ----------

    [Fact]
    public void TheCheck_MatchesWhatTheExportersAdd()
    {
        var rng = new Random(6006);
        var outcomes = new Dictionary<string, int>();
        for (var n = 0; n < 400; n++)
        {
            var limits = Open with { MaxPages = rng.Next(1, 12), MaxPngPixels = rng.Next(1, 120_000), MaxPdfTypefaces = rng.Next(1, 5), MaxFontNames = rng.Next(1, 8) };
            using var ctx = Ctx(limits);

            // A command after an earlier one in the same context (the counters it left are part of the check).
            if (rng.Next(2) == 0) Export(RandomDoc(rng), RandomOutputs(rng), ctx);

            var doc = RandomDoc(rng);
            var outputs = RandomOutputs(rng);
            string checkVerdict;
            try { RenderLimits.Check(doc, outputs, ctx); checkVerdict = "passed"; }
            catch (RenderLimitException e) { checkVerdict = e.Limit; }
            var exportVerdict = Export(doc, outputs, ctx);

            Assert.True((checkVerdict == "passed") == (exportVerdict == "passed"), $"command {n}: the check {checkVerdict}, the exports {exportVerdict}");
            Assert.Contains(checkVerdict, CountedLimits.Append("passed"));
            outcomes[checkVerdict] = outcomes.GetValueOrDefault(checkVerdict) + 1;
        }
        // The generator reaches every verdict, so the equivalence above is not vacuous.
        foreach (var verdict in CountedLimits.Append("passed"))
            Assert.True(outcomes.GetValueOrDefault(verdict) >= 10, $"{verdict}: {outcomes.GetValueOrDefault(verdict)}");
    }

    // Names that resolve and names that do not, in text boxes, in WordArt and as an empty paragraph's default.
    private static readonly string[] Names = ["Courier New", "Segoe UI", "No Such Font A", "No Such Font B", "Calibri"];

    private static PubsmithDocument RandomDoc(Random rng) =>
        Doc(Enumerable.Range(0, rng.Next(1, 5)).Select(_ =>
        {
            var styles = Styles.Where(_ => rng.Next(2) == 0).ToArray();
            var page = TextPage(rng.Next(20, 200), rng.Next(20, 200), styles);
            var extra = new List<Element>();
            if (rng.Next(3) == 0) extra.Add(WordArt(Names[rng.Next(Names.Length)]));
            if (rng.Next(3) == 0) extra.Add(new TextElement { Bounds = new Box(0, 0, 50, 20), Paragraphs = [new Paragraph { Runs = [new TextRun("b", new TextStyle { Family = Names[rng.Next(Names.Length)] })] }] });
            if (rng.Next(5) == 0) extra.Add(new TextElement { Bounds = new Box(0, 0, 50, 20), Paragraphs = [new Paragraph()] });
            return page with { Elements = [.. page.Elements, .. extra] };
        }).ToArray());

    private static RenderOutputs RandomOutputs(Random rng) => rng.Next(3) switch
    {
        0 => new(Pdf: true, PngDpi: null),
        1 => new(Pdf: false, PngDpi: 36 * rng.Next(1, 4)),
        _ => new(Pdf: true, PngDpi: 36 * rng.Next(1, 4)),
    };

    // As the CLI runs a command: the PDF, then each page's PNG; "passed" or the counted limit that refused it.
    private static string Export(PubsmithDocument doc, RenderOutputs outputs, RenderContext ctx)
    {
        try
        {
            if (outputs.Pdf) Exporter.ToPdf(doc, ctx, new MemoryStream());
            if (outputs.PngDpi is { } dpi)
                foreach (var p in doc.Pages) Exporter.ToPng(p, dpi, ctx, new MemoryStream());
            return "passed";
        }
        catch (RenderLimitException e) when (CountedLimits.Contains(e.Limit)) { return e.Limit; }
    }

    [Fact]
    public void TheCheck_ChargesNothing()
    {
        var doc = Doc(TextPage(100, 100, Styles[0], Styles[1]), TextPage(100, 100, Styles[2]));
        var outputs = new RenderOutputs(Pdf: true, PngDpi: 72);
        // Exactly what the command needs: 2 PDF pages and 2 PNG pages, 20,000 pixels, 3 typefaces.
        using var ctx = Ctx(Open with { MaxPages = 4, MaxPngPixels = 20_000, MaxPdfTypefaces = 3 });
        for (var i = 0; i < 10; i++) RenderLimits.Check(doc, outputs, ctx);
        Assert.Equal("passed", Export(doc, outputs, ctx));

        // Nor are names charged: a check of one font's document leaves room for another's, under a limit of one name.
        using var one = Ctx(Open with { MaxFontNames = 1 });
        RenderLimits.Check(Doc(TextPage(100, 100, ("Times New Roman", false, false))), new RenderOutputs(Pdf: true, PngDpi: null), one);
        Assert.Equal("passed", Export(Doc(TextPage(100, 100, Styles[0])), new RenderOutputs(Pdf: true, PngDpi: null), one));
    }

    // ---------- the font-name pre-pass ----------

    [Fact]
    public void ThePrePass_StopsAtTheNameLimit_BeforeResolving()
    {
        var page = new Page(100, 100)
        {
            Elements = [new TextElement { Bounds = new Box(0, 0, 100, 100), Paragraphs = [new Paragraph { Runs = Enumerable.Range(0, 10_000).Select(i => new TextRun("a", new TextStyle { Family = $"No Such Font {i:D5}" })).ToList() }] }],
        };
        var fonts = new FontResolver();
        using var ctx = Ctx(Open with { MaxFontNames = 100 }, fonts);
        var ex = Assert.Throws<RenderLimitException>(() => RenderLimits.Check(Doc(page), new RenderOutputs(Pdf: true, PngDpi: null), ctx));
        Assert.Equal(RenderLimitException.FontNames, ex.Limit);
        Assert.Equal(101, ex.Value);
        Assert.True(fonts.Resolutions <= 101, $"{fonts.Resolutions} resolutions");
    }

    [Fact]
    public void ToPdf_RefusesTooManyFontNames_BeforeWritingOrResolvingThem()
    {
        // The names of a PDF's text boxes are counted as their typefaces are resolved, before anything is written.
        var page = new Page(100, 100)
        {
            Elements = [new TextElement { Bounds = new Box(0, 0, 100, 100), Paragraphs = [new Paragraph { Runs = Enumerable.Range(0, 500).Select(i => new TextRun("a", new TextStyle { Family = $"No Such Font {i:D3}" })).ToList() }] }],
        };
        var fonts = new FontResolver();
        using var ctx = Ctx(Open with { MaxFontNames = 100 }, fonts);
        var s = new MemoryStream();
        var ex = Assert.Throws<RenderLimitException>(() => Exporter.ToPdf(Doc(page), ctx, s));
        Assert.Equal(RenderLimitException.FontNames, ex.Limit);
        Assert.Equal(101, ex.Value);
        Assert.Equal(0, s.Length);
        Assert.True(fonts.Resolutions <= 101, $"{fonts.Resolutions} resolutions");
    }

    [Fact]
    public void ThePrePass_CutsNamesTo256_AndIgnoresCase()
    {
        // 300-character names that differ only after character 256, or only in case, are one name.
        var stem = new string('x', 256);
        var page = new Page(100, 100)
        {
            Elements = [new TextElement { Bounds = new Box(0, 0, 100, 100), Paragraphs = [new Paragraph { Runs = Enumerable.Range(0, 20).Select(i => new TextRun("a", new TextStyle { Family = (i % 2 == 0 ? stem : stem.ToUpperInvariant()) + $"tail {i:D2}" + new string('y', 30) })).ToList() }] }],
        };
        using var ctx = Ctx(Open with { MaxFontNames = 1 });
        RenderLimits.Check(Doc(page), new RenderOutputs(Pdf: true, PngDpi: null), ctx);
    }

    // ---------- a refusal is sticky ----------

    [Fact]
    public void ARefusedContext_RefusesEveryLaterRender()
    {
        var page = TextPage();
        using var ctx = Ctx(Open with { MaxPages = 1 });
        Assert.Throws<RenderLimitException>(() => Exporter.ToPdf(Doc(page, page), ctx, new MemoryStream()));
        var counted = (ctx.PagesWritten, ctx.PngPixelsWritten, ctx.PngBytesWritten);

        // Each later render throws before it counts, writes or draws anything, although it would fit on its own.
        var pdf = new MemoryStream();
        Assert.Equal(RenderLimitException.Pages, Assert.Throws<RenderLimitException>(() => Exporter.ToPdf(Doc(page), ctx, pdf)).Limit);
        Assert.Equal(0, pdf.Length);
        var png = new MemoryStream();
        Assert.Throws<RenderLimitException>(() => Exporter.ToPng(page, 72, ctx, png));
        Assert.Equal(0, png.Length);
        Assert.Throws<RenderLimitException>(() => Exporter.RenderBitmap(page, 72, ctx));
        using var bmp = new SKBitmap(100, 100);
        bmp.Erase(SKColors.White);
        using (var canvas = new SKCanvas(bmp))
            Assert.Throws<RenderLimitException>(() => PageRenderer.Render(canvas, new Page(100, 100) { Elements = [new ShapeElement { Bounds = new Box(0, 0, 100, 100), Fill = new Rgba(255, 0, 0) }] }, ctx));
        Assert.Equal(SKColors.White, bmp.GetPixel(50, 50));
        Assert.Throws<RenderLimitException>(() => RenderLimits.Check(Doc(page), new RenderOutputs(Pdf: true, PngDpi: null), ctx));
        Assert.Equal(counted, (ctx.PagesWritten, ctx.PngPixelsWritten, ctx.PngBytesWritten));
    }

    public static TheoryData<string> Refusals() =>
    [
        RenderLimitException.Pages, RenderLimitException.PngPixels, RenderLimitException.PdfTypefaces, RenderLimitException.FontNames,
        RenderLimitException.TextBoxCharacters, RenderLimitException.TextBoxRuns, RenderLimitException.WordArtGlyphs,
        RenderLimitException.DrawTime, "pdf-bytes at a checkpoint", "pdf-bytes at a page's end", RenderLimitException.PngBytes,
        RenderLimitException.PdfLayerPixels, "font-names in a PDF's typeface pass", "pages of a bitmap", "a layer that is not finite",
    ];

    private sealed class StepClock : TimeProvider
    {
        private long _now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _now += TimeSpan.TicksPerSecond;
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public void EveryRefusal_IsSticky(string row)
    {
        var dir = Directory.CreateTempSubdirectory("pubsmith-sticky-").FullName;
        try
        {
            var shape = new ShapeElement { Bounds = new Box(0, 0, 10, 10), Fill = new Rgba(0, 0, 255) };
            Element Pic(int i)
            {
                var rng = new Random(i);
                using var bmp = new SKBitmap(40, 40);
                for (var y = 0; y < 40; y++) for (var x = 0; x < 40; x++) bmp.SetPixel(x, y, new SKColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256)));
                using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(Path.Combine(dir, $"p{i}.png"), data.ToArray());
                return new ImageElement { Bounds = new Box(0, 0, 40, 40), Source = $"p{i}.png" };
            }
            Page Of(params Element[] e) => new(100, 100) { Elements = e };
            TextElement Text(string s, string family = "Arial", TextFit fit = TextFit.None) => new() { Bounds = new Box(0, 0, 100, 50), Fit = fit, Paragraphs = [new Paragraph { Runs = [new TextRun(s, new TextStyle { Family = family })] }] };
            long pageEnd;
            using (var probe = new RenderContext(dir, Open)) { Exporter.ToPdf(Doc(Of(shape, shape)), probe, new MemoryStream()); pageEnd = probe.PdfBytesAtChecks[0]; }

            var (limits, provoke) = row switch
            {
                RenderLimitException.Pages => (Open with { MaxPages = 1 }, (Action<RenderContext>)(c => Exporter.ToPdf(Doc(Of(), Of()), c, new MemoryStream()))),
                RenderLimitException.PngPixels => (Open with { MaxPngPixels = 100 }, c => Exporter.ToPng(Of(), 72, c, new MemoryStream())),
                RenderLimitException.PdfTypefaces => (Open with { MaxPdfTypefaces = 1 }, c => Exporter.ToPdf(Doc(Of(Text("a"), Text("b", "Times New Roman"))), c, new MemoryStream())),
                RenderLimitException.FontNames => (Open with { MaxFontNames = 1 }, c => Exporter.ToPng(Of(Text("a"), Text("b", "Times New Roman")), 72, c, new MemoryStream())),
                RenderLimitException.TextBoxCharacters => (Open with { MaxTextBoxCharacters = 3 }, c => Exporter.ToPng(Of(Text("abcd")), 72, c, new MemoryStream())),
                RenderLimitException.TextBoxRuns => (Open with { MaxTextBoxRuns = 1 }, c => Exporter.ToPng(Of(new TextElement { Bounds = new Box(0, 0, 100, 50), Paragraphs = [new Paragraph { Runs = [new TextRun("a", new TextStyle()), new TextRun("b", new TextStyle())] }] }), 72, c, new MemoryStream())),
                RenderLimitException.WordArtGlyphs => (Open with { MaxWordArtGlyphs = 1 }, c => Exporter.ToPng(Of(Text("WW", fit: TextFit.Stretch)), 72, c, new MemoryStream())),
                RenderLimitException.DrawTime => (Open with { MaxDrawTime = TimeSpan.FromSeconds(1) }, c => Exporter.ToPng(Of(shape, shape, shape), 72, c, new MemoryStream())),
                "pdf-bytes at a checkpoint" => (Open with { MaxPdfBytes = 2_000 }, c => Exporter.ToPdf(Doc(Of(Pic(0), Pic(1), Pic(2), Pic(3))), c, new MemoryStream())),
                "pdf-bytes at a page's end" => (Open with { MaxPdfBytes = pageEnd - 1 }, c => Exporter.ToPdf(Doc(Of(shape, shape)), c, new MemoryStream())),
                RenderLimitException.PngBytes => (Open with { MaxPngBytes = 1 }, c => Exporter.ToPng(Of(shape), 72, c, new MemoryStream())),
                RenderLimitException.PdfLayerPixels => (Open with { MaxPdfLayerPixels = 1 }, c => Exporter.ToPdf(Doc(Of(new ShapeElement { Bounds = new Box(10, 10, 20, 20), Fill = new Rgba(0, 0, 255, 128), Shadow = new Shadow(new Rgba(0, 0, 0, 128), 2, 2) })), c, new MemoryStream())),
                "font-names in a PDF's typeface pass" => (Open with { MaxFontNames = 1 }, c => Exporter.ToPdf(Doc(Of(Text("a"), Text("b", "Times New Roman"))), c, new MemoryStream())),
                "pages of a bitmap" => (Open with { MaxPages = 1 }, c => { Exporter.RenderBitmap(Of(), 72, c).Dispose(); Exporter.RenderBitmap(Of(), 72, c).Dispose(); }),
                "a layer that is not finite" => (Open, c => Exporter.ToPdf(Doc(Of(new ShapeElement { Bounds = new Box(10, 10, double.NaN, 20), Fill = new Rgba(0, 0, 255, 128), Shadow = new Shadow(new Rgba(0, 0, 0, 128), 2, 2) })), c, new MemoryStream())),
                _ => throw new ArgumentException(row),
            };
            using var ctx = new RenderContext(dir, limits, row == RenderLimitException.DrawTime ? new StepClock() : null);
            var first = Assert.Throws<RenderLimitException>(() => provoke(ctx));
            Assert.Equal(row switch
            {
                _ when row.StartsWith("pdf-bytes") => RenderLimitException.PdfBytes,
                "font-names in a PDF's typeface pass" => RenderLimitException.FontNames,
                "pages of a bitmap" => RenderLimitException.Pages,
                "a layer that is not finite" => RenderLimitException.PdfLayerPixels,
                _ => row,
            }, first.Limit);
            if (row == "font-names in a PDF's typeface pass") Assert.Null(first.Element);   // refused before drawing, not at an element

            // Every later render with the context throws it again, and writes or draws nothing. A caller's canvas first:
            // it counts nothing, so only the refusal itself can stop it.
            using var bmp = new SKBitmap(10, 10);
            bmp.Erase(SKColors.White);
            using (var canvas = new SKCanvas(bmp))
                Assert.Equal(first.Limit, Assert.Throws<RenderLimitException>(() => PageRenderer.Render(canvas, Of(shape), ctx)).Limit);
            Assert.Equal(SKColors.White, bmp.GetPixel(5, 5));
            var pdf = new MemoryStream();
            Assert.Equal(first.Limit, Assert.Throws<RenderLimitException>(() => Exporter.ToPdf(Doc(Of()), ctx, pdf)).Limit);
            Assert.Equal(0, pdf.Length);
            var png = new MemoryStream();
            Assert.Equal(first.Limit, Assert.Throws<RenderLimitException>(() => Exporter.ToPng(Of(), 72, ctx, png)).Limit);
            Assert.Equal(0, png.Length);
            Assert.Equal(first.Limit, Assert.Throws<RenderLimitException>(() => Exporter.RenderBitmap(Of(), 72, ctx)).Limit);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- defaults ----------

    [Fact]
    public void Defaults_ArePinned()
    {
        var d = RenderLimits.Default;
        Assert.Equal(20_000, d.MaxPages);
        Assert.Equal(20_000_000_000, d.MaxPngPixels);
        Assert.Equal(36, d.MaxPdfTypefaces);
        Assert.Equal(4_096, d.MaxFontNames);
        Assert.Equal(8_388_608, d.MaxTextBoxCharacters);
        Assert.Equal(262_144, d.MaxTextBoxRuns);
        Assert.Equal(16_384, d.MaxWordArtGlyphs);
        Assert.Equal(TimeSpan.FromSeconds(600), d.MaxDrawTime);
        Assert.Equal(2L << 30, d.MaxPdfBytes);
        Assert.Equal(16L << 30, d.MaxPngBytes);
        Assert.Equal(64_000_000, d.MaxPdfPicturePixels);
        Assert.Equal(64_000_000, d.MaxPdfLayerPixels);
        Assert.Equal(256L << 20, d.MaxPictureFileBytes);
        Assert.Same(d, new RenderContext().Limits);
    }
}
