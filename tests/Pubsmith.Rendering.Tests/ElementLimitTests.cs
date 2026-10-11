using Pubsmith.Core;
using SkiaSharp;

namespace Pubsmith.Rendering.Tests;

// WI-006 AC2: a text element over a per-element limit (characters, runs, WordArt glyphs) or a document naming too many
// fonts is refused before that element is laid out or built, wherever the page is drawn: the exporters and a caller's
// own canvas.
public sealed class ElementLimitTests
{
    private static readonly RenderLimits Open = RenderLimits.Default with
    {
        MaxPages = int.MaxValue, MaxPngPixels = long.MaxValue, MaxPdfTypefaces = int.MaxValue, MaxFontNames = int.MaxValue,
        MaxTextBoxCharacters = int.MaxValue, MaxTextBoxRuns = int.MaxValue, MaxWordArtGlyphs = int.MaxValue,
        MaxDrawTime = TimeSpan.MaxValue, MaxPdfBytes = long.MaxValue, MaxPngBytes = long.MaxValue,
    };

    private static TextElement Text(params TextRun[] runs) => new() { Bounds = new Box(0, 0, 100, 50), Paragraphs = [new Paragraph { Runs = runs }] };

    private static TextElement WordArt(params TextRun[] runs) => Text(runs) with { Fit = TextFit.Stretch };

    private static TextElement Paragraphs(params TextRun[][] paragraphs) => new() { Bounds = new Box(0, 0, 100, 50), Paragraphs = paragraphs.Select(r => new Paragraph { Runs = r }).ToList() };

    private static TextRun Run(string text, string family = "Arial") => new(text, new TextStyle { Family = family });

    // Element 1 is ordinary (it is laid out); element 2 is over the row's limit; element 3 is never reached.
    private static (RenderLimits Limits, Element Over, long Value) Row(string row) => row switch
    {
        "characters" => (Open with { MaxTextBoxCharacters = 10 }, Text(Run("abcdef"), Run("ghijk")), 11),
        "characters, WordArt" => (Open with { MaxTextBoxCharacters = 10 }, WordArt(Run("abcdef"), Run("ghijk")), 11),
        "runs" => (Open with { MaxTextBoxRuns = 3 }, Text(Run("a"), Run("b"), Run("c"), Run("d")), 4),
        "characters across paragraphs" => (Open with { MaxTextBoxCharacters = 10 }, Paragraphs([Run("abcdef")], [Run("ghijk")]), 11),   // each paragraph under the limit
        "runs across paragraphs" => (Open with { MaxTextBoxRuns = 3 }, Paragraphs([Run("a"), Run("b")], [Run("c"), Run("d")]), 4),
        "runs, WordArt" => (Open with { MaxTextBoxRuns = 3 }, WordArt(Run("a"), Run("b"), Run("c"), Run("d")), 4),
        // A paragraph without runs counts as one run (round 8: millions of them were one unbounded step).
        "empty paragraphs" => (Open with { MaxTextBoxRuns = 3 }, Paragraphs([], [], [], []), 4),
        "empty paragraphs, WordArt" => (Open with { MaxTextBoxRuns = 3 }, Paragraphs([], [], [], []) with { Fit = TextFit.Stretch }, 4),
        "runs and empty paragraphs" => (Open with { MaxTextBoxRuns = 3 }, Paragraphs([Run("a")], [], [Run("b"), Run("c")]), 4),
        "WordArt glyphs" => (Open with { MaxWordArtGlyphs = 4 }, WordArt(Run("ab"), Run("c\U0001F600d")), 5),   // a surrogate pair is one glyph
        "font names" => (Open with { MaxFontNames = 3 }, WordArt(Run("a", "Arial"), Run("b", "Font B"), Run("c", "Font C"), Run("d", "Font D")), 4),
        _ => throw new ArgumentException(row),
    };

    private static readonly string[] Kinds = ["characters", "characters, WordArt", "characters across paragraphs", "runs", "runs, WordArt", "runs across paragraphs", "empty paragraphs", "empty paragraphs, WordArt", "runs and empty paragraphs", "WordArt glyphs", "font names"];

    public static TheoryData<string, string> Rows()
    {
        var data = new TheoryData<string, string>();
        foreach (var row in Kinds)
            foreach (var path in new[] { "ToPng", "ToPdf", "caller's canvas" })
                data.Add(row, path);
        return data;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void OverEachElementLimit_IsRefusedBeforeTheElement(string row, string path)
    {
        var (limits, over, value) = Row(row);
        var page = new Page(200, 200) { Elements = [Text(Run("first")), over, Text(Run("never"))] };
        using var ctx = new RenderContext(null, limits);
        var ex = Assert.Throws<RenderLimitException>(() =>
        {
            switch (path)
            {
                case "ToPng": Exporter.ToPng(page, 72, ctx, new MemoryStream()); break;
                case "ToPdf": Exporter.ToPdf(new PubsmithDocument { Pages = [page] }, ctx, new MemoryStream()); break;
                default:
                    using (var bmp = new SKBitmap(200, 200))
                    using (var canvas = new SKCanvas(bmp))
                        PageRenderer.Render(canvas, page, ctx);
                    break;
            }
        });
        Assert.Equal(row switch { "characters" or "characters, WordArt" or "characters across paragraphs" => RenderLimitException.TextBoxCharacters, "runs" or "runs, WordArt" or "runs across paragraphs" or "empty paragraphs" or "empty paragraphs, WordArt" or "runs and empty paragraphs" => RenderLimitException.TextBoxRuns, "WordArt glyphs" => RenderLimitException.WordArtGlyphs, _ => RenderLimitException.FontNames }, ex.Limit);
        Assert.Equal(value, ex.Value);
        Assert.Equal(2, ex.Element);
        Assert.Equal(path == "ToPdf" ? 1 : null, ex.Page);
        // Only the first element was laid out; the refused one was neither laid out nor built.
        Assert.Equal(1, ctx.RunsLaidOut);
        Assert.Equal(0, ctx.GlyphBuilds);
    }

    [Fact]
    public void AtEachElementLimit_IsDrawn()
    {
        foreach (var row in Kinds)
        {
            var (limits, over, value) = Row(row);
            var at = row switch
            {
                "characters" or "characters, WordArt" or "characters across paragraphs" => limits with { MaxTextBoxCharacters = (int)value },
                "runs" or "runs, WordArt" or "runs across paragraphs" or "empty paragraphs" or "empty paragraphs, WordArt" or "runs and empty paragraphs" => limits with { MaxTextBoxRuns = (int)value },
                "WordArt glyphs" => limits with { MaxWordArtGlyphs = (int)value },
                _ => limits with { MaxFontNames = (int)value },
            };
            using var ctx = new RenderContext(null, at);
            using var _ = Exporter.RenderBitmap(new Page(200, 200) { Elements = [Text(Run("first")), over] }, 72, ctx);
        }
    }

    [Fact]
    public void ManyEmptyParagraphs_AreRefusedAsTheyAreCounted()
    {
        // The runs are refused as the paragraphs are counted, not after a walk over all of them: a box of 1,000 empty
        // paragraphs over a limit of 10 is refused at the 11th, and no later walk (names, glyphs, a WordArt's runs) begins.
        foreach (var fit in new[] { TextFit.None, TextFit.Stretch })
        {
            var box = new TextElement { Bounds = new Box(0, 0, 100, 50), Fit = fit, Paragraphs = [.. Enumerable.Range(0, 1_000).Select(_ => new Paragraph())] };
            using var ctx = new RenderContext(null, Open with { MaxTextBoxRuns = 10 });
            var ex = Assert.Throws<RenderLimitException>(() => Exporter.RenderBitmap(new Page(100, 100) { Elements = [box] }, 72, ctx));
            Assert.Equal(RenderLimitException.TextBoxRuns, ex.Limit);
            Assert.Equal(11, ex.Value);
            Assert.Equal(11, ctx.ParagraphsChecked);
            Assert.Equal(0, ctx.GlyphBuilds);
        }
    }

    [Fact]
    public void ManyOneRunParagraphs_AreRefusedAsTheyAreCounted()
    {
        // The same for paragraphs that do have a run: the runs are refused as each paragraph is counted, whatever it holds.
        foreach (var fit in new[] { TextFit.None, TextFit.Stretch })
        {
            var box = new TextElement { Bounds = new Box(0, 0, 100, 50), Fit = fit, Paragraphs = [.. Enumerable.Range(0, 1_000).Select(_ => new Paragraph { Runs = [Run("")] })] };
            using var ctx = new RenderContext(null, Open with { MaxTextBoxRuns = 10 });
            var ex = Assert.Throws<RenderLimitException>(() => Exporter.RenderBitmap(new Page(100, 100) { Elements = [box] }, 72, ctx));
            Assert.Equal(RenderLimitException.TextBoxRuns, ex.Limit);
            Assert.Equal(11, ex.Value);
            Assert.Equal(11, ctx.ParagraphsChecked);
            Assert.Equal(0, ctx.GlyphBuilds);
        }
    }

    [Fact]
    public void FontNames_AreCountedAcrossTheContext()
    {
        // Three names on one page and a fourth on the next, in one context: the fourth is refused.
        using var ctx = new RenderContext(null, Open with { MaxFontNames = 3 });
        using (Exporter.RenderBitmap(new Page(100, 100) { Elements = [Text(Run("a", "Arial"), Run("b", "Font B"), Run("c", "font b"), Run("d", "Font C"))] }, 72, ctx)) { }
        var ex = Assert.Throws<RenderLimitException>(() => Exporter.RenderBitmap(new Page(100, 100) { Elements = [Text(Run("e", "Font D"))] }, 72, ctx));
        Assert.Equal(RenderLimitException.FontNames, ex.Limit);
        Assert.Equal(4, ex.Value);
    }

    [Fact]
    public void AnEmptyParagraphsDefaultFont_IsCountedBeforeTheElement()
    {
        // A paragraph without runs is laid out in the default style's font, so its name counts at the element check.
        var empty = new TextElement { Bounds = new Box(0, 0, 100, 50), Paragraphs = [new Paragraph()] };
        using var ctx = new RenderContext(null, Open with { MaxFontNames = 1 });
        var ex = Assert.Throws<RenderLimitException>(() => Exporter.RenderBitmap(new Page(100, 100) { Elements = [Text(Run("a", "Arial")), empty] }, 72, ctx));
        Assert.Equal(RenderLimitException.FontNames, ex.Limit);
        Assert.Equal(2, ex.Element);
    }

    [Fact]
    public void ALongFontName_IsCutTo256()
    {
        // Two names that differ only after 256 characters are one name, and resolve once.
        var stem = new string('q', 256);
        var fonts = new FontResolver();
        using var ctx = new RenderContext(null, Open with { MaxFontNames = 1 }, fonts: fonts);
        using (Exporter.RenderBitmap(new Page(100, 100) { Elements = [Text(Run("a", stem + "one"), Run("b", stem + "two"))] }, 72, ctx)) { }
        Assert.Equal(1, fonts.Resolutions);
        Assert.Contains(ctx.Warnings, w => w.Code == RenderWarning.FontSubstituted && w.Message.Contains(stem + "'") && !w.Message.Contains("one"));
    }
}
