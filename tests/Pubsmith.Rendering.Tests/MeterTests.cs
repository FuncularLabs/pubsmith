using Pubsmith.Core;
using SkiaSharp;

namespace Pubsmith.Rendering.Tests;

// WI-006 AC3: drawing time is metered inside PageRenderer.Render, from the context's clock, and a render stops at the
// next checkpoint once the context's total passes MaxDrawTime. Each step AC3 lists has a checkpoint, so the stop comes
// inside the element that repeats it (a picture's decode is one step: its time grows with the picture). Encoding, a
// PDF page's end and its close are not drawing time.
public sealed class MeterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pubsmith-meter-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly RenderLimits Open = RenderLimits.Default with
    {
        MaxPages = int.MaxValue, MaxPngPixels = long.MaxValue, MaxPdfTypefaces = int.MaxValue, MaxFontNames = int.MaxValue,
        MaxTextBoxCharacters = int.MaxValue, MaxTextBoxRuns = int.MaxValue, MaxWordArtGlyphs = int.MaxValue,
        MaxDrawTime = TimeSpan.MaxValue, MaxPdfBytes = long.MaxValue, MaxPngBytes = long.MaxValue,
    };

    /// <summary>Reads 0 until <paramref name="started"/> says the step under test has begun; then each reading advances <paramref name="step"/> (a second by default).</summary>
    private sealed class PhaseClock(Func<bool> started, long step = TimeSpan.TicksPerSecond) : TimeProvider
    {
        private long _now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => started() ? _now += step : _now;
    }

    /// <summary>Advances a fixed step at every reading; optionally throws at one reading (counted from 1).</summary>
    private sealed class StepClock(TimeSpan step, int throwAt = 0) : TimeProvider
    {
        private long _now;
        public int Readings { get; private set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp()
        {
            Readings++;
            _now += step.Ticks;
            if (Readings == throwAt) throw new InvalidOperationException("the clock failed");
            return _now;
        }
    }

    /// <summary>Stands still, except that it jumps an hour each time the output stream is written.</summary>
    private sealed class JumpClock : TimeProvider
    {
        public long Now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Now;
    }

    private sealed class JumpingStream(JumpClock clock) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { clock.Now += TimeSpan.TicksPerHour; base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { clock.Now += TimeSpan.TicksPerHour; base.Write(buffer); }
        public override void WriteByte(byte value) { clock.Now += TimeSpan.TicksPerHour; base.WriteByte(value); }
    }

    private static TextRun Run(string text, double size = 10) => new(text, new TextStyle { Family = "Arial", Size = size });

    private static TextElement Box(params Paragraph[] paragraphs) => new() { Bounds = new Box(0, 0, 300, 300), Paragraphs = paragraphs };

    private static Paragraph Para(params TextRun[] runs) => new() { Runs = runs };

    private string Picture(string name)
    {
        var path = Path.Combine(_dir, name);
        using var bmp = new SKBitmap(8, 8);
        bmp.Erase(SKColors.Teal);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return name;
    }

    // ---------- every kind of step is stopped inside its element ----------

    public static TheoryData<string> Steps() =>
    [
        "elements", "paragraphs", "empty paragraphs", "runs", "lines", "a long line of stacked marks",
        "WordArt runs, empty ones included", "WordArt glyphs built", "WordArt glyphs painted", "pictures", "shadowed elements",
        "between a shadow and its element", "characters of one long run", "lines laid out", "words of one line",
        "WordArt glyphs placed in an envelope", "WordArt glyphs placed on a path",
        "chunks of one long word measured", "chunks of one long word's glyphs positioned",
        "chunks of a WordArt run looked up and measured", "WordArt glyphs laid out in an envelope", "glyphless pieces of one line",
    ];

    [Theory]
    [MemberData(nameof(Steps))]
    public void EachKindOfStep_IsStoppedInsideItsElement(string step)
    {
        RenderContext? ctx = null;
        // The step under test begins; from then on each reading of the clock advances a second, and the limit is 10 s.
        Func<RenderContext, bool> begun = step switch
        {
            "paragraphs" or "empty paragraphs" => c => c.ParagraphsLaidOut > 0,
            "runs" => c => c.RunsLaidOut > 0,
            "lines" => c => c.LinesDrawn > 0,
            "a long line of stacked marks" => c => c.GlyphRunsDrawn > 0,
            "WordArt runs, empty ones included" => c => c.WordArtRunsBuilt > 0,
            "WordArt glyphs built" => c => c.WordArtGlyphsBuilt > 0,
            "WordArt glyphs painted" => c => c.WordArtGlyphsPainted > 0,
            "characters of one long run" => c => c.RunsLaidOut > 0,
            "lines laid out" => c => c.LinesLaidOut > 0,
            "words of one line" => c => c.WordsWrapped > 0,
            "WordArt glyphs placed in an envelope" or "WordArt glyphs placed on a path" => c => c.WordArtGlyphsPlaced > 0,
            "pictures" or "between a shadow and its element" => c => c.PicturesDrawn > 0,
            "chunks of one long word measured" => c => c.PieceChunksMeasured > 0,
            "chunks of one long word's glyphs positioned" => c => c.GlyphChunksPositioned > 0,
            "chunks of a WordArt run looked up and measured" => c => c.WordArtChunksMeasured > 0,
            "WordArt glyphs laid out in an envelope" => c => c.WordArtGlyphsLaid > 0,
            "glyphless pieces of one line" => c => c.LinesDrawn > 0,
            _ => _ => true,
        };
        // Between a shadow and its element the next reading must pass the limit at once: it jumps an hour.
        var clock = new PhaseClock(() => ctx is not null && begun(ctx), step == "between a shadow and its element" ? TimeSpan.TicksPerHour : TimeSpan.TicksPerSecond);
        ctx = new RenderContext(_dir, Open with { MaxDrawTime = TimeSpan.FromSeconds(10) }, clock);

        var marks = "a" + new string('́', 3_000);   // 3,001 glyphs on one line: 47 runs of 64
        var pic = Picture("p.png");
        Element[] elements = step switch
        {
            "elements" => Enumerable.Range(0, 200).Select(_ => (Element)new ShapeElement { Bounds = new Box(0, 0, 5, 5), Fill = new Rgba(0, 0, 0) }).ToArray(),
            "paragraphs" => [Box(Enumerable.Range(0, 200).Select(_ => Para(Run("a"))).ToArray())],
            "empty paragraphs" => [Box(Enumerable.Range(0, 200).Select(_ => new Paragraph()).ToArray())],
            "runs" => [Box(Para(Enumerable.Range(0, 200).Select(_ => Run("a")).ToArray()))],
            "lines" => [Box(Para(Run(string.Concat(Enumerable.Repeat("a\n", 200)))))],
            "a long line of stacked marks" => [Box(Para(Run(marks)))],
            "WordArt runs, empty ones included" => [Box(Para(Enumerable.Range(0, 200).Select(i => Run(i % 10 == 9 ? "W" : "")).ToArray())) with { Fit = TextFit.Stretch }],
            "WordArt glyphs built" or "WordArt glyphs painted" or "WordArt glyphs placed in an envelope" or "WordArt glyphs laid out in an envelope" => [Box(Para(Run(new string('W', 500)))) with { Fit = TextFit.Stretch }],
            "chunks of one long word measured" or "chunks of one long word's glyphs positioned" => [Box(Para(Run(new string('a', 20_000))))],   // one word: 20 chunks
            "chunks of a WordArt run looked up and measured" => [Box(Para(Run(new string('W', 20_000)))) with { Fit = TextFit.Stretch }],   // one run: 20 chunks
            // 20,000 one-unit words on one line, each an unpaired surrogate: no glyphs to draw
            "glyphless pieces of one line" => [Box(Para(Run(string.Concat(Enumerable.Repeat("\uD835 ", 20_000))))) with { Bounds = new Box(0, 0, 10_000_000, 300) }],
            "WordArt glyphs placed on a path" => [Box(Para(Run(new string('W', 500)))) with { Fit = TextFit.Stretch, Warp = new TextWarp(WarpKind.Circle) }],
            "characters of one long run" => [Box(Para(Run(string.Concat(Enumerable.Repeat("a ", 20_000)))))],   // 40,000 characters, one run
            "lines laid out" => [Box(Para(Run(string.Concat(Enumerable.Repeat("a\n", 200)))))],
            "words of one line" => [Box(Para(Run(string.Concat(Enumerable.Repeat("a ", 20_000))))) with { Bounds = new Box(0, 0, 10_000_000, 300) }],   // 20,000 words, one line
            "pictures" => Enumerable.Range(0, 50).Select(_ => (Element)new ImageElement { Bounds = new Box(0, 0, 20, 20), Source = pic }).ToArray(),
            "shadowed elements" => Enumerable.Range(0, 200).Select(_ => (Element)new ShapeElement { Bounds = new Box(0, 0, 5, 5), Stroke = new Stroke(new Rgba(0, 0, 0, 128), 0.25), Shadow = new Shadow(new Rgba(0, 0, 0, 128), 2, 2) }).ToArray(),
            "between a shadow and its element" => [new ImageElement { Bounds = new Box(0, 0, 20, 20), Source = pic, Shadow = new Shadow(new Rgba(0, 0, 0, 128), 2, 2) },
                                                   new ImageElement { Bounds = new Box(30, 0, 20, 20), Source = pic }],
            _ => throw new ArgumentException(step),
        };

        var ex = Assert.Throws<RenderLimitException>(() => Exporter.RenderBitmap(new Page(300, 300) { Elements = elements }, 72, ctx));
        Assert.Equal(RenderLimitException.DrawTime, ex.Limit);
        Assert.True(ex.Value > 10_000, $"{ex.Value} ms");
        Assert.Null(ex.Page);
        switch (step)
        {
            case "elements" or "shadowed elements": Assert.InRange(ex.Element!.Value, 1, 20); break;
            case "pictures": Assert.InRange(ex.Element!.Value, 1, 20); Assert.InRange(ctx.PicturesDrawn, 1, 20); break;
            case "between a shadow and its element": Assert.Equal(1, ex.Element); Assert.Equal(1, ctx.PicturesDrawn); break;   // the shadow's picture, not the element's
            default: Assert.Equal(1, ex.Element); break;
        }
        switch (step)
        {
            case "paragraphs" or "empty paragraphs": Assert.InRange(ctx.ParagraphsLaidOut, 1, 20); break;
            case "runs": Assert.InRange(ctx.RunsLaidOut, 1, 20); Assert.Equal(1, ctx.ParagraphsLaidOut); break;
            case "lines":
                // The stop is the 11th reading after the first line began: one before each line and one before each
                // run of glyphs (each line here is one run).
                Assert.Equal(6, ctx.LinesDrawn);
                Assert.Equal(5, ctx.GlyphRunsDrawn);
                break;
            case "a long line of stacked marks": Assert.Equal(1, ctx.LinesDrawn); Assert.InRange(ctx.GlyphRunsDrawn, 1, 20); break;
            case "WordArt runs, empty ones included": Assert.InRange(ctx.WordArtRunsBuilt, 1, 20); break;
            case "WordArt glyphs built": Assert.InRange(ctx.WordArtGlyphsBuilt, 1, 20); break;
            case "WordArt glyphs painted": Assert.Equal(500, ctx.WordArtGlyphsBuilt); Assert.InRange(ctx.WordArtGlyphsPainted, 1, 20); break;
            case "characters of one long run": Assert.Equal(1, ctx.RunsLaidOut); Assert.InRange(ctx.CharactersLaidOut, 1, 40_000 - 1); Assert.Equal(0, ctx.LinesLaidOut); break;
            case "lines laid out": Assert.InRange(ctx.LinesLaidOut, 1, 20); Assert.Equal(0, ctx.LinesDrawn); break;
            case "words of one line": Assert.InRange(ctx.WordsWrapped, 1, 20_000 - 1); Assert.Equal(0, ctx.LinesLaidOut); break;
            case "WordArt glyphs placed in an envelope" or "WordArt glyphs placed on a path": Assert.Equal(500, ctx.WordArtGlyphsBuilt); Assert.InRange(ctx.WordArtGlyphsPlaced, 1, 20); Assert.Equal(0, ctx.WordArtGlyphsPainted); break;
            // A long word is looked up, measured and positioned a chunk of at most 1,024 code points at a time.
            case "chunks of one long word measured": Assert.Equal(20_000, ctx.CharactersLaidOut); Assert.InRange(ctx.PieceChunksMeasured, 1, 19); Assert.Equal(0, ctx.LinesLaidOut); break;
            case "chunks of one long word's glyphs positioned": Assert.Equal(20, ctx.PieceChunksMeasured); Assert.Equal(1, ctx.LinesDrawn); Assert.InRange(ctx.GlyphChunksPositioned, 1, 19); Assert.Equal(0, ctx.GlyphRunsDrawn); break;
            case "chunks of a WordArt run looked up and measured": Assert.InRange(ctx.WordArtChunksMeasured, 1, 19); Assert.Equal(0, ctx.WordArtGlyphsBuilt); break;
            case "WordArt glyphs laid out in an envelope": Assert.Equal(500, ctx.WordArtGlyphsBuilt); Assert.InRange(ctx.WordArtGlyphsLaid, 1, 20); Assert.Equal(0, ctx.WordArtGlyphsPlaced); break;
            case "glyphless pieces of one line": Assert.Equal(1, ctx.LinesDrawn); Assert.InRange(ctx.GlyphlessPiecesSkipped, 1, 20); Assert.Equal(0, ctx.GlyphRunsDrawn); break;
        }
    }

    // ---------- what is not drawing time ----------

    [Fact]
    public void EncodingPageEndAndClose_AreNotDrawingTime()
    {
        // The clock jumps an hour whenever the output is written; drawing writes nothing to it (PDF pages are empty,
        // since SkPDF writes while it draws anything), so all of that time is encoding, a page's end or the close.
        var clock = new JumpClock();
        using var ctx = new RenderContext(null, Open, clock);
        var png = new JumpingStream(clock);
        Exporter.ToPng(new Page(100, 100) { Elements = [new ShapeElement { Bounds = new Box(10, 10, 50, 50), Fill = new Rgba(255, 0, 0) }] }, 72, ctx, png);
        var pdf = new JumpingStream(clock);
        Exporter.ToPdf(new PubsmithDocument { Pages = [new Page(100, 100), new Page(100, 100), new Page(100, 100)] }, ctx, pdf);
        Assert.True(clock.Now >= 2 * TimeSpan.TicksPerHour, "the outputs were written");
        Assert.True(ctx.DrawTime < TimeSpan.FromMinutes(1), $"{ctx.DrawTime}");
    }

    // ---------- the meter's bookkeeping ----------

    [Fact]
    public void ARenderThatThrows_StillAddsItsTime()
    {
        // Readings: the start (1 s), the first element's checkpoint (2 s), the second's throws; the stop reads 4 s.
        var clock = new StepClock(TimeSpan.FromSeconds(1), throwAt: 3);
        using var ctx = new RenderContext(null, Open, clock);
        var shape = new ShapeElement { Bounds = new Box(0, 0, 5, 5), Fill = new Rgba(0, 0, 0) };
        Assert.Throws<InvalidOperationException>(() => Exporter.RenderBitmap(new Page(20, 20) { Elements = [shape, shape] }, 72, ctx));
        Assert.Equal(4, clock.Readings);
        Assert.Equal(TimeSpan.FromSeconds(3), ctx.DrawTime);
    }

    [Fact]
    public void ACallersOwnCanvas_IsMetered_AndRestoredAfterAStop()
    {
        RenderContext? ctx = null;
        // The stop comes while the shadow's text is laid out: inside the element's rotation, the shadow's offset and its layer.
        var clock = new PhaseClock(() => ctx is not null && ctx.ParagraphsLaidOut > 0);
        ctx = new RenderContext(null, Open with { MaxDrawTime = TimeSpan.FromSeconds(10) }, clock);
        var text = Box(Enumerable.Range(0, 50).Select(_ => Para(Run("shadowed text"))).ToArray()) with { Rotation = 30, Shadow = new Shadow(new Rgba(0, 0, 0, 128), 3, 3) };

        using var bmp = new SKBitmap(300, 300);
        using var canvas = new SKCanvas(bmp);
        canvas.Translate(5, 7);
        canvas.Save();
        var saveCount = canvas.SaveCount;
        var matrix = canvas.TotalMatrix;
        var ex = Assert.Throws<RenderLimitException>(() => PageRenderer.Render(canvas, new Page(300, 300) { Elements = [text] }, ctx));
        Assert.Equal(RenderLimitException.DrawTime, ex.Limit);
        Assert.InRange(ctx.ParagraphsLaidOut, 1, 20);
        Assert.Equal(saveCount, canvas.SaveCount);
        Assert.Equal(matrix, canvas.TotalMatrix);
        Assert.True(ctx.DrawTime > TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void ACallersOwnCanvas_IsRestoredAfterAnyFailure()
    {
        // Not a limit: the clock fails while the shadow's text is laid out, inside the rotation, offset and layer.
        RenderContext? ctx = null;
        var clock = new PhaseClock(() => ctx is not null && ctx.ParagraphsLaidOut > 0);
        ctx = new RenderContext(null, Open, new FailingClock(clock));
        var text = Box(Enumerable.Range(0, 5).Select(_ => Para(Run("shadowed text"))).ToArray()) with { Rotation = 30, Shadow = new Shadow(new Rgba(0, 0, 0, 128), 3, 3) };
        using var bmp = new SKBitmap(300, 300);
        using var canvas = new SKCanvas(bmp);
        canvas.Translate(5, 7);
        var saveCount = canvas.SaveCount;
        var matrix = canvas.TotalMatrix;
        Assert.Throws<InvalidOperationException>(() => PageRenderer.Render(canvas, new Page(300, 300) { Elements = [text] }, ctx));
        Assert.Equal(saveCount, canvas.SaveCount);
        Assert.Equal(matrix, canvas.TotalMatrix);
    }

    /// <summary>Throws once its inner clock has started advancing (the step under test has begun).</summary>
    private sealed class FailingClock(TimeProvider inner) : TimeProvider
    {
        private long _last;
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override long GetTimestamp()
        {
            var now = inner.GetTimestamp();
            if (now != _last && _last != 0) throw new InvalidOperationException("the clock failed");
            if (now != 0) _last = now;
            return now;
        }
    }

    [Fact]
    public void ACallersOwnCanvas_IsMeteredFromStartToStop_AsTheExportersAre()
    {
        // Readings: the start, a checkpoint per element, the stop: 3 s for two elements, on any canvas.
        using var ctx = new RenderContext(null, Open, new StepClock(TimeSpan.FromSeconds(1)));
        var shape = new ShapeElement { Bounds = new Box(0, 0, 5, 5), Fill = new Rgba(0, 0, 0) };
        var page = new Page(20, 20) { Elements = [shape, shape] };
        using (var bmp = new SKBitmap(20, 20))
        using (var canvas = new SKCanvas(bmp))
            PageRenderer.Render(canvas, page, ctx);
        Assert.Equal(TimeSpan.FromSeconds(3), ctx.DrawTime);
        using (Exporter.RenderBitmap(page, 72, ctx)) { }
        Assert.Equal(TimeSpan.FromSeconds(6), ctx.DrawTime);
        Assert.Equal(TimeSpan.FromSeconds(1), ctx.LongestStep);
    }

    [Fact]
    public void TheLongestStep_IsTheLongestTimeBetweenTwoReadings()
    {
        // Readings at 0, 1, 4 and 5 s: the start, two checkpoints and the stop; the longest step is the 3 s one.
        var times = new Queue<long>([0, 1, 4, 5]);
        using var ctx = new RenderContext(null, Open, new ListClock(times));
        var shape = new ShapeElement { Bounds = new Box(0, 0, 5, 5), Fill = new Rgba(0, 0, 0) };
        using (Exporter.RenderBitmap(new Page(20, 20) { Elements = [shape, shape] }, 72, ctx)) { }
        Assert.Equal(TimeSpan.FromSeconds(5), ctx.DrawTime);
        Assert.Equal(TimeSpan.FromSeconds(3), ctx.LongestStep);
    }

    private sealed class ListClock(Queue<long> seconds) : TimeProvider
    {
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => seconds.Dequeue();
    }

    [Fact]
    public void TheTimeAddsUpAcrossCalls()
    {
        // Each call alone draws for 2 s; the context's total passes 5 s on a later call.
        var page = new Page(20, 20) { Elements = [new ShapeElement { Bounds = new Box(0, 0, 5, 5), Fill = new Rgba(0, 0, 0) }] };
        using (var fresh = new RenderContext(null, Open with { MaxDrawTime = TimeSpan.FromSeconds(5) }, new StepClock(TimeSpan.FromSeconds(1))))
        using (Exporter.RenderBitmap(page, 72, fresh)) Assert.Equal(TimeSpan.FromSeconds(2), fresh.DrawTime);

        using var ctx = new RenderContext(null, Open with { MaxDrawTime = TimeSpan.FromSeconds(5) }, new StepClock(TimeSpan.FromSeconds(1)));
        var calls = 0;
        var ex = Assert.Throws<RenderLimitException>(() => { while (calls++ < 10) Exporter.ToPng(page, 72, ctx, new MemoryStream()); });
        Assert.Equal(RenderLimitException.DrawTime, ex.Limit);
        Assert.InRange(calls, 3, 4);
    }

    [Fact]
    public void AStoppedContext_StaysStopped()
    {
        var clock = new StepClock(TimeSpan.FromSeconds(1));
        using var ctx = new RenderContext(null, Open with { MaxDrawTime = TimeSpan.FromSeconds(3) }, clock);
        var shape = new ShapeElement { Bounds = new Box(0, 0, 5, 5), Fill = new Rgba(0, 0, 0) };
        Assert.Throws<RenderLimitException>(() => Exporter.RenderBitmap(new Page(20, 20) { Elements = Enumerable.Repeat<Element>(shape, 10).ToArray() }, 72, ctx));
        var spent = ctx.DrawTime;

        var png = new MemoryStream();
        Assert.Equal(RenderLimitException.DrawTime, Assert.Throws<RenderLimitException>(() => Exporter.ToPng(new Page(20, 20), 72, ctx, png)).Limit);
        Assert.Equal(0, png.Length);
        Assert.Equal(spent, ctx.DrawTime);   // nothing more was drawn
    }

    [Fact]
    public void ThePdfPage_IsNamedInAStop()
    {
        var clock = new StepClock(TimeSpan.FromSeconds(1));
        using var ctx = new RenderContext(null, Open with { MaxDrawTime = TimeSpan.FromSeconds(6) }, clock);
        var shape = new ShapeElement { Bounds = new Box(0, 0, 5, 5), Fill = new Rgba(0, 0, 0) };
        var page = new Page(20, 20) { Elements = [shape, shape, shape] };
        // A page reads the clock at its start, at three checkpoints and at its stop: 4 s a page. On the second page
        // the third checkpoint brings the total to 7 s, past 6 s.
        var ex = Assert.Throws<RenderLimitException>(() => Exporter.ToPdf(new PubsmithDocument { Pages = [page, page, page] }, ctx, new MemoryStream()));
        Assert.Equal(2, ex.Page);
        Assert.Equal(3, ex.Element);
        Assert.Equal(7_000, ex.Value);
    }
}
