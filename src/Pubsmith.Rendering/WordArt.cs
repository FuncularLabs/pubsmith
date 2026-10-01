using Pubsmith.Core;
using SkiaSharp;

namespace Pubsmith.Rendering;

/// <summary>One glyph outline with its baseline at y = 0 and its advance starting at x = 0.</summary>
internal sealed record Glyph(SKPath Path, float Advance, SKColor Color);

/// <summary>
/// WordArt drawing: stretched text, envelope warps (text bent between a top and a bottom curve) and path warps
/// (glyphs placed along an ellipse, scaled so the text fills the path). Default amounts follow the standard
/// preset text shapes (e.g. can-down 14.3 %, slant 55.6 %, inflate/deflate 18.75 %, wave 12.5 %).
/// </summary>
internal static class WordArt
{
    /// <summary>
    /// The glyphs' final outlines, warped, scaled and placed in the target frame, ready to paint. Built once per
    /// element, so the shadow and the element are drawn from the same paths. The caller disposes them.
    /// </summary>
    public static List<(SKPath Path, SKColor Color)> Place(IReadOnlyList<Glyph> glyphs, SKRect target, TextWarp? warp)
    {
        var kind = warp?.Kind ?? WarpKind.None;
        return kind is WarpKind.ArchUp or WarpKind.ArchDown or WarpKind.Circle or WarpKind.Button
            ? OnPath(glyphs, target, kind)
            : Envelope(glyphs, target, kind, warp?.Adjust);
    }

    public static void Paint(SKCanvas canvas, IEnumerable<(SKPath Path, SKColor Color)> placed, SKPaint? outline)
    {
        foreach (var (path, color) in placed)
        {
            using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };
            canvas.DrawPath(path, fill);
            if (outline is not null) canvas.DrawPath(path, outline);
        }
    }

    // ---------- envelopes ----------

    private static List<(SKPath, SKColor)> Envelope(IReadOnlyList<Glyph> glyphs, SKRect t, WarpKind kind, double? adjust)
    {
        var bounds = SKRect.Empty;
        var x = 0f;
        var laid = new List<(SKPath Path, SKColor Color)>();
        var result = new List<(SKPath, SKColor)>();
        try
        {
            foreach (var g in glyphs)
            {
                var p = new SKPath(g.Path);
                p.Transform(SKMatrix.CreateTranslation(x, 0));
                if (!p.IsEmpty) bounds.Union(p.TightBounds);
                laid.Add((p, g.Color));
                x += g.Advance;
            }
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return result;
            var (top, bottom) = Curves(kind, adjust);
            SKPoint Map(SKPoint p)
            {
                var u = (p.X - bounds.Left) / bounds.Width;
                var v = (p.Y - bounds.Top) / bounds.Height;
                var tu = top(u); var bu = bottom(u);
                return new SKPoint(t.Left + u * t.Width, t.Top + (float)(tu + v * (bu - tu)) * t.Height);
            }
            foreach (var (p, color) in laid) result.Add((kind == WarpKind.None ? Linear(p, Map) : Bend(p, Map), color));
            return result;
        }
        catch
        {
            foreach (var (p, _) in result) p.Dispose();
            throw;
        }
        finally { foreach (var (p, _) in laid) p.Dispose(); }
    }

    /// <summary>Top and bottom curves in unit coordinates (0 = frame top, 1 = frame bottom) for each envelope.</summary>
    internal static (Func<float, double> Top, Func<float, double> Bottom) Curves(WarpKind kind, double? adjust)
    {
        double A(double d) => adjust ?? d;
        double Arc(float u) => Math.Sin(Math.PI * u);           // 0 at the ends, 1 in the middle
        double Vee(float u) => Math.Abs(2 * u - 1);             // 1 at the ends, 0 in the middle
        switch (kind)
        {
            case WarpKind.CanDown: { var a = A(0.14286); return (u => a * Arc(u), u => 1 - a + a * Arc(u)); }
            case WarpKind.CanUp: { var a = A(0.14286); return (u => a - a * Arc(u), u => 1 - a * Arc(u)); }
            case WarpKind.SlantUp: { var a = A(0.55556); return (u => (1 - u) * (1 - a), u => (1 - u) * (1 - a) + a); }
            case WarpKind.SlantDown: { var a = A(0.55556); return (u => u * (1 - a), u => u * (1 - a) + a); }
            case WarpKind.CurveUp: { var a = A(0.45977); return (u => a * Math.Cos(Math.PI * u / 2), u => a * Math.Cos(Math.PI * u / 2) + 1 - a); }
            case WarpKind.CurveDown: { var a = A(0.45977); return (u => a * Math.Sin(Math.PI * u / 2), u => a * Math.Sin(Math.PI * u / 2) + 1 - a); }
            case WarpKind.Inflate: { var a = A(0.1875); return (u => a * (1 - Arc(u)), u => 1 - a * (1 - Arc(u))); }
            case WarpKind.Deflate: { var a = A(0.1875); return (u => a * Arc(u), u => 1 - a * Arc(u)); }
            case WarpKind.InflateTop: { var a = A(0.1875); return (u => a * (1 - Arc(u)), _ => 1); }
            case WarpKind.InflateBottom: { var a = A(0.1875); return (_ => 0, u => 1 - a * (1 - Arc(u))); }
            case WarpKind.DeflateTop: { var a = A(0.1875); return (u => a * Arc(u), _ => 1); }
            case WarpKind.DeflateBottom: { var a = A(0.1875); return (_ => 0, u => 1 - a * Arc(u)); }
            case WarpKind.Wave1: { var a = A(0.125); return (u => a * (1 + Math.Sin(2 * Math.PI * u)), u => a * (1 + Math.Sin(2 * Math.PI * u)) + 1 - 2 * a); }
            case WarpKind.Wave2: { var a = A(0.125); return (u => a * (1 - Math.Sin(2 * Math.PI * u)), u => a * (1 - Math.Sin(2 * Math.PI * u)) + 1 - 2 * a); }
            case WarpKind.Triangle: { var a = A(0.5); return (u => a * Vee(u), _ => 1); }
            case WarpKind.TriangleInverted: { var a = A(0.5); return (_ => 0, u => 1 - a * Vee(u)); }
            case WarpKind.Chevron: { var a = A(0.25); return (u => a * Vee(u), u => 1 - a + a * Vee(u)); }
            case WarpKind.ChevronInverted: { var a = A(0.25); return (u => a * (1 - Vee(u)), u => 1 - a * Vee(u)); }
            default: return (_ => 0, _ => 1);
        }
    }

    private static SKPath Linear(SKPath src, Func<SKPoint, SKPoint> map)
    {
        // A pure scale/translate: transform exactly, keeping curves as curves.
        var a = map(new SKPoint(0, 0)); var b = map(new SKPoint(1, 1));
        var copy = new SKPath(src);
        copy.Transform(new SKMatrix(b.X - a.X, 0, a.X, 0, b.Y - a.Y, a.Y, 0, 0, 1));
        return copy;
    }

    /// <summary>Maps every point of the path through a non-linear function, flattening curves into short lines.</summary>
    internal static SKPath Bend(SKPath src, Func<SKPoint, SKPoint> map, int steps = 8)
    {
        using var dst = new SKPathBuilder { FillType = src.FillType };
        using var it = src.CreateRawIterator();
        var pts = new SKPoint[4];
        SKPathVerb verb;
        while ((verb = it.Next(pts)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move: dst.MoveTo(map(pts[0])); break;
                case SKPathVerb.Line: LineTo(dst, map, pts[0], pts[1], 4); break;
                case SKPathVerb.Quad:
                    for (var i = 1; i <= steps; i++) { var t = i / (float)steps; dst.LineTo(map(Quad(pts[0], pts[1], pts[2], t))); }
                    break;
                case SKPathVerb.Conic:
                    {
                        var w = it.ConicWeight();
                        for (var i = 1; i <= steps; i++) { var t = i / (float)steps; dst.LineTo(map(Conic(pts[0], pts[1], pts[2], w, t))); }
                        break;
                    }
                case SKPathVerb.Cubic:
                    for (var i = 1; i <= steps; i++) { var t = i / (float)steps; dst.LineTo(map(Cubic(pts[0], pts[1], pts[2], pts[3], t))); }
                    break;
                case SKPathVerb.Close: dst.Close(); break;
            }
        }
        return dst.Detach();
    }

    // Straight edges are subdivided too: under a curved envelope a long straight stroke must bend.
    private static void LineTo(SKPathBuilder dst, Func<SKPoint, SKPoint> map, SKPoint a, SKPoint b, int steps)
    {
        for (var i = 1; i <= steps; i++) { var t = i / (float)steps; dst.LineTo(map(new SKPoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t))); }
    }

    private static SKPoint Quad(SKPoint a, SKPoint b, SKPoint c, float t)
    {
        var u = 1 - t;
        return new SKPoint(u * u * a.X + 2 * u * t * b.X + t * t * c.X, u * u * a.Y + 2 * u * t * b.Y + t * t * c.Y);
    }

    private static SKPoint Conic(SKPoint a, SKPoint b, SKPoint c, float w, float t)
    {
        var u = 1 - t;
        var d = u * u + 2 * w * u * t + t * t;
        return new SKPoint((u * u * a.X + 2 * w * u * t * b.X + t * t * c.X) / d, (u * u * a.Y + 2 * w * u * t * b.Y + t * t * c.Y) / d);
    }

    private static SKPoint Cubic(SKPoint a, SKPoint b, SKPoint c, SKPoint d, float t)
    {
        var u = 1 - t;
        return new SKPoint(u * u * u * a.X + 3 * u * u * t * b.X + 3 * u * t * t * c.X + t * t * t * d.X,
            u * u * u * a.Y + 3 * u * u * t * b.Y + 3 * u * t * t * c.Y + t * t * t * d.Y);
    }

    // ---------- glyphs along an ellipse ----------

    private static List<(SKPath, SKColor)> OnPath(IReadOnlyList<Glyph> glyphs, SKRect t, WarpKind kind)
    {
        var result = new List<(SKPath, SKColor)>();
        var advance = glyphs.Sum(g => g.Advance);
        if (advance <= 0) return result;

        // As Publisher draws them: the baseline runs on the ellipse inscribed in the frame (glyphs point outward,
        // or inward for arch-down), and the text is scaled so its advance fills the path length.
        using var path = Arc(t, kind);
        using (var measure = new SKPathMeasure(path))
        {
            var scale = measure.Length / advance;
            var x = 0f;
            foreach (var g in glyphs)
            {
                var mid = (x + g.Advance / 2) * scale;
                x += g.Advance;
                if (g.Path.IsEmpty || !measure.GetPositionAndTangent(Math.Min(mid, measure.Length), out var pos, out var tan)) continue;
                var angle = (float)(Math.Atan2(tan.Y, tan.X) * 180 / Math.PI);
                var m = SKMatrix.CreateTranslation(-g.Advance / 2, 0)
                    .PostConcat(SKMatrix.CreateScale(scale, scale))
                    .PostConcat(SKMatrix.CreateRotationDegrees(angle))
                    .PostConcat(SKMatrix.CreateTranslation(pos.X, pos.Y));
                var p = new SKPath(g.Path);
                p.Transform(m);
                result.Add((p, g.Color));
            }
        }
        return result;
    }

    /// <summary>Arch up / button: upper half, left to right. Arch down: lower half, left to right. Circle: from the left, clockwise.</summary>
    private static SKPath Arc(SKRect t, WarpKind kind)
    {
        using var p = new SKPathBuilder();
        switch (kind)
        {
            case WarpKind.ArchDown: p.AddArc(t, 180, -180); break;
            case WarpKind.Circle: p.AddArc(t, 180, 359.9f); break;
            default: p.AddArc(t, 180, 180); break;
        }
        return p.Detach();
    }
}
