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
    private sealed record Piece(string Text, SKFont Font, SKColor Color, float Width);

    private sealed class Word
    {
        public List<Piece> Pieces { get; } = [];
        public float Width => Pieces.Sum(p => p.Width);
        public float SpaceAfter { get; set; }     // width of the space that followed this word (0 at line end)
        public bool BreakAfter { get; set; }      // forced line break after this word
    }

    public static void Draw(SKCanvas canvas, TextElement t, RenderContext ctx)
    {
        if (t.Fit == TextFit.Stretch) { DrawStretched(canvas, t, ctx); return; }
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
                y += (float)p.SpaceBefore;
                var words = Tokenize(p, ctx, fonts);
                if (words.Count == 0)
                {
                    // An empty paragraph takes the height of its last run (the importer keeps the paragraph
                    // mark's formatting as an empty run); a paragraph with no runs at all uses the default style.
                    using var f = MakeFont(p.Runs.Count > 0 ? p.Runs[^1].Style : new TextStyle(), ctx, fonts, track: false);
                    y += p.ExactLineSpacing is { } exactEmpty ? (float)exactEmpty : f.Spacing * (float)p.LineSpacing;
                }
                foreach (var (line, isLast) in Wrap(words, width))
                {
                    var fontsInLine = line.SelectMany(w => w.Pieces).Select(pc => pc.Font).ToList();
                    var descent = fontsInLine.Max(f => f.Metrics.Descent);
                    var lineHeight = p.ExactLineSpacing is { } exact ? (float)exact : fontsInLine.Max(f => f.Spacing) * (float)p.LineSpacing;
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
                DrawLine(canvas, line, isLast, align, left, width, baseline + shift);
        }
        finally
        {
            canvas.Restore();
            foreach (var f in fonts) f.Dispose();
        }
    }

    /// <summary>
    /// WordArt-style: all text on one line as glyph outlines, scaled (non-uniformly) so its ink fills the inner
    /// frame exactly; optional outline stroked on top. Forced breaks are drawn as spaces.
    /// </summary>
    private static void DrawStretched(SKCanvas canvas, TextElement t, RenderContext ctx)
    {
        var fonts = new List<SKFont>();
        var glyphs = new List<Glyph>();
        try
        {
            foreach (var run in t.Paragraphs.SelectMany(p => p.Runs))
            {
                var font = MakeFont(run.Style, ctx, fonts);
                var text = run.Text.Replace('\n', ' ');
                if (run.Style.AllCaps) text = text.ToUpperInvariant();
                var ids = font.GetGlyphs(text);
                var widths = font.GetGlyphWidths(ids);
                var color = PageRenderer.Color(run.Style.Color);
                for (var i = 0; i < ids.Length; i++)
                    glyphs.Add(new Glyph(font.GetGlyphPath(ids[i]) ?? new SKPath(), widths[i], color));
            }
            var b = t.Bounds; var ins = t.Insets;
            var target = SKRect.Create((float)(b.X + ins.Left), (float)(b.Y + ins.Top), (float)(b.Width - ins.Left - ins.Right), (float)(b.Height - ins.Top - ins.Bottom));
            using var line = t.Outline is { Width: > 0 } o
                ? new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)o.Width, Color = PageRenderer.Color(o.Color), StrokeJoin = SKStrokeJoin.Round }
                : null;
            WordArt.Draw(canvas, glyphs, target, t.Warp, line);
        }
        finally
        {
            foreach (var g in glyphs) g.Path.Dispose();
            foreach (var f in fonts) f.Dispose();
        }
    }

    private static SKFont MakeFont(TextStyle style, RenderContext ctx, List<SKFont> fonts, bool track = true)
    {
        var resolved = ctx.Fonts.Resolve(style.Family, style.Bold, style.Italic);
        if (resolved.IsSubstitute)
            ctx.Warn(RenderWarning.FontSubstituted, $"Font '{resolved.RequestedFamily}' is not installed; using '{resolved.Typeface.FamilyName}'.");
        var font = new SKFont(resolved.Typeface, (float)style.Size) { Subpixel = true, Edging = SKFontEdging.Antialias };
        if (track) fonts.Add(font);
        return font;
    }

    private static List<Word> Tokenize(Paragraph p, RenderContext ctx, List<SKFont> fonts)
    {
        var words = new List<Word>();
        var current = new Word();
        foreach (var run in p.Runs)
        {
            var font = MakeFont(run.Style, ctx, fonts);
            var color = PageRenderer.Color(run.Style.Color);
            var buffer = new System.Text.StringBuilder();

            void FlushPiece()
            {
                if (buffer.Length == 0) return;
                var s = buffer.ToString();
                current.Pieces.Add(new Piece(s, font, color, font.MeasureText(s)));
                buffer.Clear();
            }

            foreach (var ch in run.Text)
            {
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

    private static IEnumerable<(List<Word> Line, bool IsLast)> Wrap(List<Word> words, float width)
    {
        var line = new List<Word>();
        float used = 0;
        for (var i = 0; i < words.Count; i++)
        {
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

    private static void DrawLine(SKCanvas canvas, List<Word> line, bool isLast, TextAlignment align, float left, float width, float baseline)
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
                canvas.DrawText(piece.Text, x, baseline, SKTextAlign.Left, piece.Font, paint);
                x += piece.Width;
            }
            if (i < line.Count - 1) x += line[i].SpaceAfter + extraPerGap;
        }
    }
}
