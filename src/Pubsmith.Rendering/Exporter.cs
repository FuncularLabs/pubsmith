using System.Globalization;
using Pubsmith.Core;
using SkiaSharp;

namespace Pubsmith.Rendering;

/// <summary>PNG and PDF output. Both go through <see cref="PageRenderer"/>.</summary>
public static class Exporter
{
    public const double MaxDpi = 2400;

    /// <summary>
    /// Largest bitmap rendered. One page's bitmap must stay under SkiaSharp's 2 GiB allocation: a US Letter page at
    /// <see cref="MaxDpi"/> (538.6 megapixels) failed to allocate, so Letter renders up to about 2,310 dpi.
    /// </summary>
    public const long MaxPixels = 500_000_000;

    /// <summary>The bitmap size a page renders to at <paramref name="dpi"/>; throws when it is impossible or over <see cref="MaxPixels"/>.</summary>
    public static (int Width, int Height) PixelSize(Page page, double dpi)
    {
        ArgumentNullException.ThrowIfNull(page);
        CheckPageSize(page);
        if (!(dpi > 0 && dpi <= MaxDpi)) throw new ArgumentOutOfRangeException(nameof(dpi), dpi, $"dpi must be in (0, {MaxDpi}].");
        double w = Math.Max(1, Math.Round(page.Width / 72 * dpi, MidpointRounding.AwayFromZero));
        double h = Math.Max(1, Math.Round(page.Height / 72 * dpi, MidpointRounding.AwayFromZero));
        if (w * h > MaxPixels)
            throw new ArgumentOutOfRangeException(nameof(page), $"A {page.Width.ToString(CultureInfo.InvariantCulture)} x {page.Height.ToString(CultureInfo.InvariantCulture)} pt page at {dpi.ToString(CultureInfo.InvariantCulture)} dpi is {w * h:0} pixels, over the {MaxPixels} limit; use a lower --dpi.");
        return ((int)w, (int)h);
    }

    private static void CheckPageSize(Page page)
    {
        if (!(double.IsFinite(page.Width) && page.Width > 0 && double.IsFinite(page.Height) && page.Height > 0))
            throw new ArgumentOutOfRangeException(nameof(page), $"Page size {page.Width.ToString(CultureInfo.InvariantCulture)} x {page.Height.ToString(CultureInfo.InvariantCulture)} pt is not a real page size.");
    }

    /// <summary>Renders a page to an opaque bitmap (white paper) at the given resolution.</summary>
    public static SKBitmap RenderBitmap(Page page, double dpi, RenderContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.ThrowIfDisposed();
        ctx.ThrowIfRefused();
        var (w, h) = PixelSize(page, dpi);
        ctx.CountBitmapPage((long)w * h);   // before the bitmap is allocated
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        var previous = ctx.Target;
        try
        {
            ctx.Target = RenderTarget.Bitmap;
            using var canvas = new SKCanvas(bmp);
            canvas.Clear(SKColors.White);
            canvas.Scale((float)(dpi / 72));
            PageRenderer.Render(canvas, page, ctx);
            return bmp;
        }
        catch
        {
            bmp.Dispose();
            throw;
        }
        finally { ctx.Target = previous; }
    }

    public static void ToPng(Page page, double dpi, RenderContext ctx, Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.ThrowIfDisposed();
        using var bmp = RenderBitmap(page, dpi, ctx);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        ctx.CountPngBytes(data.Size);   // before it is written
        data.SaveTo(output);
    }

    /// <summary>
    /// Writes every page as a vector PDF page whose MediaBox is the page size in points; fonts are embedded. The PDF is
    /// counted as it is written (the output need not seek) and refused past <see cref="RenderLimits.MaxPdfBytes"/>;
    /// on any exception the document is aborted, never closed, so the output holds no trailer and nothing written after.
    /// </summary>
    public static void ToPdf(PubsmithDocument document, RenderContext ctx, Stream output)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.ThrowIfDisposed();   // before anything is written to the caller's stream
        ctx.ThrowIfRefused();
        if (document.Pages.Count == 0) throw new ArgumentException("The document has no pages.", nameof(document));
        foreach (var page in document.Pages) CheckPageSize(page);
        ctx.CountPdf(document);   // pages and typefaces, before anything is written

        var previous = ctx.Target;
        var counted = new CountingStream(output);
        SKDocument? pdf = null;
        try
        {
            ctx.Target = RenderTarget.Pdf;
            ctx.PdfOutput = counted;
            pdf = SKDocument.CreatePdf(counted);
            for (var p = 0; p < document.Pages.Count; p++)
            {
                var page = document.Pages[p];
                var canvas = pdf.BeginPage((float)page.Width, (float)page.Height);
                PageRenderer.Render(canvas, page, ctx, p + 1);
                pdf.EndPage();
                ctx.CheckPdfBytes(p + 1);   // after the page's end; after the last, before the close
            }
            pdf.Close();
        }
        catch
        {
            pdf?.Abort();   // never closed: no fonts, no trailer
            throw;
        }
        finally
        {
            pdf?.Dispose();
            ctx.PdfOutput = null;
            ctx.Target = previous;
        }
    }
}
