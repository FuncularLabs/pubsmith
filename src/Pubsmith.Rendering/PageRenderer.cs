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

        foreach (var element in page.Elements)
        {
            if (element.Shadow is { } shadow) DrawShadow(canvas, element, shadow, ctx);
            DrawElement(canvas, element, ctx);
        }
    }

    private static void DrawElement(SKCanvas canvas, Element element, RenderContext ctx)
    {
        canvas.Save();
        if (element.Rotation != 0)   // clockwise-positive about the frame centre; Skia is clockwise for y-down
            canvas.RotateDegrees((float)element.Rotation, (float)element.Bounds.CenterX, (float)element.Bounds.CenterY);
        switch (element)
        {
            case ShapeElement s: DrawShape(canvas, s); break;
            case ImageElement i: DrawImage(canvas, i, ctx); break;
            case TextElement t: TextRenderer.Draw(canvas, t, ctx); break;
        }
        canvas.Restore();
    }

    /// <summary>
    /// The element drawn into a layer whose colour filter replaces every pixel's colour with the shadow colour
    /// (keeping its alpha), offset in page coordinates. So the shadow is the element's exact silhouette: glyph
    /// shapes for text, the alpha outline for transparent pictures, fill + line for shapes.
    /// </summary>
    private static void DrawShadow(SKCanvas canvas, Element element, Shadow shadow, RenderContext ctx)
    {
        var c = shadow.Color;
        using var filter = SKColorFilter.CreateBlendMode(new SKColor(c.R, c.G, c.B), SKBlendMode.SrcIn);
        using var layer = new SKPaint
        {
            ColorFilter = filter,
            Color = new SKColor(0, 0, 0, c.A),   // layer alpha = shadow opacity
        };
        canvas.Save();
        canvas.Translate((float)shadow.OffsetX, (float)shadow.OffsetY);
        canvas.SaveLayer(layer);
        DrawElement(canvas, element, ctx);
        canvas.Restore();
        canvas.Restore();
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
        if (!File.Exists(path))
        {
            ctx.Warn(RenderWarning.ImageMissing, $"Image '{img.Source}' not found (looked for '{path}').");
            DrawPlaceholder(canvas, dst);
            return;
        }

        using var image = SKImage.FromEncodedData(path);
        if (image is null)
        {
            ctx.Warn(RenderWarning.ImageUnreadable, $"Image '{img.Source}' could not be decoded.");
            DrawPlaceholder(canvas, dst);
            return;
        }

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
