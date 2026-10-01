using Pubsmith.Core;
using SkiaSharp;

namespace Pubsmith.Rendering.Tests;

// WI-005 AC4: a RenderContext owns its cached pictures and releases them when disposed; it cannot be used afterwards.
public sealed class RenderContextTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pubsmith-context-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Dispose_ReleasesThePictures_AndLaterRenderingThrows()
    {
        using (var bmp = new SKBitmap(40, 30))
        {
            bmp.Erase(SKColors.Red);
            using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(_dir, "a.png"), data.ToArray());
        }
        var ctx = new RenderContext(_dir);
        using (Exporter.RenderBitmap(new Page(100, 100) { Elements = [new ImageElement { Bounds = new Box(10, 10, 40, 30), Source = "missing.png" }] }, 72, ctx)) { }
        using (Exporter.RenderBitmap(new Page(100, 100) { Elements = [new ImageElement { Bounds = new Box(10, 10, 40, 30), Source = "a.png" }] }, 72, ctx)) { }
        Assert.Equal(1, ctx.Pictures.Decodes);
        var picture = ctx.Pictures.Get(Path.Combine(_dir, "a.png"));
        var (encoded, decoded) = (picture.Encoded!, picture.Decoded!);

        ctx.Dispose();

        Assert.True(ctx.Pictures.IsDisposed);
        Assert.Equal(IntPtr.Zero, encoded.Handle);   // the native images are released
        Assert.Equal(IntPtr.Zero, decoded.Handle);
        Assert.Single(ctx.Warnings);   // still readable
        // A page with no pictures: the guard is the renderer's, not the picture cache's.
        var plain = new Page(100, 100) { Elements = [new ShapeElement { Bounds = new Box(10, 10, 40, 30), Fill = new Rgba(0, 0, 0) }] };
        using (var bmp = new SKBitmap(100, 100))
        using (var canvas = new SKCanvas(bmp))
            Assert.Throws<ObjectDisposedException>(() => PageRenderer.Render(canvas, plain, ctx));
        Assert.Throws<ObjectDisposedException>(() => Exporter.RenderBitmap(plain, 72, ctx));
        using (var png = new MemoryStream())
        {
            Assert.Throws<ObjectDisposedException>(() => Exporter.ToPng(plain, 72, ctx, png));
            Assert.Equal(0, png.Length);
        }
        using (var pdf = new MemoryStream())
        {
            Assert.Throws<ObjectDisposedException>(() => Exporter.ToPdf(new PubsmithDocument { Pages = [plain] }, ctx, pdf));
            Assert.Equal(0, pdf.Length);   // nothing written before the check
        }
        ctx.Dispose();   // twice is harmless
    }

    [Fact]
    public void TheOutputMark_IsClearedWhenAnExporterReturnsOrThrows()
    {
        // While an exporter draws, the context knows its canvas is a bitmap or a PDF; afterwards it is a caller's own
        // canvas again, however the exporter ended. A null element (a caller's bug) is used only as something that
        // throws. The same drawing call that throws has first drawn something the way only the exporter's mark draws
        // it, so the mark was set when the throw came: the bitmap decodes a picture, which only a bitmap-marked context
        // does; the PDF draws a ½ pt line's shadow as a silhouette, judged at the PDF's 300 dpi (at the PDF canvas's own
        // scale it would be a layer, which SkPDF writes as images when drawn), after a picture it writes when drawn.
        using (var bmp = new SKBitmap(40, 30))
        {
            bmp.Erase(SKColors.Red);
            using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(_dir, "a.png"), data.ToArray());
        }
        var ctx = new RenderContext(_dir);
        var plain = new Page(100, 100) { Elements = [new ShapeElement { Bounds = new Box(10, 10, 40, 30), Fill = new Rgba(0, 0, 0) }] };
        var picture = new ImageElement { Bounds = new Box(10, 10, 40, 30), Source = "a.png" };
        var thin = new ShapeElement
        {
            Bounds = new Box(20, 60, 60, 30), Fill = new Rgba(250, 220, 0), Stroke = new Stroke(new Rgba(0, 0, 0), 0.5), Shadow = new Shadow(new Rgba(0, 0, 0), 4, 4),
        };

        using (Exporter.RenderBitmap(plain, 72, ctx)) { }
        Assert.Equal(RenderTarget.Canvas, ctx.Target);
        using (var pdf = new MemoryStream()) Exporter.ToPdf(new PubsmithDocument { Pages = [plain] }, ctx, pdf);
        Assert.Equal(RenderTarget.Canvas, ctx.Target);

        Assert.Throws<NullReferenceException>(() => Exporter.RenderBitmap(new Page(100, 100) { Elements = [picture, null!] }, 72, ctx));
        Assert.Equal(1, ctx.Pictures.Decodes);   // the picture before the null element was drawn bitmap-marked
        Assert.Equal(RenderTarget.Canvas, ctx.Target);

        var page = new PubsmithDocument { Pages = [new Page(100, 100) { Elements = [picture, thin, null!] }] };
        using (var pdf = new MemoryStream())
        {
            Assert.Throws<NullReferenceException>(() => Exporter.ToPdf(page, ctx, pdf));
            // Before the throw, this page's drawing wrote its picture and drew the shadow PDF-marked (no image of its own).
            Assert.Equal([(40, 30)], PdfImages.In(pdf.ToArray()).Select(i => (i.Width, i.Height)));
        }
        Assert.Equal(RenderTarget.Canvas, ctx.Target);
    }
}
