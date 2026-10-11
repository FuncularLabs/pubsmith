using Pubsmith.Core;
using SkiaSharp;

namespace Pubsmith.Rendering.Tests;

// WI-006 AC3 and AC9: a piece of text is drawn in runs of at most 64 glyphs (one glyph when the font is more than
// 256 device pixels tall), each after a checkpoint, and the runs draw exactly the pixels of the piece drawn at once.
public sealed class ChunkedTextTests
{
    private static readonly RenderLimits Open = RenderLimits.Default with
    {
        MaxPages = int.MaxValue, MaxPngPixels = long.MaxValue, MaxPdfTypefaces = int.MaxValue, MaxFontNames = int.MaxValue,
        MaxTextBoxCharacters = int.MaxValue, MaxTextBoxRuns = int.MaxValue, MaxWordArtGlyphs = int.MaxValue,
        MaxDrawTime = TimeSpan.MaxValue, MaxPdfBytes = long.MaxValue, MaxPngBytes = long.MaxValue,
    };

    // ---------- the threshold is the canvas's own scale ----------

    [Theory]
    [InlineData("PDF", 100, 0, 0, 2)]      // 100 device pixels: runs of 64
    [InlineData("PDF", 256, 0, 0, 2)]      // exactly 256: still runs of 64
    [InlineData("PDF", 300, 0, 0, 128)]    // 300 device pixels: one glyph a run
    [InlineData("PNG", 100, 72, 0, 2)]
    [InlineData("PNG", 100, 300, 0, 128)]  // 100 pt at 300 dpi is 417 device pixels
    [InlineData("PNG", 300, 72, 90, 128)]  // turned: the scale is the determinant's, not the matrix's first entry
    [InlineData("PNG", 300, 72, 45, 128)]
    [InlineData("PNG", 100, 72, 90, 2)]
    public void TheOneGlyphRunThreshold_IsTheCanvasScale(string output, double size, double dpi, double rotation, int runs)
    {
        // One word of 128 letters: one piece on one line (a word longer than its frame is not broken).
        var word = string.Concat(Enumerable.Range(0, 128).Select(i => (char)('a' + i % 26)));
        var page = new Page(400, 600) { Elements = [new TextElement { Bounds = new Box(0, 0, 400, 600), Rotation = rotation, Paragraphs = [new Paragraph { Runs = [new TextRun(word, new TextStyle { Family = "Arial", Size = size })] }] }] };
        using var ctx = new RenderContext(null, Open);
        if (output == "PDF") Exporter.ToPdf(new PubsmithDocument { Pages = [page] }, ctx, new MemoryStream());
        else using (Exporter.RenderBitmap(page, dpi, ctx)) { }
        Assert.Equal(1, ctx.LinesDrawn);
        Assert.Equal(runs, ctx.GlyphRunsDrawn);
    }

    [Theory]
    [InlineData(2f, 0.5f, 200f, 2)]    // area scale 1: 200 device pixels, though the x scale alone is 2
    [InlineData(0.5f, 2f, 300f, 128)]  // area scale 1: 300 device pixels, though the x scale alone is 0.5
    public void TheOneGlyphRunThreshold_UnderAnUnevenScale_IsTheSquareRootOfTheDeterminant(float sx, float sy, float size, int runs)
    {
        var word = string.Concat(Enumerable.Range(0, 128).Select(i => (char)('a' + i % 26)));
        using var ctx = new RenderContext(null, Open);
        using var typeface = SKTypeface.FromFamilyName("Arial");
        using var font = new SKFont(typeface, size);
        using var paint = new SKPaint();
        using var bmp = new SKBitmap(64, 64);
        using var canvas = new SKCanvas(bmp);
        canvas.Scale(sx, sy);
        TextRenderer.DrawPiece(canvas, word, font, 0, size, paint, ctx);
        Assert.Equal(runs, ctx.GlyphRunsDrawn);
    }

    // ---------- a line's metrics, read once per run ----------

    public static TheoryData<string> TallRunPlacements() => ["first word", "middle word", "last word", "middle word of a second line", "first line, above a line of small runs"];

    [Theory]
    [MemberData(nameof(TallRunPlacements))]
    public void ALineIsAsTallAsItsTallestRun_WhereverThatRunStands(string placement)
    {
        // A tall X among small words: the line's baseline lies at its top plus the tallest font's spacing times the line
        // spacing, less that font's descent, wherever the tall run stands; the next line is as tall as its own runs, not
        // the tall one's. No glyph here goes below the baseline, so the lowest ink is the last line's baseline.
        const double small = 8, tall = 60, dpi = 150;
        TextRun Run(string text, double size) => new(text, new TextStyle { Family = "Arial", Size = size });
        TextRun[] runs = placement switch
        {
            "first word" => [Run("X", tall), Run(" a a", small)],
            "middle word" => [Run("a ", small), Run("X", tall), Run(" a", small)],
            "last word" => [Run("a a ", small), Run("X", tall)],
            "middle word of a second line" => [Run("a a\na ", small), Run("X", tall), Run(" a", small)],   // a forced break: the tall run on line 2
            "first line, above a line of small runs" => [Run("a ", small), Run("X", tall), Run(" a\na a", small)],   // line 2 holds only small runs
            _ => throw new ArgumentException(placement),
        };
        var paragraph = new Paragraph { Runs = runs };
        var page = new Page(300, 200) { Elements = [new TextElement { Bounds = new Box(0, 0, 300, 200), Paragraphs = [paragraph] }] };
        using var ctx = new RenderContext(null, Open);
        using var bmp = Exporter.RenderBitmap(page, dpi, ctx);
        var lowestInk = Enumerable.Range(0, bmp.Height).Last(y => Enumerable.Range(0, bmp.Width).Any(x => bmp.GetPixel(x, y).Red < 128));

        using var typeface = SKTypeface.FromFamilyName("Arial");
        using var smallFont = new SKFont(typeface, (float)small);
        using var tallFont = new SKFont(typeface, (float)tall);
        var top = Insets.PublisherDefault.Top;
        var baseline = placement switch
        {
            // line 1 holds only small runs; the tall run sets line 2
            "middle word of a second line" => top + smallFont.Spacing * paragraph.LineSpacing + tallFont.Spacing * paragraph.LineSpacing - tallFont.Metrics.Descent,
            // the tall run sets line 1; line 2 is as tall as its small runs
            "first line, above a line of small runs" => top + tallFont.Spacing * paragraph.LineSpacing + smallFont.Spacing * paragraph.LineSpacing - smallFont.Metrics.Descent,
            _ => top + tallFont.Spacing * paragraph.LineSpacing - tallFont.Metrics.Descent,
        };
        Assert.InRange(lowestInk, baseline * dpi / 72 - 3, baseline * dpi / 72 + 1);
    }

    // ---------- drawn in runs, the same pixels ----------

    // Texts made as Task 0 round 3's probe made them (one seeded sequence), where the variants Design names drew
    // different pixels: a piece's origin folded into its positions, or passed to GetGlyphPositions; positions re-based
    // per run; runs advanced by measured widths.
    private static Dictionary<string, string> Texts()
    {
        var rnd = new Random(7);
        string Letters(int n) => new(Enumerable.Range(0, n).Select(_ => (char)('a' + rnd.Next(26))).ToArray());
        var texts = new Dictionary<string, string> { ["63"] = Letters(63), ["64"] = Letters(64), ["65"] = Letters(65), ["1000"] = Letters(1000), ["1000 Calibri"] = Letters(1000) };
        Letters(1000);   // the probe's Times New Roman text, kept for the sequence
        var s = Letters(100);
        texts["surrogate at 63"] = s[..63] + char.ConvertFromUtf32(0x1D400 + rnd.Next(26)) + s[63..];   // MATHEMATICAL BOLD CAPITAL
        s = Letters(100);
        rnd.Next(26);   // the probe's second surrogate text
        texts["CJK"] = string.Concat(Enumerable.Range(0, 300).Select(_ => (char)(0x4E00 + rnd.Next(2000))));
        // Longer than one chunk of 1,024 (rev 17), after the probe's texts so theirs stay as they were.
        texts["2000"] = Letters(2000);
        texts["CJK 1500"] = string.Concat(Enumerable.Range(0, 1500).Select(_ => (char)(0x4E00 + rnd.Next(2000))));
        return texts;
    }

    public static TheoryData<string, string, string, float, float, float> Rows() => new()
    {
        { "1,000 random letters, rotated", "1000", "Arial", 9.7f, 150f, 30f },
        { "1,000 random letters in Calibri", "1000 Calibri", "Calibri", 9.7f, 300f, 0f },
        { "300 CJK ideographs with no space", "CJK", "Microsoft YaHei", 9.7f, 300f, 0f },
        { "300 CJK ideographs, rotated", "CJK", "Microsoft YaHei", 37.3f, 150f, 30f },
        { "63 glyphs", "63", "Arial", 9.7f, 300f, 0f },
        { "64 glyphs", "64", "Arial", 9.7f, 300f, 0f },
        { "65 glyphs", "65", "Arial", 9.7f, 300f, 0f },
        { "a surrogate pair at the boundary", "surrogate at 63", "Cambria Math", 9.7f, 300f, 0f },
        { "a 300 pt font", "64", "Arial", 300f, 72f, 0f },
        { "2,000 letters: two chunks, all on the canvas", "2000", "Arial", 1.5f, 72f, 0f },
        { "1,500 CJK ideographs: two chunks, all on the canvas, rotated", "CJK 1500", "Microsoft YaHei", 2f, 72f, 30f },
    };

    // ---------- a long piece worked in chunks, bit for bit the whole piece ----------

    public static TheoryData<string, string, float> LongPieces()
    {
        var rnd = new Random(17);
        var letters = new string(Enumerable.Range(0, 3_000).Select(_ => (char)('a' + rnd.Next(26))).ToArray());
        var cjk = new string(Enumerable.Range(0, 1_500).Select(_ => (char)(0x4E00 + rnd.Next(20_000))).ToArray());
        var extB = string.Concat(Enumerable.Range(0, 800).Select(_ => char.ConvertFromUtf32(0x20000 + rnd.Next(40_000))));   // 1,600 units, all pairs
        var straddling = new string('a', 1_023) + char.ConvertFromUtf32(0x1D400) + new string('b', 1_500);   // a pair across the first boundary
        var rows = new TheoryData<string, string, float>();
        foreach (var size in new[] { 4f, 9.7f, 250f, 256f, BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(256f) + 1), 300f, 1_000f, 4_000f })
        {
            rows.Add("Arial", letters, size);
            rows.Add("Microsoft YaHei", cjk, size);
            rows.Add("SimSun-ExtB", extB, size);
            rows.Add("Cambria Math", straddling, size);
        }
        return rows;
    }

    private static SKFont Font(SKTypeface typeface, float size) => new(typeface, size) { Subpixel = true, Edging = SKFontEdging.Antialias };   // as TextRenderer makes them

    [Theory]
    [MemberData(nameof(LongPieces))]
    public void ALongPiece_MeasuredInChunks_IsItsWholeWidth_BitForBit(string family, string text, float size)
    {
        using var typeface = SKTypeface.FromFamilyName(family);
        Assert.Equal(family, typeface.FamilyName);
        using var font = Font(typeface, size);
        using var ctx = new RenderContext(null, Open);
        var chunked = TextRenderer.Measure(font, text, ctx);
        Assert.Equal(BitConverter.SingleToInt32Bits(font.MeasureText(text)), BitConverter.SingleToInt32Bits(chunked));
        Assert.Equal(TextRenderer.Chunks(text).Count(), ctx.PieceChunksMeasured);
        Assert.True(ctx.PieceChunksMeasured > 1);
    }

    [Theory]
    [MemberData(nameof(LongPieces))]
    public void ALongPiece_LookedUpAndPositionedInChunks_IsTheWholePiece_BitForBit(string family, string text, float size)
    {
        using var typeface = SKTypeface.FromFamilyName(family);
        using var font = Font(typeface, size);
        using var ctx = new RenderContext(null, Open);
        var (glyphs, positions) = TextRenderer.Glyphs(font, text, ctx);
        var whole = font.GetGlyphs(text);
        Assert.Equal(whole, glyphs);
        var wholePositions = font.GetGlyphPositions(whole, new SKPoint(0, 0));
        Assert.Equal(wholePositions.Length, positions.Length);
        for (var i = 0; i < whole.Length; i++)
        {
            Assert.Equal(BitConverter.SingleToInt32Bits(wholePositions[i].X), BitConverter.SingleToInt32Bits(positions[i].X));
            Assert.Equal(BitConverter.SingleToInt32Bits(wholePositions[i].Y), BitConverter.SingleToInt32Bits(positions[i].Y));
        }
        Assert.Equal(TextRenderer.Chunks(text).Count(), ctx.GlyphChunksPositioned);
    }

    [Fact]
    public void ALongPiece_MeasuresEachDistinctGlyphOnce()
    {
        // 20,000 ideographs cycling through 2,000: measuring and positioning each ask the font for 2,000 advances, not
        // 20,000 (a glyph met again in a later chunk is not measured again).
        var text = new string(Enumerable.Range(0, 20_000).Select(i => (char)(0x4E00 + i % 2_000)).ToArray());
        using var typeface = SKTypeface.FromFamilyName("Microsoft YaHei");
        using var font = Font(typeface, 10);
        using var ctx = new RenderContext(null, Open);
        TextRenderer.Measure(font, text, ctx);
        Assert.Equal(2_000, ctx.GlyphAdvancesMeasured);
        TextRenderer.Glyphs(font, text, ctx);
        Assert.Equal(4_000, ctx.GlyphAdvancesMeasured);
        Assert.Equal(20, ctx.PieceChunksMeasured);
        Assert.Equal(20, ctx.GlyphChunksPositioned);
    }

    [Fact]
    public void AShortPiece_IsMeasuredAndPositionedWhole()
    {
        // At most one chunk: the font's own whole-piece calls, and none of the chunk counters move.
        using var typeface = SKTypeface.FromFamilyName("Arial");
        using var font = Font(typeface, 10);
        using var ctx = new RenderContext(null, Open);
        var text = new string('a', 1_024);
        Assert.Equal(BitConverter.SingleToInt32Bits(font.MeasureText(text)), BitConverter.SingleToInt32Bits(TextRenderer.Measure(font, text, ctx)));
        var (glyphs, positions) = TextRenderer.Glyphs(font, text, ctx);
        Assert.Equal(font.GetGlyphs(text), glyphs);
        Assert.Equal(font.GetGlyphPositions(glyphs, new SKPoint(0, 0)), positions);
        Assert.Empty(TextRenderer.Glyphs(font, "", ctx).Glyphs);
        Assert.Equal(0, ctx.PieceChunksMeasured + ctx.GlyphChunksPositioned + ctx.GlyphAdvancesMeasured);
    }

    [Theory]
    [MemberData(nameof(LongPieces))]
    public void AWordArtRun_LookedUpAndMeasuredInChunks_IsTheWholeRun_BitForBit(string family, string text, float size)
    {
        using var typeface = SKTypeface.FromFamilyName(family);
        using var font = Font(typeface, size);
        using var ctx = new RenderContext(null, Open);
        var (ids, widths) = TextRenderer.LookUpAndMeasure(font, text, ctx);
        var whole = font.GetGlyphs(text);
        Assert.Equal(whole, ids);
        Assert.Equal(font.GetGlyphWidths(whole).Select(BitConverter.SingleToInt32Bits), widths.Select(BitConverter.SingleToInt32Bits));
        Assert.Equal(TextRenderer.Chunks(text).Count(), ctx.WordArtChunksMeasured);
    }

    // ---------- text that is not valid UTF-16: what the whole-piece calls give ----------

    public static TheoryData<string, float> NotValidUtf16()
    {
        var rows = new TheoryData<string, float>();
        foreach (var size in new[] { 10f, 300f })
            foreach (var row in new[] { "a lone high surrogate in the second chunk", "a lone high surrogate at the first boundary", "a lone low surrogate at the start", "a lone low surrogate at the end", "a high half at the end" })
                rows.Add(row, size);
        return rows;
    }

    private static string NotValid(string row) => row switch
    {
        "a lone high surrogate in the second chunk" => new string('a', 1_500) + '\uD835' + new string('b', 500),
        "a lone high surrogate at the first boundary" => new string('a', 1_023) + '\uD835' + new string('b', 1_000),
        "a lone low surrogate at the start" => '\uDC00' + new string('a', 2_000),
        "a lone low surrogate at the end" => new string('a', 2_000) + '\uDC00',
        "a high half at the end" => new string('a', 2_000) + '\uD835',
        _ => throw new ArgumentException(row),
    };

    [Theory]
    [MemberData(nameof(NotValidUtf16))]
    public void ALongPieceThatIsNotValidUtf16_IsWhatTheWholePieceCallsGive(string row, float size)
    {
        // Skia's whole-piece calls take an unpaired surrogate to make the whole text invalid (width 0, no glyphs); a long
        // piece worked in chunks gives the same, rather than measuring and drawing the chunks that are valid on their own.
        var text = NotValid(row);
        using var typeface = SKTypeface.FromFamilyName("Arial");
        using var font = Font(typeface, size);
        using var ctx = new RenderContext(null, Open);
        Assert.Equal(BitConverter.SingleToInt32Bits(font.MeasureText(text)), BitConverter.SingleToInt32Bits(TextRenderer.Measure(font, text, ctx)));
        var whole = font.GetGlyphs(text);
        var (glyphs, positions) = TextRenderer.Glyphs(font, text, ctx);
        Assert.Equal(whole, glyphs);
        SKPoint[] wholePositions = whole.Length == 0 ? [] : font.GetGlyphPositions(whole, new SKPoint(0, 0));
        Assert.Equal(wholePositions, positions);
        var (ids, widths) = TextRenderer.LookUpAndMeasure(font, text, ctx);
        Assert.Equal(whole, ids);
        Assert.Equal(font.GetGlyphWidths(whole), widths);

        SKBitmap Draw(Action<SKCanvas, SKPaint> draw)
        {
            var bmp = new SKBitmap(new SKImageInfo(400, 400, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(bmp);
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { IsAntialias = true, Color = SKColors.Black };
            draw(canvas, paint);
            return bmp;
        }
        using var inChunks = Draw((c, p) => TextRenderer.DrawPiece(c, text, font, 10, size, p, ctx));
        using var atOnce = Draw((c, p) => c.DrawText(text, 10, size, SKTextAlign.Left, font, p));
        Assert.Equal(atOnce.Bytes, inChunks.Bytes);
    }

    [Fact]
    public void ManyLongPieces_ShareOneTableOfAdvances()
    {
        // The table of distinct glyphs' advances (65,536 of them) belongs to the context and is reused from piece to piece,
        // so a long piece allocates for its own glyphs only (round 5: two tables a piece made a box of 8,175 such words
        // allocate 5.6 GB).
        using var typeface = SKTypeface.FromFamilyName("Arial");
        using var font = Font(typeface, 10);
        using var ctx = new RenderContext(null, Open);
        var text = new string('a', 1_025);
        TextRenderer.Measure(font, text, ctx);   // the table is made once
        TextRenderer.Glyphs(font, text, ctx);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            TextRenderer.Measure(font, text, ctx);
            TextRenderer.Glyphs(font, text, ctx);
        }
        var perPiece = (GC.GetAllocatedBytesForCurrentThread() - before) / 100;
        Assert.True(perPiece < 64 * 1_024, $"{perPiece:N0} bytes a piece");
    }

    [Fact]
    public void TheSharedTable_ForgetsOnlyTheLastPiecesGlyphs()
    {
        // Begin forgets the glyphs the last piece met, and only those. A table that forgot nothing would be right but
        // would reset every glyph the context had ever met at each piece, a cost growing with the square of the pieces
        // (the round 6 review: boxes of 500, 1,000 and 2,000 distinct-ideograph words drew in 0.33, 1.04 and 3.96 s
        // instead of 0.13 to 0.22 s); no step bound or allocation bound sees that.
        using var typeface = SKTypeface.FromFamilyName("Microsoft YaHei");
        using var font = Font(typeface, 10);
        using var ctx = new RenderContext(null, Open);
        string Ideographs(int distinct, int length) => new(Enumerable.Range(0, length).Select(i => (char)(0x4E00 + i % distinct)).ToArray());
        TextRenderer.Measure(font, Ideographs(2_000, 4_000), ctx);
        Assert.Equal(2_000, ctx.Advances.Remembered);
        TextRenderer.Glyphs(font, Ideographs(500, 1_500), ctx);
        Assert.Equal(500, ctx.Advances.Remembered);   // the 2,000 forgotten, the 500 met
        TextRenderer.Measure(font, new string('a', 1_025), ctx);
        Assert.Equal(1, ctx.Advances.Remembered);
        Assert.Equal(2_501, ctx.GlyphAdvancesMeasured);
    }

    [Fact]
    public void TheSharedTable_ForgetsAPieceWhoseMeasuringFailed()
    {
        // A glyph joins the list of met glyphs before it is measured, so a piece whose measuring failed part-way (here:
        // a table begun with no font) is still forgotten by the next Begin. Otherwise its glyphs would stay known with
        // an advance of 0 for the next piece in the context, which a reused context would draw too narrow.
        using var typeface = SKTypeface.FromFamilyName("Arial");
        using var font = Font(typeface, 10);
        using var ctx = new RenderContext(null, Open);
        var table = ctx.Advances;
        table.Begin(null!);
        Assert.ThrowsAny<Exception>(() => table.Learn(font.GetGlyphs("abc"), ctx));
        Assert.Equal(3, table.Remembered);
        var text = new string('a', 1_025);
        Assert.Equal(BitConverter.SingleToInt32Bits(font.MeasureText(text)), BitConverter.SingleToInt32Bits(TextRenderer.Measure(font, text, ctx)));
        Assert.Equal(1, table.Remembered);
    }

    [Fact]
    public void LongPiecesInOneContext_EachUseTheirOwnFontsAdvances()
    {
        // The shared table forgets the last piece's glyphs: the same letters at another size, or measured at the canonical
        // strike and then positioned with the font's own advances, are each bit for bit their own whole-piece results.
        using var typeface = SKTypeface.FromFamilyName("Arial");
        using var small = Font(typeface, 10);
        using var large = Font(typeface, 20);
        using var huge = Font(typeface, 300);
        using var ctx = new RenderContext(null, Open);
        var text = new string(Enumerable.Range(0, 3_000).Select(i => (char)('a' + i % 26)).ToArray());
        int Bits(float f) => BitConverter.SingleToInt32Bits(f);
        Assert.Equal(Bits(small.MeasureText(text)), Bits(TextRenderer.Measure(small, text, ctx)));
        Assert.Equal(Bits(large.MeasureText(text)), Bits(TextRenderer.Measure(large, text, ctx)));
        Assert.Equal(Bits(huge.MeasureText(text)), Bits(TextRenderer.Measure(huge, text, ctx)));   // the 64-unit strike's advances
        var whole = huge.GetGlyphs(text);
        Assert.Equal(huge.GetGlyphPositions(whole, new SKPoint(0, 0)), TextRenderer.Glyphs(huge, text, ctx).Positions);   // the font's own
        Assert.Equal(small.GetGlyphPositions(whole, new SKPoint(0, 0)), TextRenderer.Glyphs(small, text, ctx).Positions);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("1,024 letters", 1)]
    [InlineData("1,025 letters", 2)]
    [InlineData("a pair across the boundary", 2)]
    [InlineData("pairs only, 2,048 units", 2)]
    [InlineData("a lone high surrogate at the boundary", 2)]
    [InlineData("a high half at the end", 2)]
    public void Chunks_AreAtMost1024Units_AndNeverSplitAPair(string row, int count)
    {
        var text = row switch
        {
            "" => "",
            "1,024 letters" => new string('a', 1_024),
            "1,025 letters" => new string('a', 1_025),
            "a pair across the boundary" => new string('a', 1_023) + char.ConvertFromUtf32(0x1D400) + "b",
            "pairs only, 2,048 units" => string.Concat(Enumerable.Repeat(char.ConvertFromUtf32(0x20000), 1_024)),
            "a lone high surrogate at the boundary" => new string('a', 1_023) + '\uD835' + new string('b', 10),   // unpaired: kept whole all the same
            "a high half at the end" => new string('a', 1_500) + '\uD835',   // the last chunk keeps it: there is nothing to pair it with
            _ => throw new ArgumentException(row),
        };
        var chunks = TextRenderer.Chunks(text).Take(text.Length + 2).ToList();   // a chunk of no units would never end
        Assert.Equal(count, chunks.Count);
        Assert.Equal(text, string.Concat(chunks.Select(c => text.Substring(c.Start, c.Length))));
        Assert.All(chunks, c => Assert.InRange(c.Length, 1, 1_024));
        Assert.All(chunks.Where(c => c.Start + c.Length < text.Length), c => Assert.False(char.IsHighSurrogate(text[c.Start + c.Length - 1])));
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void ALongPiece_DrawnInRuns_IsPixelIdentical(string row, string textKey, string family, float size, float dpi, float rotation)
    {
        Assert.NotNull(row);
        var text = Texts()[textKey];
        using var ctx = new RenderContext(null, Open);
        using var typeface = SKTypeface.FromFamilyName(family);
        using var font = new SKFont(typeface, size) { Subpixel = true, Edging = SKFontEdging.Antialias };
        using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(20, 40, 160) };
        float x = 13.37f, y = 50.25f + size;
        var width = font.MeasureText(text);
        int w = (int)Math.Min(4000, Math.Ceiling((x + width + 20) * dpi / 72)), h = (int)Math.Ceiling((y + size + 20) * dpi / 72);
        if (rotation != 0) { w = Math.Min(4000, Math.Max(w, h)); h = w; }

        SKBitmap Draw(Action<SKCanvas> draw)
        {
            var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(bmp);
            canvas.Clear(SKColors.White);
            canvas.Scale(dpi / 72);
            if (rotation != 0) canvas.RotateDegrees(rotation, x, y);
            draw(canvas);
            return bmp;
        }

        using var inRuns = Draw(c => TextRenderer.DrawPiece(c, text, font, x, y, paint, ctx));
        using var atOnce = Draw(c => c.DrawText(text, x, y, SKTextAlign.Left, font, paint));
        var glyphs = font.GetGlyphs(text).Length;
        Assert.Equal(size * dpi / 72 > 256 ? glyphs : (glyphs + 63) / 64, ctx.GlyphRunsDrawn);
        var a = inRuns.Bytes;
        var b = atOnce.Bytes;
        Assert.True(b.Any(v => v != 255), "the reference drew something");
        var differing = Enumerable.Range(0, a.Length / 4).Count(i => a[4 * i] != b[4 * i] || a[4 * i + 1] != b[4 * i + 1] || a[4 * i + 2] != b[4 * i + 2] || a[4 * i + 3] != b[4 * i + 3]);
        Assert.True(differing == 0, $"{differing} pixels differ");
    }
}
