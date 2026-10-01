using Pubsmith.Core;
using SkiaSharp;

namespace Pubsmith.Rendering;

/// <summary>
/// Draws a page onto any canvas whose units are points. The PNG and PDF exporters both call this, so what is
/// seen on screen and what is printed come from the same code.
/// </summary>
public static class PageRenderer
{
    public static void Render(SKCanvas canvas, Page page, RenderContext ctx)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.ThrowIfDisposed();

        foreach (var element in page.Elements)
        {
            // WordArt is built once and drawn for its shadow (if it has one), then itself, inside this element's step.
            using var stretched = element is TextElement { Fit: TextFit.Stretch } t ? TextRenderer.PrepareStretched(t, ctx) : null;
            if (element.Shadow is { } shadow) DrawShadow(canvas, element, shadow, ctx, stretched);
            canvas.Save();
            Rotate(canvas, element);
            DrawContent(canvas, element, ctx, stretched);
            canvas.Restore();
        }
    }

    // Clockwise-positive about the frame centre; Skia is clockwise for y-down.
    private static void Rotate(SKCanvas canvas, Element element)
    {
        if (element.Rotation != 0) canvas.RotateDegrees((float)element.Rotation, (float)element.Bounds.CenterX, (float)element.Bounds.CenterY);
    }

    private static void DrawContent(SKCanvas canvas, Element element, RenderContext ctx, StretchedText? stretched)
    {
        switch (element)
        {
            case ShapeElement s: DrawShape(canvas, s); break;
            case ImageElement i: DrawImage(canvas, i, ctx); break;
            case TextElement when stretched is not null: stretched.Draw(canvas); break;
            case TextElement t: TextRenderer.Draw(canvas, t, ctx); break;
        }
    }

    /// <summary>
    /// The element's silhouette in the shadow colour, offset in page coordinates: glyph shapes for text, the alpha
    /// outline for transparent pictures, fill + line for shapes. An opaque shape with no line, or a line at least 1.5
    /// device pixels wide, is drawn directly as its silhouette; any other element (a thin-lined shape included) is drawn
    /// through a layer that recolours it, bounded to what the element draws, so the layer is only as large as the
    /// element: a page-sized element still gets a page-sized layer.
    /// </summary>
    private static void DrawShadow(SKCanvas canvas, Element element, Shadow shadow, RenderContext ctx, StretchedText? stretched)
    {
        var c = shadow.Color;
        canvas.Save();
        canvas.Translate((float)shadow.OffsetX, (float)shadow.OffsetY);
        Rotate(canvas, element);
        if (element is not ShapeElement s || !TryDrawSilhouette(canvas, s, new SKColor(c.R, c.G, c.B, c.A), SilhouetteScale(canvas, ctx)))
        {
            using var filter = SKColorFilter.CreateBlendMode(new SKColor(c.R, c.G, c.B), SKBlendMode.SrcIn);
            using var layer = new SKPaint { ColorFilter = filter, Color = new SKColor(0, 0, 0, c.A) };   // layer alpha = shadow opacity
            canvas.SaveLayer(LayerBounds(element, stretched), layer);
            DrawContent(canvas, element, ctx, stretched);
            canvas.Restore();
        }
        canvas.Restore();
    }

    /// <summary>Device pixels per point a line is judged at: 300 dpi for a PDF, otherwise the canvas's own scale.</summary>
    internal static float SilhouetteScale(SKCanvas canvas, RenderContext ctx)
    {
        if (ctx.Target == RenderTarget.Pdf) return 300f / 72;
        var m = canvas.TotalMatrix;
        return (float)Math.Sqrt(Math.Abs(m.ScaleX * m.ScaleY - m.SkewX * m.SkewY));
    }

    /// <summary>
    /// Draws an opaque shape's shadow as one path in the shadow colour (no layer), when that matches the layered
    /// shadow: the fill (if any) and the line (if any) must be opaque, and a line at least 1.5 device pixels wide
    /// (a thinner line's anti-aliasing would differ). A fill of alpha 0 counts as no fill. Returns false otherwise.
    /// </summary>
    internal static bool TryDrawSilhouette(SKCanvas canvas, ShapeElement s, SKColor color, float scale)
    {
        if (s.Fill is { A: > 0 and < 255 }) return false;
        var hasFill = s.Fill is { A: 255 };
        var line = s.Stroke is { Width: > 0 } st ? st : null;
        if (line is not null && (line.Color.A != 255 || line.Width * scale < 1.5)) return false;
        var r = Rect(s.Bounds).Standardized;
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };
        if (line is null)
        {
            if (hasFill) Draw(canvas, s.Kind, r, paint);   // nothing drawn, nothing cast
            return true;
        }
        var w = (float)line.Width;
        if (!hasFill)
        {
            using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = w, Color = color };
            Draw(canvas, s.Kind, r, stroke);
            return true;
        }
        if (s.Kind != ShapeKind.Ellipse)
        {
            r.Inflate(w / 2, w / 2);   // fill and line together: the line's outer edge, square at the mitred corners
            canvas.DrawRect(r, paint);
            return true;
        }
        using var silhouette = EllipseSilhouette(r, w, scale);
        if (silhouette is null) return false;
        canvas.DrawPath(silhouette, paint);
        return true;
    }

    /// <summary>A filled ellipse with a line: the union of the fill and the line's outline, flattened at <paramref name="scale"/>.</summary>
    internal static SKPath? EllipseSilhouette(SKRect r, float lineWidth, float scale)
    {
        using var builder = new SKPathBuilder();
        builder.AddOval(r, SKPathDirection.Clockwise);
        using var oval = builder.Detach();
        using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = lineWidth };
        using var outline = stroke.GetFillPath(oval, scale);
        return outline is null ? null : oval.Op(outline, SKPathOp.Union);
    }

    /// <summary>
    /// What an element draws, in its own (unrotated) coordinates, for its shadow layer: the frame (sorted, since
    /// doc.json allows negative sizes) widened by twice the line plus 1 pt for shapes and pictures (mitred corners
    /// included); the frame itself for a text box (its text is clipped to it); WordArt's ink box widened the same way
    /// by its outline (path warps draw far outside the frame).
    /// </summary>
    internal static SKRect LayerBounds(Element element, StretchedText? stretched)
    {
        var r = Rect(element.Bounds).Standardized;
        float margin;
        switch (element)
        {
            case ShapeElement s: margin = 2 * (s.Stroke is { Width: > 0 } a ? (float)a.Width : 0) + 1; break;
            case ImageElement i: margin = 2 * (i.Stroke is { Width: > 0 } b ? (float)b.Width : 0) + 1; break;
            case TextElement when stretched is not null:
                r = stretched.InkBounds;
                margin = 2 * stretched.OutlineWidth + 1;
                break;
            default: return r;
        }
        if (r.IsEmpty) return r;
        r.Inflate(margin, margin);
        return r;
    }

    internal static SKRect Rect(Box b) => SKRect.Create((float)b.X, (float)b.Y, (float)b.Width, (float)b.Height);

    internal static SKColor Color(Rgba c) => new(c.R, c.G, c.B, c.A);

    private static void DrawShape(SKCanvas canvas, ShapeElement s)
    {
        var rect = Rect(s.Bounds);
        if (s.Fill is { } fill)
        {
            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = Color(fill) };
            Draw(canvas, s.Kind, rect, paint);
        }
        if (s.Stroke is { Width: > 0 } stroke)
        {
            using var paint = StrokePaint(stroke);
            Draw(canvas, s.Kind, rect, paint);
        }
    }

    private static void Draw(SKCanvas canvas, ShapeKind kind, SKRect rect, SKPaint paint)
    {
        if (kind == ShapeKind.Ellipse) canvas.DrawOval(rect, paint);
        else canvas.DrawRect(rect, paint);
    }

    private static SKPaint StrokePaint(Stroke stroke) =>
        new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)stroke.Width, Color = Color(stroke.Color) };

    private static void DrawImage(SKCanvas canvas, ImageElement img, RenderContext ctx)
    {
        var dst = Rect(img.Bounds);
        var path = ctx.ResolvePath(img.Source, out var refusal);
        if (path is null)
        {
            ctx.Warn(RenderWarning.ImageRefused, $"Image '{img.Source.Replace('\0', '?')}' was not opened: {refusal}.");
            DrawPlaceholder(canvas, dst);
            return;
        }
        // Read once per context (only a read failure is tried again); drawn from its decoded copy on a bitmap, from its
        // encoded image anywhere else (so a PDF embeds it at most once per distinct crop and passes a JPEG through).
        var picture = ctx.Pictures.Get(path);
        switch (picture.State)
        {
            case PictureState.Missing:
                ctx.Warn(RenderWarning.ImageMissing, $"Image '{img.Source}' not found (looked for '{path}').");
                DrawPlaceholder(canvas, dst);
                return;
            case PictureState.Unreadable:
                ctx.Warn(RenderWarning.ImageUnreadable, picture.ReadFailed ? $"Image '{img.Source}' could not be read." : $"Image '{img.Source}' could not be decoded.");
                DrawPlaceholder(canvas, dst);
                return;
            case PictureState.TooLarge:
                ctx.Warn(RenderWarning.ImageTooLarge, $"Image '{img.Source}' declares {picture.Pixels} pixels, more than the {ctx.Pictures.MaxPixels} limit; it was not decoded.");
                DrawPlaceholder(canvas, dst);
                return;
        }
        var image = (ctx.Target == RenderTarget.Bitmap ? ctx.Pictures.Decoded(picture) : null) ?? picture.Encoded!;

        var c = img.Crop ?? new Crop(0, 0, 0, 0);
        var src = new SKRect(
            (float)(c.Left * image.Width), (float)(c.Top * image.Height),
            (float)((1 - c.Right) * image.Width), (float)((1 - c.Bottom) * image.Height));
        using var paint = new SKPaint { IsAntialias = true };
        var oval = img.Mask == ShapeKind.Ellipse;
        canvas.Save();
        if (oval)
        {
            using var builder = new SKPathBuilder();
            builder.AddOval(dst, SKPathDirection.Clockwise);
            using var clip = builder.Detach();
            canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);
        }
        canvas.DrawImage(image, src, dst, new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
        canvas.Restore();

        if (img.Stroke is { Width: > 0 } stroke)
        {
            using var sp = StrokePaint(stroke);
            if (oval) canvas.DrawOval(dst, sp); else canvas.DrawRect(dst, sp);
        }
    }

    private static void DrawPlaceholder(SKCanvas canvas, SKRect r)
    {
        using var fill = new SKPaint { Color = new SKColor(0xE0, 0xE0, 0xE0), Style = SKPaintStyle.Fill };
        using var line = new SKPaint { Color = new SKColor(0xC0, 0x00, 0x00), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        canvas.DrawRect(r, fill);
        canvas.DrawRect(r, line);
        canvas.DrawLine(r.Left, r.Top, r.Right, r.Bottom, line);
        canvas.DrawLine(r.Right, r.Top, r.Left, r.Bottom, line);
    }
}
