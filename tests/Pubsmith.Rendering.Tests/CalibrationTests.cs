using System.Diagnostics;
using Pubsmith.Core;
using Pubsmith.PubReader;
using SkiaSharp;
using Xunit.Abstractions;

namespace Pubsmith.Rendering.Tests;

/// <summary>Skips unless PUBSMITH_CALIBRATE is set: wall-clock measurements, minutes long, for a quiet machine.</summary>
public sealed class CalibrateFactAttribute : FactAttribute
{
    public CalibrateFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PUBSMITH_CALIBRATE")))
            Skip = "Set PUBSMITH_CALIBRATE=1 to run the calibration suite (minutes long; on a quiet machine).";
    }
}

public sealed class CalibrateTheoryAttribute : TheoryAttribute
{
    public CalibrateTheoryAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PUBSMITH_CALIBRATE")))
            Skip = "Set PUBSMITH_CALIBRATE=1 to run the calibration suite (minutes long; on a quiet machine).";
    }
}

[CollectionDefinition(nameof(CalibrationTests), DisableParallelization = true)]
public sealed class CalibrationCollection;

// WI-006 Task 7, opt-in: drawing times of ordinary documents (AC5), every hostile class stopped by the meter or refused
// by a count (AC3, AC6, with the longest step of each kind), the PDF page's end recorded for WI-007, and a passed-through
// 225 MP JPEG embedded without a decode (AC4). Runs alone; wall-clock, so its numbers are the machine's.
[Collection(nameof(CalibrationTests))]
public sealed class CalibrationTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pubsmith-calibration-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly string Shared = Path.Combine(Path.GetTempPath(), "pubsmith-calibration-pictures");   // generated pictures, kept between runs

    private void Say(string s) => output.WriteLine(s);

    /// <summary>Counts what is written and keeps none of it.</summary>
    private sealed class Sink : Stream
    {
        public long Count { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Count;
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Count += count;
    }

    /// <summary>A command as the CLI runs one, without encoding PNG pages: the PDF (if asked), then each page's bitmap.</summary>
    private static (long PdfBytes, TimeSpan Wall) Command(PubsmithDocument doc, RenderContext ctx, bool pdf, double? dpi)
    {
        var sw = Stopwatch.StartNew();
        var sink = new Sink();
        if (pdf) Exporter.ToPdf(doc, ctx, sink);
        if (dpi is { } d) foreach (var p in doc.Pages) using (Exporter.RenderBitmap(p, d, ctx)) { }
        return (sink.Count, sw.Elapsed);
    }

    // ---------- AC5: ordinary drawing times ----------

    [CalibrateFact]
    public void OrdinaryDrawingTimes_AreAtMostATenthOfMaxDrawTime()
    {
        Directory.CreateDirectory(Shared);
        OrdinaryPublications.WritePhotos(Shared);
        var limit = RenderLimits.Default.MaxDrawTime;
        foreach (var (name, build) in OrdinaryPublications.All())
        {
            using var ctx = new RenderContext(Shared);
            var (bytes, wall) = Command(build(), ctx, pdf: true, dpi: 300);
            Say($"{name}: drawing {ctx.DrawTime.TotalSeconds:F2} s (PDF and PNG at 300 dpi, one context), longest step {ctx.LongestStep.TotalMilliseconds:F0} ms, wall {wall.TotalSeconds:F1} s, PDF {bytes / 1048576.0:F1} MiB");
            Assert.True(ctx.DrawTime <= limit / 10, $"{name}: {ctx.DrawTime}");
        }
    }

    [CalibrateFact]
    public void RealFiles_DrawingTimes_AreAtMostATenthOfMaxDrawTime()
    {
        // The maintainers' harvest, read only: each file imported into a temporary folder, never written back.
        var corpus = Environment.GetEnvironmentVariable("PUBSMITH_CORPUS");
        if (string.IsNullOrEmpty(corpus)) { Say("PUBSMITH_CORPUS is not set: no real files measured."); return; }
        var sources = File.ReadAllLines(Path.Combine(corpus, "manifest.csv")).Skip(1).Select(l => l.Split(',')[0].Trim('"'))
            .Where(s => s.EndsWith(".pub", StringComparison.OrdinalIgnoreCase) && File.Exists(s)).Distinct().ToList();
        var times = new List<double>();
        var i = 0;
        foreach (var src in sources)
        {
            PubImportResult r;
            try { r = PubImporter.Import(src); } catch (PubFormatException) { continue; }
            var dir = Path.Combine(_dir, (i++).ToString());
            PubImporter.WriteTo(r, dir, "doc");
            using var ctx = new RenderContext(dir);
            Command(r.Document, ctx, pdf: true, dpi: 300);
            times.Add(ctx.DrawTime.TotalSeconds);
            Directory.Delete(dir, true);
        }
        Say($"real files: {times.Count}; drawing (PDF and PNG at 300 dpi, one context): slowest {times.Max():F2} s, five slowest {string.Join(", ", times.OrderDescending().Take(5).Select(t => t.ToString("F2")))} s");
        Assert.All(times, t => Assert.True(t <= RenderLimits.Default.MaxDrawTime.TotalSeconds / 10));
    }

    // ---------- AC3, AC6: every hostile class stopped or refused ----------

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);   // injected: the bound is MaxDrawTime plus the longest step

    private static readonly RenderLimits Calibration = RenderLimits.Default with { MaxDrawTime = Limit };

    private static string Picture(string name, int w, int h, SKEncodedImageFormat format)
    {
        Directory.CreateDirectory(Shared);
        var path = Path.Combine(Shared, name);
        if (File.Exists(path)) return name;
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp))
        {
            using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(w, h), [new SKColor(200, 80, 40), new SKColor(20, 160, 220)], SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader };
            canvas.DrawRect(0, 0, w, h, paint);
            var rng = new Random(w + h);
            using var dot = new SKPaint { IsAntialias = true };
            for (var i = 0; i < w * h / 2_000; i++) { dot.Color = new SKColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256)); canvas.DrawCircle(rng.Next(w), rng.Next(h), 2 + rng.Next(8), dot); }
        }
        using var data = bmp.Encode(format, 85);
        File.WriteAllBytes(path, data.ToArray());
        return name;
    }

    private static Page Letter(IEnumerable<Element> elements) => new(612, 792) { Elements = elements.ToList() };

    private static TextElement WordArt(int glyphs, double outline) => new()
    {
        Bounds = new Box(36, 36, 540, 200), Fit = TextFit.Stretch, Outline = new Stroke(new Rgba(200, 0, 0), outline),
        Paragraphs = [new Paragraph { Runs = [new TextRun(new string('W', glyphs), new TextStyle { Family = "Arial" })] }],
    };

    public static TheoryData<string> Hostile() =>
    [
        "shadowed 1 x 1 pt shapes with 1,000 pt translucent lines (PNG 300 dpi)",
        "a 1 MP picture page-sized, many times (PNG 300 dpi)",
        "two 56 MP pictures larger than the cache together, alternately (PNG 150 dpi)",
        "one line of 200,000 stacked U+0336 at 13,000 pt, each over the whole page (PNG 300 dpi)",
        "WordArt of 16,384 glyphs with a 1,000 pt outline (PNG 300 dpi)",
        "page-sized translucent shapes with layered shadows: heavy overdraw (PNG 300 dpi)",
        "distinct crops of a 4 MP picture (PDF)",
        "page-sized translucent shapes with layered shadows (PDF)",
        "20,000 pages of WordArt (PDF)",
    ];

    [CalibrateTheory]
    [MemberData(nameof(Hostile))]
    public void EachHostileClass_IsStoppedWithinItsBound(string name)
    {
        var half = new Rgba(200, 0, 0, 128);
        var shadow = new Shadow(new Rgba(0, 0, 0, 128), 4, 4);
        // Exact names: a row builds its own shape or throws (rev 16's prefixes let two rows build another row's shape).
        var (doc, pdf, dpi) = name switch
        {
            "shadowed 1 x 1 pt shapes with 1,000 pt translucent lines (PNG 300 dpi)" => (Doc(Letter(Enumerable.Range(0, 2_000).Select(_ => (Element)new ShapeElement { Bounds = new Box(300, 400, 1, 1), Stroke = new Stroke(half, 1000), Shadow = shadow }))), false, 300.0),
            "a 1 MP picture page-sized, many times (PNG 300 dpi)" => (Doc(Letter(Enumerable.Range(0, 2_000).Select(_ => (Element)new ImageElement { Bounds = new Box(0, 0, 612, 792), Source = Picture("one-mp.png", 1000, 1000, SKEncodedImageFormat.Png) }))), false, 300.0),
            "two 56 MP pictures larger than the cache together, alternately (PNG 150 dpi)" => (Doc(Letter(Enumerable.Range(0, 400).Select(i => (Element)new ImageElement { Bounds = new Box(0, 0, 612, 792), Source = Picture(i % 2 == 0 ? "a56.jpg" : "b56.jpg", 7500, 7500, SKEncodedImageFormat.Jpeg) }))), false, 150.0),
            "one line of 200,000 stacked U+0336 at 13,000 pt, each over the whole page (PNG 300 dpi)" => (Doc(Letter([StackedOverThePage('̶', 200_000, 13_000)])), false, 300.0),
            "WordArt of 16,384 glyphs with a 1,000 pt outline (PNG 300 dpi)" => (Doc(Letter(Enumerable.Range(0, 20).Select(_ => (Element)WordArt(16_384, 1000)))), false, 300.0),
            "page-sized translucent shapes with layered shadows: heavy overdraw (PNG 300 dpi)" => (Doc(Letter(Enumerable.Range(0, 2_000).Select(_ => (Element)new ShapeElement { Bounds = new Box(0, 0, 612, 792), Fill = half, Shadow = shadow }))), false, 300.0),
            "distinct crops of a 4 MP picture (PDF)" => (new PubsmithDocument { Pages = Enumerable.Range(0, 20).Select(p => Letter(Enumerable.Range(0, 200).Select(i => (Element)new ImageElement { Bounds = new Box(i % 20 * 30, i / 20 * 30, 30, 30), Source = Picture("four-mp.png", 2000, 2000, SKEncodedImageFormat.Png), Crop = new Crop((p * 200 + i) * 0.0002, 0, 0.01, 0) }))).ToList() }, true, (double?)null),
            "page-sized translucent shapes with layered shadows (PDF)" => (new PubsmithDocument { Pages = Enumerable.Range(0, 200).Select(_ => Letter(Enumerable.Range(0, 100).Select(_ => (Element)new ShapeElement { Bounds = new Box(0, 0, 612, 792), Fill = half, Shadow = shadow }))).ToList() }, true, null),
            "20,000 pages of WordArt (PDF)" => (new PubsmithDocument { Pages = Enumerable.Range(0, 20_000).Select(_ => Letter([WordArt(200, 3)])).ToList() }, true, null),
            _ => throw new ArgumentException(name),
        };
        Directory.CreateDirectory(Shared);
        using var ctx = new RenderContext(Shared, Calibration);
        var before = Process.GetCurrentProcess().PeakWorkingSet64;
        var sw = Stopwatch.StartNew();
        var ex = Assert.Throws<RenderLimitException>(() => Command(doc, ctx, pdf, dpi));
        var wall = sw.Elapsed;
        Process.GetCurrentProcess().Refresh();
        var peak = Process.GetCurrentProcess().PeakWorkingSet64;
        Say($"{name}: stopped by {ex.Limit} ({ex.Value:N0}; page {ex.Page}, element {ex.Element}); drawing {ctx.DrawTime.TotalSeconds:F1} s, " +
            $"longest step {ctx.LongestStep.TotalMilliseconds:F0} ms, wall {wall.TotalSeconds:F1} s, peak working set {peak / 1048576} MiB (before {before / 1048576} MiB)");
        Assert.Equal(RenderLimitException.DrawTime, ex.Limit);
        Assert.True(ctx.DrawTime <= Limit + ctx.LongestStep, $"{ctx.DrawTime} past {Limit} + {ctx.LongestStep}");
    }

    private static PubsmithDocument Doc(params Page[] pages) => new() { Pages = pages };

    public static TheoryData<string> TextSteps() =>
    [
        "one line of 4,194,304 words in a very wide box (PNG 72 dpi)",
        "one run of 8,388,608 characters of short words in a 5 pt column (PNG 72 dpi)",
        "one word of 8,388,608 characters (PNG 72 dpi)",
        "one word of 8,388,608 characters cycling 60,000 SimSun-ExtB ideographs, 4 pt (PNG 72 dpi)",
        "one word of 8,388,608 characters cycling 60,000 SimSun-ExtB ideographs, 250 pt (PNG 72 dpi)",
        "one word of 8,388,608 characters, the last an unpaired surrogate (PNG 72 dpi)",
        "a text box of 8,388,608 characters in 2,048 paragraphs, 4 pt type (PNG 300 dpi)",
        "a text box of 8,175 words of 1,025 letters, each measured in two chunks (PNG 72 dpi)",
        "a text box of 262,144 empty paragraphs (PNG 72 dpi)",
        "a WordArt of 262,143 empty paragraphs and one run (PNG 72 dpi)",
        "WordArt of 16,384 complex glyphs in a wave (PNG 72 dpi)",
        "WordArt of 16,384 distinct Microsoft YaHei ideographs in a wave (PNG 72 dpi)",
        "WordArt of 16,384 distinct Microsoft YaHei ideographs at 250 pt in a wave (PNG 72 dpi)",
    ];

    [CalibrateTheory]
    [MemberData(nameof(TextSteps))]
    public void TextAndWordArtSteps_AtTheElementLimits_AreShort(string name)
    {
        // Text and WordArt at their element limits, under the default limits, drawn to the end: the meter sees every
        // step of the kind, so the longest one is that kind's (a run stopped part-way would not have reached them all).
        // Distinct glyphs cost time each, more at larger sizes, so the rows include words and WordArt of distinct glyphs.
        // The 2 s bound catches a step of seconds; a step a few times slower than measured passes it.
        Page Small(TextElement e) => new(400, 400) { Elements = [e] };
        TextElement Box(Box at, params Paragraph[] paragraphs) => new() { Bounds = at, Paragraphs = paragraphs };
        Paragraph One(string text, double size, string family = "Arial") => new() { Runs = [new TextRun(text, new TextStyle { Family = family, Size = size })] };
        TextElement Wave(string text, string family, double? size = null) => new()
        {
            Bounds = new Box(36, 36, 540, 200), Fit = TextFit.Stretch, Warp = new TextWarp(WarpKind.Wave1),
            Paragraphs = [new Paragraph { Runs = [new TextRun(text, size is { } s ? new TextStyle { Family = family, Size = s } : new TextStyle { Family = family })] }],
        };
        var words = string.Concat(Enumerable.Repeat("a ", 4_194_304));
        var defaultSize = new TextStyle().Size;
        // Exact names. Each row states what its name says it builds (the family and size of every run, the UTF-16 units
        // and the distinct characters), checked against the element it built, and a counter of that shape checked after
        // it is drawn.
        var (page, dpi, made, drawn) = name switch
        {
            "one line of 4,194,304 words in a very wide box (PNG 72 dpi)" =>
                (Small(Box(new Box(10, 10, 1e9, 380), One(words, 4))), 72.0, ("Arial", 4.0, 8_388_608, 2), (Func<RenderContext, bool>)(c => c.LinesDrawn == 1 && c.WordsWrapped == 4_194_304)),
            "one run of 8,388,608 characters of short words in a 5 pt column (PNG 72 dpi)" =>
                (Small(Box(new Box(10, 10, 5, 380), One(words, 4))), 72.0, ("Arial", 4.0, 8_388_608, 2), c => c.RunsLaidOut == 1 && c.LinesLaidOut == 4_194_304),
            "one word of 8,388,608 characters (PNG 72 dpi)" =>
                (Small(Box(new Box(10, 10, 380, 380), One(new string('a', 8_388_608), 4))), 72.0, ("Arial", 4.0, 8_388_608, 1), c => c.LinesDrawn == 1 && c.PieceChunksMeasured == 8_192 && c.GlyphAdvancesMeasured == 2),
            "one word of 8,388,608 characters cycling 60,000 SimSun-ExtB ideographs, 4 pt (PNG 72 dpi)" =>
                (Small(Box(new Box(10, 10, 380, 380), One(Cycle("SimSun-ExtB", 0x20000, 60_000, 8_388_608), 4, "SimSun-ExtB"))), 72.0, ("SimSun-ExtB", 4.0, 8_388_608, 60_000), c => c.LinesDrawn == 1 && c.PieceChunksMeasured == 8_192 && c.GlyphAdvancesMeasured == 120_000),
            "one word of 8,388,608 characters cycling 60,000 SimSun-ExtB ideographs, 250 pt (PNG 72 dpi)" =>
                (Small(Box(new Box(10, 10, 380, 380), One(Cycle("SimSun-ExtB", 0x20000, 60_000, 8_388_608), 250, "SimSun-ExtB"))), 72.0, ("SimSun-ExtB", 250.0, 8_388_608, 60_000), c => c.LinesDrawn == 1 && c.PieceChunksMeasured == 8_192 && c.GlyphAdvancesMeasured == 120_000),
            // Not valid UTF-16: its chunks are measured up to the invalid one, then nothing is drawn, and no call scans the whole word.
            "one word of 8,388,608 characters, the last an unpaired surrogate (PNG 72 dpi)" =>
                (Small(Box(new Box(10, 10, 380, 380), One(new string('a', 8_388_607) + '\uD835', 4))), 72.0, ("Arial", 4.0, 8_388_608, 2), c => c.PieceChunksMeasured == 8_192 && c.GlyphlessPiecesSkipped == 1 && c.GlyphRunsDrawn == 0),
            "a text box of 8,388,608 characters in 2,048 paragraphs, 4 pt type (PNG 300 dpi)" =>
                (Letter([Box(new Box(36, 36, 540, 720), [.. Enumerable.Range(0, 2_048).Select(_ => One(string.Concat(Enumerable.Repeat("word ", 819)) + "e", 4))])]), 300.0, ("Arial", 4.0, 8_388_608, 6), c => c.ParagraphsLaidOut == 2_048),
            "a text box of 8,175 words of 1,025 letters, each measured in two chunks (PNG 72 dpi)" =>
                (Small(Box(new Box(10, 10, 380, 380), One(string.Concat(Enumerable.Repeat(new string('a', 1_025) + " ", 8_175)), 4))), 72.0, ("Arial", 4.0, 8_175 * 1_026, 2), c => c.PieceChunksMeasured == 16_350 && c.GlyphChunksPositioned == 16_350),
            // Paragraphs without runs, at the runs limit: the element's check walks them all (round 8), then each is laid out.
            "a text box of 262,144 empty paragraphs (PNG 72 dpi)" =>
                (Small(new TextElement { Bounds = new Box(10, 10, 380, 380), Paragraphs = [.. Enumerable.Range(0, 262_144).Select(_ => new Paragraph())] }), 72.0, (new TextStyle().Family, new TextStyle().Size, 0, 0), c => c.ParagraphsChecked == 262_144 && c.ParagraphsLaidOut == 262_144),
            "a WordArt of 262,143 empty paragraphs and one run (PNG 72 dpi)" =>
                (Letter([new TextElement { Bounds = new Box(36, 36, 540, 200), Fit = TextFit.Stretch, Paragraphs = [.. Enumerable.Range(0, 262_143).Select(_ => new Paragraph()), new Paragraph { Runs = [new TextRun("W", new TextStyle { Family = "Arial" })] }] }]), 72.0, ("Arial", new TextStyle().Size, 1, 1), c => c.ParagraphsChecked == 262_144 && c.WordArtRunsBuilt == 1),
            "WordArt of 16,384 complex glyphs in a wave (PNG 72 dpi)" =>
                (Letter([Wave(new string('\u9F98', 16_384), "Microsoft YaHei")]), 72.0, ("Microsoft YaHei", defaultSize, 16_384, 1), c => c.WordArtGlyphsPlaced == 16_384),
            "WordArt of 16,384 distinct Microsoft YaHei ideographs in a wave (PNG 72 dpi)" =>
                (Letter([Wave(Cycle("Microsoft YaHei", 0x4E00, 16_384, 16_384), "Microsoft YaHei")]), 72.0, ("Microsoft YaHei", defaultSize, 16_384, 16_384), c => c.WordArtGlyphsPlaced == 16_384 && c.WordArtChunksMeasured == 16),
            "WordArt of 16,384 distinct Microsoft YaHei ideographs at 250 pt in a wave (PNG 72 dpi)" =>
                (Letter([Wave(Cycle("Microsoft YaHei", 0x4E00, 16_384, 16_384), "Microsoft YaHei", 250)]), 72.0, ("Microsoft YaHei", 250.0, 16_384, 16_384), c => c.WordArtGlyphsPlaced == 16_384 && c.WordArtChunksMeasured == 16),
            _ => throw new ArgumentException(name),
        };
        var runs = page.Elements.OfType<TextElement>().SelectMany(e => e.Paragraphs).SelectMany(p => p.Runs).ToList();
        Assert.All(runs, r => Assert.Equal((made.Item1, made.Item2), (r.Style.Family, r.Style.Size)));
        Assert.Equal(made.Item3, runs.Sum(r => r.Text.Length));
        Assert.Equal(made.Item4, runs.SelectMany(r => CodePoints(r.Text)).Distinct().Count());
        using var ctx = new RenderContext(null, RenderLimits.Default);
        var sw = Stopwatch.StartNew();
        using (Exporter.RenderBitmap(page, dpi, ctx)) { }
        Say($"{name}: completed: drawing {ctx.DrawTime.TotalSeconds:F1} s, longest step {ctx.LongestStep.TotalMilliseconds:F0} ms, wall {sw.Elapsed.TotalSeconds:F1} s (this build's test host)");
        Assert.True(drawn(ctx), $"{name}: not drawn as its shape");
        Assert.DoesNotContain(ctx.Warnings, w => w.Code == RenderWarning.FontSubstituted);
        Assert.True(ctx.LongestStep < TimeSpan.FromSeconds(2), $"{name}: {ctx.LongestStep}");
    }

    private static IEnumerable<int> CodePoints(string text)
    {
        for (var i = 0; i < text.Length; i++)
            if (char.IsSurrogatePair(text, i)) yield return char.ConvertToUtf32(text[i], text[++i]);
            else yield return text[i];
    }

    /// <summary>A string of <paramref name="length"/> UTF-16 units cycling through <paramref name="distinct"/> code points the font maps, from <paramref name="first"/>.</summary>
    private static string Cycle(string family, int first, int distinct, int length)
    {
        using var typeface = SKTypeface.FromFamilyName(family);
        using var font = new SKFont(typeface);
        var points = new List<int>();
        for (var cp = first; points.Count < distinct && cp < 0x110000; cp++)
            if (cp is < 0xD800 or > 0xDFFF && font.GetGlyph(cp) != 0) points.Add(cp);
        Assert.Equal(distinct, points.Count);
        var text = new System.Text.StringBuilder(length);
        for (var i = 0; ; i++)
        {
            var s = char.ConvertFromUtf32(points[i % points.Count]);
            if (text.Length + s.Length > length) break;
            text.Append(s);
        }
        return text.ToString();
    }

    /// <summary>
    /// A Letter page's text box whose one line is <paramref name="count"/> copies of a zero-advance mark, each copy's ink
    /// over the whole page: the inset and the exact line spacing put the ink box on the page (Task 0 round 2's).
    /// </summary>
    private static TextElement StackedOverThePage(char mark, int count, double size)
    {
        using var typeface = SKTypeface.FromFamilyName("Arial");
        using var font = new SKFont(typeface, (float)size);
        using var path = font.GetGlyphPath(font.GetGlyphs(mark.ToString())[0]);
        var ink = path.Bounds;   // relative to the pen at the baseline
        var left = -ink.Left - (ink.Width - 612) / 2;
        var baseline = -ink.Top - (ink.Height - 792) / 2;
        return new TextElement
        {
            Bounds = new Box(0, 0, 612, 792), Insets = new Insets(left, 0, 0, 0),
            Paragraphs = [new Paragraph { ExactLineSpacing = baseline + font.Metrics.Descent, SpaceAfter = 0, Runs = [new TextRun(new string(mark, count), new TextStyle { Family = "Arial", Size = size })] }],
        };
    }

    [CalibrateFact]
    public void CountedClasses_AreRefusedAtOnce()
    {
        // What a count refuses before anything is drawn, whatever the drawing would cost.
        var families = SKFontManager.Default.FontFamilies.Distinct().Take(40).ToList();
        var typefaces = Doc(Letter([new TextElement { Bounds = new Box(0, 0, 612, 792), Paragraphs = [new Paragraph { Runs = families.Select(f => new TextRun("a", new TextStyle { Family = f })).ToList() }] }]));
        var names = Doc(Letter([new TextElement { Bounds = new Box(0, 0, 612, 792), Paragraphs = [new Paragraph { Runs = Enumerable.Range(0, 5_000).Select(i => new TextRun("a", new TextStyle { Family = $"No Such Font {i}" })).ToList() }] }]));
        foreach (var (name, doc, outputs, limit) in new[]
        {
            ("20,001 pages to PDF", new PubsmithDocument { Pages = Enumerable.Range(0, 20_001).Select(_ => new Page(72, 72)).ToList() }, new RenderOutputs(true, null), RenderLimitException.Pages),
            ("41 Letter pages at 2,300 dpi (20.5 GP)", new PubsmithDocument { Pages = Enumerable.Range(0, 41).Select(_ => new Page(612, 792)).ToList() }, new RenderOutputs(false, 2300), RenderLimitException.PngPixels),
            ($"{families.Count} installed families in one PDF", typefaces, new RenderOutputs(true, null), RenderLimitException.PdfTypefaces),
            ("5,000 font names", names, new RenderOutputs(true, null), RenderLimitException.FontNames),
        })
        {
            using var ctx = new RenderContext(null);
            var sw = Stopwatch.StartNew();
            var ex = Assert.Throws<RenderLimitException>(() => RenderLimits.Check(doc, outputs, ctx));
            Say($"{name}: refused by {ex.Limit} ({ex.Value:N0}) in {sw.Elapsed.TotalMilliseconds:F0} ms");
            Assert.Equal(limit, ex.Limit);
        }
    }

    // ---------- the PDF page's end, recorded for WI-007 ----------

    [CalibrateFact]
    public void ThePdfPageEnd_IsRecorded()
    {
        // Pages of WordArt each end within the machine; per second of drawing: bytes written, time outside drawing.
        foreach (var glyphs in new[] { 200, 1_000 })
        {
            var doc = new PubsmithDocument { Pages = Enumerable.Range(0, 400).Select(_ => Letter([WordArt(glyphs, 3)])).ToList() };
            using var ctx = new RenderContext(null, Calibration);
            var sw = Stopwatch.StartNew();
            var sink = new Sink();
            try { Exporter.ToPdf(doc, ctx, sink); } catch (RenderLimitException) { }
            var wall = sw.Elapsed;
            var draw = ctx.DrawTime.TotalSeconds;
            Say($"PDF pages of WordArt of {glyphs} glyphs: drawing {draw:F1} s, wall {wall.TotalSeconds:F1} s; per second of drawing {sink.Count / 1048576.0 / draw:F1} MiB written and {(wall.TotalSeconds - draw) / draw:F2} s outside drawing (page ends)");
        }
    }

    [CalibrateFact]
    public void ThePdfClose_IsRecorded()
    {
        // The close writes each typeface's subset: its time grows with the distinct glyphs drawn from it, which nothing
        // counts. One page of distinct ideographs, one a piece; time outside drawing is the page's end and the close.
        foreach (var n in new[] { 2_000, 10_000 })
        {
            var text = string.Join(" ", Enumerable.Range(0, n).Select(i => ((char)(0x4E00 + i)).ToString()));
            var doc = Doc(Letter([new TextElement { Bounds = new Box(0, 0, 612, 792), Paragraphs = [new Paragraph { Runs = [new TextRun(text, new TextStyle { Family = "Microsoft YaHei", Size = 2 })] }] }]));
            using var ctx = new RenderContext(null, RenderLimits.Default);
            var sink = new Sink();
            var sw = Stopwatch.StartNew();
            Exporter.ToPdf(doc, ctx, sink);
            var wall = sw.Elapsed.TotalSeconds;
            Say($"{n:N0} distinct ideographs of one typeface in a PDF: drawing {ctx.DrawTime.TotalSeconds:F1} s, page end and close {wall - ctx.DrawTime.TotalSeconds:F1} s, {sink.Count / 1048576.0:F1} MiB");
        }
    }

    // ---------- AC4: a passed-through JPEG is not decoded ----------

    /// <summary>
    /// A valid baseline YCbCr JPEG of <paramref name="side"/> squared pixels (4:4:4, JFIF), all mid-gray, written without
    /// holding its pixels: every block is a DC difference of 0 and an end-of-block, each a one-bit Huffman code.
    /// </summary>
    internal static void WriteGrayJpeg(string path, int side)
    {
        using var f = File.Create(path);
        void Seg(byte code, params byte[] data) { f.Write([0xFF, code, (byte)((data.Length + 2) >> 8), (byte)(data.Length + 2)]); f.Write(data); }
        f.Write([0xFF, 0xD8]);
        Seg(0xE0, [.. "JFIF\0"u8, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        Seg(0xDB, [0, .. Enumerable.Repeat((byte)1, 64)]);
        Seg(0xC0, 8, (byte)(side >> 8), (byte)side, (byte)(side >> 8), (byte)side, 3, 1, 0x11, 0, 2, 0x11, 0, 3, 0x11, 0);
        Seg(0xC4, [0x00, 1, .. new byte[15], 0x00]);   // DC table 0: one code of length 1, category 0
        Seg(0xC4, [0x10, 1, .. new byte[15], 0x00]);   // AC table 0: one code of length 1, end of block
        Seg(0xDA, 3, 1, 0x00, 2, 0x00, 3, 0x00, 0, 63, 0);
        var blocks = (long)(side / 8) * (side / 8) * 3;   // two zero bits a block
        var bits = blocks * 2;
        var zeros = new byte[1 << 20];
        for (var left = bits / 8; left > 0; left -= zeros.Length) f.Write(zeros, 0, (int)Math.Min(zeros.Length, left));
        if (bits % 8 != 0) f.WriteByte((byte)(0xFF >> (int)(bits % 8)));   // padded with one-bits
        f.Write([0xFF, 0xD9]);
    }

    [CalibrateFact]
    public void APassedThroughJpegAt225MP_IsNotDecoded()
    {
        // Run alone (its own process): the peak working set must not grow by a decode, whose pixels alone take 900 MB.
        var path = Path.Combine(_dir, "big.jpg");
        WriteGrayJpeg(path, 15_000);
        Assert.True(JpegWalk.IsPassedThroughShape(File.ReadAllBytes(path)));
        using (var codec = SKCodec.Create(path)) Assert.Equal(225_000_000, (long)codec.Info.Width * codec.Info.Height);

        Element Pic(Box at, Crop? crop = null, double rotation = 0, Stroke? stroke = null, ShapeKind mask = ShapeKind.Rectangle) =>
            new ImageElement { Bounds = at, Source = "big.jpg", Crop = crop, Rotation = rotation, Stroke = stroke, Mask = mask };
        var placements = new (string Name, Element Element)[]
        {
            ("plain", Pic(new Box(36, 36, 300, 300))),
            ("explicit zero crop", Pic(new Box(36, 36, 300, 300), new Crop(0, 0, 0, 0))),
            ("rotated 30 degrees", Pic(new Box(36, 36, 300, 300), rotation: 30)),
            ("rotated 90 degrees", Pic(new Box(36, 36, 300, 300), rotation: 90)),
            ("stroked", Pic(new Box(36, 36, 300, 300), stroke: new Stroke(new Rgba(0, 0, 0), 4))),
            ("oval mask", Pic(new Box(36, 36, 300, 300), mask: ShapeKind.Ellipse)),
            ("tiny (1 pt)", Pic(new Box(36, 36, 1, 1))),
            ("page-sized", Pic(new Box(0, 0, 612, 792))),
        };
        using (var warm = new RenderContext(_dir)) Exporter.ToPdf(Doc(Letter([new ShapeElement { Bounds = new Box(0, 0, 10, 10), Fill = new Rgba(0, 0, 0) }])), warm, new Sink());
        GC.Collect();
        Process.GetCurrentProcess().Refresh();
        var before = Process.GetCurrentProcess().PeakWorkingSet64;
        foreach (var (name, element) in placements)
        {
            using var ctx = new RenderContext(_dir);
            var sink = new Sink();
            var sw = Stopwatch.StartNew();
            Exporter.ToPdf(Doc(Letter([element])), ctx, sink);
            var took = sw.Elapsed;
            Process.GetCurrentProcess().Refresh();
            var growth = Process.GetCurrentProcess().PeakWorkingSet64 - before;
            Say($"{name}: {took.TotalMilliseconds:F0} ms, {sink.Count / 1048576.0:F1} MiB written, peak working set grown by {growth / 1048576} MiB");
            Assert.True(sink.Count >= new FileInfo(path).Length, $"{name}: the file is embedded whole");
            Assert.True(took < TimeSpan.FromSeconds(1), $"{name}: {took}");
            Assert.True(growth < 256L << 20, $"{name}: {growth / 1048576} MiB");
        }
    }
}
