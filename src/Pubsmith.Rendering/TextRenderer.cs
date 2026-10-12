using Pubsmith.Core;
using SkiaSharp;

namespace Pubsmith.Rendering;

/// <summary>
/// Spike-level text layout: greedy word wrap of styled runs inside the frame's insets, left/center/right/justify.
/// Line height = the tallest font's natural spacing x the paragraph's LineSpacing multiple; the extra space sits
/// above the text (as in Publisher), so the baseline is lineHeight - descent below the line top.
/// Text is clipped to the frame (Publisher hides overflow).
/// </summary>
internal static class TextRenderer
{
    private sealed record Piece(string Text, SKFont Font, SKColor Color, float Width, float Descent, float Spacing);

    private sealed class Word
    {
        public List<Piece> Pieces { get; } = [];
        public float Width => Pieces.Sum(p => p.Width);
        public float SpaceAfter { get; set; }     // width of the space that followed this word (0 at line end)
        public bool BreakAfter { get; set; }      // forced line break after this word
    }

    /// <summary>Draws a text box (not WordArt: see <see cref="PrepareStretched"/>).</summary>
    public static void Draw(SKCanvas canvas, TextElement t, RenderContext ctx)
    {
        var b = t.Bounds;
        var ins = t.Insets;
        var left = (float)(b.X + ins.Left);
        var width = (float)(b.Width - ins.Left - ins.Right);
        var top = (float)(b.Y + ins.Top);
        var y = top;
        var fonts = new List<SKFont>();
        var lines = new List<(List<Word> Line, bool IsLast, TextAlignment Align, float Baseline)>();

        canvas.Save();
        canvas.ClipRect(PageRenderer.Rect(b), SKClipOperation.Intersect, antialias: true);
        try
        {
            // Pass 1: lay out every line top-down; pass 2 shifts the block for vertical anchoring and draws.
            foreach (var p in t.Paragraphs)
            {
                ctx.Checkpoint();
                ctx.ParagraphsLaidOut++;
                y += (float)p.SpaceBefore;
                var words = Tokenize(p, ctx, fonts);
                if (words.Count == 0)
                {
                    // An empty paragraph takes the height of its last run (the importer keeps the paragraph
                    // mark's formatting as an empty run); a paragraph with no runs at all uses the default style.
                    using var f = MakeFont(p.Runs.Count > 0 ? p.Runs[^1].Style : new TextStyle(), ctx, fonts, track: false);
                    y += p.ExactLineSpacing is { } exactEmpty ? (float)exactEmpty : f.Spacing * (float)p.LineSpacing;
                }
                foreach (var (line, isLast) in Wrap(words, width, ctx))
                {
                    ctx.Checkpoint();
                    ctx.LinesLaidOut++;
                    // The tallest of the line's fonts: the same maxima as reading each piece's font, read once per run.
                    float descent = float.MinValue, spacing = float.MinValue;
                    foreach (var w in line)
                        foreach (var pc in w.Pieces)
                        {
                            descent = Math.Max(descent, pc.Descent);
                            spacing = Math.Max(spacing, pc.Spacing);
                        }
                    var lineHeight = p.ExactLineSpacing is { } exact ? (float)exact : spacing * (float)p.LineSpacing;
                    lines.Add((line, isLast, p.Alignment, y + lineHeight - descent));
                    y += lineHeight;
                }
                y += (float)p.SpaceAfter;
            }
            var available = (float)(b.Height - ins.Top - ins.Bottom);
            var used = y - top;
            var shift = t.VerticalAlign switch
            {
                TextVerticalAlign.Middle => (available - used) / 2,
                TextVerticalAlign.Bottom => available - used,
                _ => 0f,
            };
            foreach (var (line, isLast, align, baseline) in lines)
            {
                ctx.Checkpoint();
                ctx.LinesDrawn++;
                DrawLine(canvas, line, isLast, align, left, width, baseline + shift, ctx);
            }
        }
        finally
        {
            canvas.Restore();
            foreach (var f in fonts) f.Dispose();
        }
    }

    /// <summary>
    /// WordArt-style text, built once: all text on one line as glyph outlines, scaled (non-uniformly) so its ink
    /// fills the inner frame exactly; optional outline stroked on top. Forced breaks are drawn as spaces.
    /// </summary>
    public static StretchedText PrepareStretched(TextElement t, RenderContext ctx)
    {
        var fonts = new List<SKFont>();
        var glyphs = new List<Glyph>();
        ctx.GlyphBuilds++;
        try
        {
            foreach (var run in t.Paragraphs.SelectMany(p => p.Runs))
            {
                ctx.Checkpoint();
                ctx.WordArtRunsBuilt++;
                var font = MakeFont(run.Style, ctx, fonts);
                var text = run.Text.Replace('\n', ' ');
                if (run.Style.AllCaps) text = text.ToUpperInvariant();
                var (ids, widths) = LookUpAndMeasure(font, text, ctx);
                var color = PageRenderer.Color(run.Style.Color);
                for (var i = 0; i < ids.Length; i++)
                {
                    ctx.Checkpoint();
                    ctx.WordArtGlyphsBuilt++;
                    glyphs.Add(new Glyph(font.GetGlyphPath(ids[i]) ?? new SKPath(), widths[i], color));
                }
            }
            var b = t.Bounds; var ins = t.Insets;
            var target = SKRect.Create((float)(b.X + ins.Left), (float)(b.Y + ins.Top), (float)(b.Width - ins.Left - ins.Right), (float)(b.Height - ins.Top - ins.Bottom));
            var outline = t.Outline is { Width: > 0 } o
                ? new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)o.Width, Color = PageRenderer.Color(o.Color), StrokeJoin = SKStrokeJoin.Round }
                : null;
            try { return new StretchedText(WordArt.Place(glyphs, target, t.Warp, ctx), outline, ctx); }
            catch
            {
                outline?.Dispose();   // no StretchedText owns it
                throw;
            }
        }
        finally
        {
            foreach (var g in glyphs) g.Path.Dispose();
            foreach (var f in fonts) f.Dispose();
        }
    }

    /// <summary>
    /// Draws a piece of text in runs of at most 64 glyphs (one glyph when the font is more than 256 device pixels tall:
    /// its size times the canvas's own scale), each after a checkpoint. The piece's glyphs and their positions are those
    /// of the whole piece (<see cref="Glyphs"/>); each run is a positioned text blob drawn at the piece's origin, which
    /// draws exactly the pixels of the piece drawn at once.
    /// </summary>
    internal static void DrawPiece(SKCanvas canvas, string text, SKFont font, float x, float baseline, SKPaint paint, RenderContext ctx)
    {
        var (glyphs, positions) = Glyphs(font, text, ctx);
        if (glyphs.Length == 0)
        {
            // Nothing to draw (text that is not valid UTF-16): a checkpoint all the same, so a line of such pieces is
            // not one step.
            ctx.Checkpoint();
            ctx.GlyphlessPiecesSkipped++;
            return;
        }
        var m = canvas.TotalMatrix;
        var devicePixels = font.Size * Math.Sqrt(Math.Abs((double)m.ScaleX * m.ScaleY - (double)m.SkewX * m.SkewY));
        var perRun = devicePixels > 256 ? 1 : 64;
        for (var start = 0; start < glyphs.Length; start += perRun)
        {
            ctx.Checkpoint();
            ctx.GlyphRunsDrawn++;
            var n = Math.Min(perRun, glyphs.Length - start);
            using var builder = new SKTextBlobBuilder();
            builder.AddPositionedRun(glyphs.AsSpan(start, n), font, positions.AsSpan(start, n));
            using var blob = builder.Build();
            if (blob is not null) canvas.DrawText(blob, x, baseline, paint);
        }
    }

    /// <summary>
    /// UTF-16 units of a piece or a WordArt run looked up, measured or positioned between two checkpoints: each distinct
    /// glyph costs time the first time a font meets it, more at larger sizes, so a long piece is worked a chunk at a time.
    /// </summary>
    internal const int GlyphsPerCheckpoint = 1_024;

    /// <summary>
    /// Chunks of at most <see cref="GlyphsPerCheckpoint"/> UTF-16 units, never splitting a surrogate pair: a chunk that
    /// would end in a high half gives it to the next chunk. A chunk of one unit is never shortened (a lone high half at
    /// the text's end stays where it is), so no chunk is empty and the loop ends.
    /// </summary>
    internal static IEnumerable<(int Start, int Length)> Chunks(string text)
    {
        for (var start = 0; start < text.Length;)
        {
            var length = Math.Min(GlyphsPerCheckpoint, text.Length - start);
            if (length > 1 && start + length < text.Length && char.IsHighSurrogate(text[start + length - 1])) length--;
            yield return (start, length);
            start += length;
        }
    }

    /// <summary>
    /// Whether a chunk shows that its piece is not valid UTF-16: Skia takes one unpaired surrogate to make the whole
    /// text invalid (no glyphs, width 0), and a chunk holding it gives no glyphs although it is not empty. Chunks never
    /// split a pair, so the text is valid exactly when every chunk is. The piece then gets what the whole-piece call
    /// would give, without the call: it would scan the whole piece in one step.
    /// </summary>
    private static bool Invalid(int length, ushort[] glyphs) => length > 0 && glyphs.Length == 0;

    /// <summary>
    /// A piece's width, bit for bit as <c>SKFont.MeasureText</c> gives it. A piece longer than one chunk is worked a
    /// chunk at a time, with a checkpoint before each chunk after the first: the chunk's glyphs are looked up, the ones
    /// not met before are measured (<see cref="GlyphAdvances"/>), and the chunk's advances are added in order. Skia sums
    /// the advances of the font's strike in order and then scales the sum; a font over 256 units is measured at its
    /// canonical 64-unit strike (SkFont::setupForAsPaths: no hinting, no embedded bitmaps), so the chunks sum that
    /// strike's advances in the same order and scale once. (The fonts here are not scaled or skewed.) A long piece that is
    /// not valid UTF-16 gives what the whole-piece call gives, width 0, without the call (a piece of one chunk makes it).
    /// </summary>
    internal static float Measure(SKFont font, string text, RenderContext ctx)
    {
        if (text.Length <= GlyphsPerCheckpoint) return font.MeasureText(text);
        var canonical = font.Size * font.Size > 256f * 256f;
        using var strike = canonical
            ? new SKFont(font.Typeface, 64)
            {
                Subpixel = true, Hinting = SKFontHinting.None, EmbeddedBitmaps = false, ForceAutoHinting = false,
                Edging = font.Edging == SKFontEdging.SubpixelAntialias ? SKFontEdging.Antialias : font.Edging,
                LinearMetrics = font.LinearMetrics, Embolden = font.Embolden, BaselineSnap = font.BaselineSnap,
            }
            : null;
        var advances = ctx.Advances;
        advances.Begin(strike ?? font);
        float sum = 0;
        var first = true;
        foreach (var (start, length) in Chunks(text))
        {
            if (!first) ctx.Checkpoint();
            first = false;
            ctx.PieceChunksMeasured++;
            var glyphs = font.GetGlyphs(text.AsSpan(start, length));
            if (Invalid(length, glyphs)) return 0;   // what MeasureText gives for such text
            advances.Learn(glyphs, ctx);
            foreach (var g in glyphs) sum += advances[g];
        }
        return canonical ? sum * (font.Size / 64f) : sum;
    }

    /// <summary>
    /// A piece's glyphs and their positions from a pen at (0, 0), bit for bit as <c>SKFont.GetGlyphs</c> and
    /// <c>GetGlyphPositions</c> give them for the whole piece. A piece longer than one chunk is worked a chunk at a time,
    /// with a checkpoint before each chunk after the first: the chunk's glyphs are looked up, the ones not met before are
    /// measured, and each glyph is placed where the running sum of the advances before it puts the pen, as Skia places
    /// them (horizontal text: the pen does not move down). A long piece that is not valid UTF-16 gives what the whole-piece
    /// calls give, no glyphs, without the calls (a piece of one chunk makes <c>GetGlyphs</c>, and <c>GetGlyphPositions</c>
    /// when it has glyphs).
    /// </summary>
    internal static (ushort[] Glyphs, SKPoint[] Positions) Glyphs(SKFont font, string text, RenderContext ctx)
    {
        (ushort[], SKPoint[]) Whole()
        {
            var whole = font.GetGlyphs(text);
            return (whole, whole.Length == 0 ? [] : font.GetGlyphPositions(whole, new SKPoint(0, 0)));
        }

        if (text.Length <= GlyphsPerCheckpoint) return Whole();
        var advances = ctx.Advances;
        advances.Begin(font);
        var glyphs = new List<ushort>(text.Length);
        var positions = new List<SKPoint>(text.Length);
        float x = 0;
        var first = true;
        foreach (var (start, length) in Chunks(text))
        {
            if (!first) ctx.Checkpoint();
            first = false;
            ctx.GlyphChunksPositioned++;
            var chunk = font.GetGlyphs(text.AsSpan(start, length));
            if (Invalid(length, chunk)) return ([], []);   // what GetGlyphs gives for such text
            advances.Learn(chunk, ctx);
            foreach (var g in chunk)
            {
                positions.Add(new SKPoint(x, 0));
                x += advances[g];
            }
            glyphs.AddRange(chunk);
        }
        return (glyphs.ToArray(), positions.ToArray());
    }

    /// <summary>
    /// A WordArt run's glyphs and their widths, looked up and measured a chunk at a time, with a checkpoint before each
    /// chunk after the first. A glyph's width is its own advance (scaled), whatever chunk it is measured in. A run that is
    /// not valid UTF-16 gives what the whole-run calls give, no glyphs, without the calls (a run of one chunk is looked
    /// up by that one call, which is the whole run's).
    /// </summary>
    internal static (ushort[] Ids, float[] Widths) LookUpAndMeasure(SKFont font, string text, RenderContext ctx)
    {
        var ids = new List<ushort>(text.Length);
        var widths = new List<float>(text.Length);
        var first = true;
        foreach (var (start, length) in Chunks(text))
        {
            if (!first) ctx.Checkpoint();
            first = false;
            ctx.WordArtChunksMeasured++;
            var chunk = font.GetGlyphs(text.AsSpan(start, length));
            if (Invalid(length, chunk)) return ([], []);   // what GetGlyphs gives for such text
            ids.AddRange(chunk);
            widths.AddRange(font.GetGlyphWidths(chunk));
        }
        return (ids.ToArray(), widths.ToArray());
    }

    private static SKFont MakeFont(TextStyle style, RenderContext ctx, List<SKFont> fonts, bool track = true)
    {
        var resolved = ctx.ResolveFont(style.Family, style.Bold, style.Italic);
        if (resolved.IsSubstitute)
            ctx.Warn(RenderWarning.FontSubstituted, $"Font '{resolved.RequestedFamily}' is not installed; using '{resolved.Typeface.FamilyName}'.");
        var font = new SKFont(resolved.Typeface, (float)style.Size) { Subpixel = true, Edging = SKFontEdging.Antialias };
        if (track) fonts.Add(font);
        return font;
    }

    /// <summary>Characters of a run laid out between two checkpoints.</summary>
    internal const int CharactersPerCheckpoint = 1_024;

    private static List<Word> Tokenize(Paragraph p, RenderContext ctx, List<SKFont> fonts)
    {
        var words = new List<Word>();
        var current = new Word();
        foreach (var run in p.Runs)
        {
            ctx.Checkpoint();
            ctx.RunsLaidOut++;
            var font = MakeFont(run.Style, ctx, fonts);
            var color = PageRenderer.Color(run.Style.Color);
            var (descent, spacing) = (font.Metrics.Descent, font.Spacing);
            var buffer = new System.Text.StringBuilder();

            void FlushPiece()
            {
                if (buffer.Length == 0) return;
                var s = buffer.ToString();
                current.Pieces.Add(new Piece(s, font, color, Measure(font, s, ctx), descent, spacing));
                buffer.Clear();
            }

            var chars = 0;
            foreach (var ch in run.Text)
            {
                if (++chars % CharactersPerCheckpoint == 0) ctx.Checkpoint();   // a long run is laid out in steps
                ctx.CharactersLaidOut++;
                if (ch is ' ' or '\t' or '\n' or '\r' or '\v')
                {
                    FlushPiece();
                    var forced = ch is '\n' or '\r' or '\v';
                    if (current.Pieces.Count > 0)
                    {
                        current.SpaceAfter = forced ? 0 : font.MeasureText(" ");
                        current.BreakAfter = forced;
                        words.Add(current);
                        current = new Word();
                    }
                    else if (words.Count > 0)
                    {
                        // Consecutive whitespace: widen the previous gap (spaces) or force the break.
                        if (forced) words[^1].BreakAfter = true; else words[^1].SpaceAfter += font.MeasureText(" ");
                    }
                }
                else buffer.Append(run.Style.AllCaps ? char.ToUpperInvariant(ch) : ch);   // display-only capitals
            }
            FlushPiece();
        }
        if (current.Pieces.Count > 0) words.Add(current);
        return words;
    }

    /// <summary>Words of a paragraph wrapped between two checkpoints.</summary>
    internal const int WordsPerCheckpoint = 1_024;

    private static IEnumerable<(List<Word> Line, bool IsLast)> Wrap(List<Word> words, float width, RenderContext ctx)
    {
        var line = new List<Word>();
        float used = 0;
        for (var i = 0; i < words.Count; i++)
        {
            if ((i + 1) % WordsPerCheckpoint == 0) ctx.Checkpoint();   // a long line is gathered in steps
            ctx.WordsWrapped++;
            var w = words[i];
            var gap = line.Count == 0 ? 0 : line[^1].SpaceAfter;
            if (line.Count > 0 && used + gap + w.Width > width)
            {
                yield return (line, false);
                line = [];
                used = 0;
                gap = 0;
            }
            line.Add(w);
            used += gap + w.Width;
            if (w.BreakAfter && i < words.Count - 1)
            {
                yield return (line, true);   // a forced break ends the line like a paragraph end: no justify
                line = [];
                used = 0;
            }
        }
        if (line.Count > 0) yield return (line, true);
    }

    private static void DrawLine(SKCanvas canvas, List<Word> line, bool isLast, TextAlignment align, float left, float width, float baseline, RenderContext ctx)
    {
        var gaps = line.Take(line.Count - 1).Sum(w => w.SpaceAfter);
        var natural = line.Sum(w => w.Width) + gaps;
        float x = align switch
        {
            TextAlignment.Center => left + (width - natural) / 2,
            TextAlignment.Right => left + width - natural,
            _ => left,
        };
        var extraPerGap = align == TextAlignment.Justify && !isLast && line.Count > 1
            ? Math.Max(0, (width - natural) / (line.Count - 1))
            : 0;

        using var paint = new SKPaint { IsAntialias = true };
        for (var i = 0; i < line.Count; i++)
        {
            foreach (var piece in line[i].Pieces)
            {
                paint.Color = piece.Color;
                DrawPiece(canvas, piece.Text, piece.Font, x, baseline, paint, ctx);
                x += piece.Width;
            }
            if (i < line.Count - 1) x += line[i].SpaceAfter + extraPerGap;
        }
    }
}

/// <summary>
/// The advances of the distinct glyphs a long piece has met, each measured once: a chunk asks the font only for the
/// glyphs the table has not met. (Asking for each chunk's glyphs would meet a glyph again in every chunk it appears in once
/// Skia's cache has let it go, and each meeting costs its time again: 60,000 distinct glyphs at 250 pt took over 600 s
/// that way.) Each entry is what the font's own calls use for that glyph: its strike's advance, scaled. One table per
/// context (<see cref="RenderContext.Advances"/>), reused from piece to piece: <see cref="Begin"/> forgets only the
/// glyphs the last piece met, so a long piece allocates for its own glyphs only.
/// </summary>
internal sealed class GlyphAdvances
{
    private readonly float[] _advance = new float[ushort.MaxValue + 1];
    private readonly bool[] _known = new bool[ushort.MaxValue + 1];
    private readonly List<ushort> _met = [];
    private readonly List<ushort> _missing = [];
    private SKFont? _font;

    /// <summary>The distinct glyphs met since <see cref="Begin"/>: what the next <see cref="Begin"/> forgets (tests).</summary>
    internal int Remembered => _met.Count;

    /// <summary>Starts a piece measured by <paramref name="font"/>: no glyph is known.</summary>
    public void Begin(SKFont font)
    {
        foreach (var g in _met) _known[g] = false;
        _met.Clear();
        _font = font;
    }

    public float this[ushort glyph] => _advance[glyph];

    /// <summary>Measures the glyphs of <paramref name="glyphs"/> not met since <see cref="Begin"/>.</summary>
    public void Learn(ushort[] glyphs, RenderContext ctx)
    {
        _missing.Clear();
        foreach (var g in glyphs)
            if (!_known[g])
            {
                _known[g] = true;
                _met.Add(g);   // forgotten at the next Begin, even if measuring fails
                _missing.Add(g);
            }
        if (_missing.Count == 0) return;
        var widths = _font!.GetGlyphWidths(_missing.ToArray());
        for (var i = 0; i < widths.Length; i++) _advance[_missing[i]] = widths[i];
        ctx.GlyphAdvancesMeasured += _missing.Count;
    }
}

/// <summary>WordArt's final glyph outlines and outline paint, built once and drawn for its shadow (if it has one) and for itself.</summary>
internal sealed class StretchedText(List<(SKPath Path, SKColor Color)> placed, SKPaint? outline, RenderContext ctx) : IDisposable
{
    /// <summary>The union of the outlines' bounds (empty when there is no ink); the outline's stroke lies outside it.</summary>
    public SKRect InkBounds { get; } = Union(placed);

    public float OutlineWidth => outline?.StrokeWidth ?? 0;

    public void Draw(SKCanvas canvas) => WordArt.Paint(canvas, placed, outline, ctx);

    public void Dispose()
    {
        foreach (var (path, _) in placed) path.Dispose();
        outline?.Dispose();
    }

    private static SKRect Union(List<(SKPath Path, SKColor Color)> placed)
    {
        var r = SKRect.Empty;
        foreach (var (path, _) in placed)
        {
            if (path.IsEmpty) continue;
            if (r.IsEmpty) r = path.Bounds; else r.Union(path.Bounds);
        }
        return r;
    }
}
