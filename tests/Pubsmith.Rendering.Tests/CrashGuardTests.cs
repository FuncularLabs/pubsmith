using Xunit.Abstractions;
using Pubsmith.Core;
using SkiaSharp;
using static Pubsmith.Rendering.Tests.JpegBytes;

namespace Pubsmith.Rendering.Tests;

// WI-006 AC4: sizes that would make one allocation too large are refused before they are allocated: a picture that
// counts as decoded in a PDF, over its cap; a PDF shadow layer over its cap; a picture file over its size; the font
// cache; a bitmap.
public sealed class CrashGuardTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pubsmith-guards-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly RenderLimits Open = RenderLimits.Default with
    {
        MaxPages = int.MaxValue, MaxPngPixels = long.MaxValue, MaxPdfTypefaces = int.MaxValue, MaxFontNames = int.MaxValue,
        MaxTextBoxCharacters = int.MaxValue, MaxTextBoxRuns = int.MaxValue, MaxWordArtGlyphs = int.MaxValue,
        MaxDrawTime = TimeSpan.MaxValue, MaxPdfBytes = long.MaxValue, MaxPngBytes = long.MaxValue,
    };

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    private static byte[] Pdf(Element element, RenderContext ctx)
    {
        using var s = new MemoryStream();
        Exporter.ToPdf(new PubsmithDocument { Pages = [new Page(200, 200) { Elements = [element] }] }, ctx, s);
        return s.ToArray();
    }

    private static byte[] Png(SKColor color, int w = 48, int h = 32)
    {
        using var bmp = new SKBitmap(w, h);
        bmp.Erase(color);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // ---------- a picture that counts as decoded in a PDF, over its cap ----------

    public static TheoryData<string, byte[], string, bool> Pictures()
    {
        var j = Encode(1);                 // JFIF, IDs 1, 2, 3
        var y = Without(j, 0xE0);          // no JFIF: IDs 1, 2, 3 make it YCbCr
        var rgb = SetIds(y, 0x52, 0x47, 0x42);
        var sof = Sof(y);
        var sofCode = sof.Code;
        var sofSeg = y[sof.At..(sof.At + 2 + sof.Length)];
        var a0 = Adobe(0);
        byte[] Be(int v) => [(byte)(v >> 8), (byte)v];
        var gray = Encode(2, gray: true);
        return new()
        {
            // placements and kinds that count as decoded
            { "PNG", Png(SKColors.Teal), "plain", false },
            { "JFIF, cropped", j, "cropped", false },
            { "JFIF, turned (EXIF 6)", InsertAfter(j, 0xE0, Exif(6)), "plain", false },
            { "JFIF, mirrored (EXIF 2)", InsertAfter(j, 0xE0, Exif(2)), "plain", false },
            { "CMYK", Fixture("cmyk.jpg"), "plain", false },
            { "JFIF, shadowed", j, "shadowed", false },
            { "lossless (SOF3), IDs 1 2 3, no JFIF or Adobe", SetSofCode(y, 0xC3), "plain", false },
            { "arithmetic (SOF9), IDs 1 2 3", SetSofCode(y, 0xC9), "plain", false },
            { "arithmetic (SOF11), IDs 1 2 3", SetSofCode(y, 0xCB), "plain", false },
            { "12-bit SOF1", SetPrecision(SetSofCode(y, 0xC1), 12), "plain", false },
            { "two SOFs", InsertBefore(y, sofCode, sofSeg), "plain", false },
            { "a stuffed 00 sized to skip an Adobe 0 marker, IDs 1 2 3", InsertBefore(y, 0xDB, [0xFF, 0x00, .. Be(a0.Length + 2), .. a0]), "plain", false },
            { "Adobe 0, IDs 1 2 3", InsertBefore(y, 0xDB, a0), "plain", false },
            { "IDs R G B", rgb, "plain", false },
            { "Adobe 1 then Adobe 0", InsertBefore(y, 0xDB, [.. Adobe(1), .. a0]), "plain", false },
            { "Adobe 0 after the SOF, IDs 1 2 3", InsertAfter(y, sofCode, a0), "plain", false },
            { "a JFIF of 13 data bytes before an Adobe 0 marker, IDs 1 2 3", InsertBefore(y, 0xDB, [.. Seg(0xE0, [.. "JFIF\0"u8, 1, 1, 0, 0, 1, 0, 1, 0]), .. a0]), "plain", false },
            { "a JFIF of 13 data bytes, IDs R G B", InsertBefore(rgb, 0xDB, Seg(0xE0, [.. "JFIF\0"u8, 1, 1, 0, 0, 1, 0, 1, 0])), "plain", false },
            { "an APP0 JFIFX, IDs R G B", InsertBefore(rgb, 0xDB, Seg(0xE0, [.. "JFIFX"u8, 1, 1, 0, 0, 1, 0, 1, 0, 0])), "plain", false },
            { "an Adobe of 10 data bytes, IDs R G B", InsertBefore(rgb, 0xDB, Seg(0xEE, [.. "Adobe"u8, 0, 100, 0, 0, 0])), "plain", false },
            { "an Adobe of 11 data bytes, IDs R G B", InsertBefore(rgb, 0xDB, Seg(0xEE, [.. "Adobe"u8, 0, 100, 0, 0, 0, 0])), "plain", false },
            { "an 8-byte Adobe, IDs R G B", InsertBefore(rgb, 0xDB, Seg(0xEE, [.. "Adobe"u8, 0, 100, 0])), "plain", false },
            { "Adobe version 0x0164, transform 0, IDs 1 2 3", InsertBefore(y, 0xDB, Adobe(0, version: 0x0164)), "plain", false },
            { "a TEM before an Adobe 0 marker of length 65,516", InsertBefore(y, 0xDB, [0xFF, 0x01, .. Adobe(0, padding: 65_516 - 14)]), "plain", false },
            { "an RST0 before an Adobe 0 marker of length 65,516", InsertBefore(y, 0xDB, [0xFF, 0xD0, .. Adobe(0, padding: 65_516 - 14)]), "plain", false },
            { "a fill byte before an Adobe 0 marker of length 61,165", InsertBefore(y, 0xDB, [0xFF, .. Adobe(0, padding: 61_165 - 14)]), "plain", false },
            { "a TEM before the tables", InsertBefore(y, 0xDB, [0xFF, 0x01]), "plain", false },
            { "a DAC before the SOS", InsertBefore(y, 0xDA, Seg(0xCC, 0x00, 0x11)), "plain", false },
            { "a DNL before the SOS", InsertBefore(y, 0xDA, Seg(0xDC, 0x00, 0x20)), "plain", false },
            // the one shape a PDF passes through
            { "JFIF", j, "plain", true },
            { "JFIF, explicit zero crop", j, "zero crop", true },
            { "JFIF, EXIF top-left", InsertAfter(j, 0xE0, Exif(1)), "plain", true },
            { "IDs 1 2 3, no JFIF or Adobe", y, "plain", true },
            { "Adobe 1", InsertBefore(y, 0xDB, Adobe(1)), "plain", true },
            { "Adobe 2", InsertBefore(y, 0xDB, Adobe(2)), "plain", true },
            { "JFIF with Adobe 0", InsertAfter(j, 0xE0, a0), "plain", true },
            { "Adobe 0 then Adobe 1", InsertBefore(y, 0xDB, [.. a0, .. Adobe(1)]), "plain", true },
            { "Adobe 1, IDs R G B", InsertBefore(rgb, 0xDB, Adobe(1)), "plain", true },
            { "JFIF after the SOF, IDs R G B", InsertAfter(rgb, sofCode, JpegBytes.Jfif), "plain", true },
            { "IDs B G R", SetIds(y, 0x42, 0x47, 0x52), "plain", true },
            { "IDs 7 8 9", SetIds(y, 7, 8, 9), "plain", true },
            { "1 component with Adobe 0", InsertBefore(Without(gray, 0xE0), 0xDB, a0), "plain", true },
            // ordinary files, where the verdict must agree with SkPDF
            { "camera-style: EXIF and no JFIF", InsertBefore(y, 0xDB, Exif(1)), "plain", true },
            { "grayscale", gray, "plain", true },
            { "progressive", Fixture("progressive.jpg"), "plain", true },
            { "4:4:4", Encode(3, full444: true), "plain", true },
            { "ICC-tagged", Encode(4, colorSpace: SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3)), "plain", true },
            { "Photoshop-style: Adobe 1", InsertBefore(Without(Encode(5), 0xE0), 0xDB, Adobe(1)), "plain", true },
        };
    }

    [Theory]
    [MemberData(nameof(Pictures))]
    public void APdfPictureThatWillBeDecoded_OverItsCap_IsAPlaceholder(string row, byte[] file, string placement, bool passedThrough)
    {
        File.WriteAllBytes(Path.Combine(_dir, "p.jpg"), file);
        var element = new ImageElement
        {
            Bounds = new Box(20, 20, 96, 64), Source = "p.jpg",
            Crop = placement switch { "cropped" => new Crop(0.1, 0, 0, 0), "zero crop" => new Crop(0, 0, 0, 0), _ => null },
            Shadow = placement == "shadowed" ? new Shadow(new Rgba(0, 0, 0, 128), 3, 3) : null,
        };

        // The plan's verdict, under a cap smaller than any picture.
        using (var ctx = new RenderContext(_dir, Open with { MaxPdfPicturePixels = 1 }))
        {
            var pdf = Pdf(element, ctx);
            var tooLarge = ctx.Warnings.Any(w => w.Code == RenderWarning.ImageTooLarge);
            var unreadable = ctx.Warnings.Any(w => w.Code == RenderWarning.ImageUnreadable);
            if (placement == "shadowed")
            {
                // The shadow is drawn through a layer, so its copy counts as decoded; the element itself is passed through.
                Assert.True(tooLarge, row);
                Assert.True(Contains(pdf, file), row);
            }
            else if (passedThrough)
            {
                Assert.False(tooLarge || unreadable, row);
                Assert.True(Contains(pdf, file), row);
            }
            else
            {
                // A file the decoder reads is refused by the cap; one it cannot read is a placeholder anyway.
                using var codec = SKCodec.Create(new MemoryStream(file));
                output.WriteLine(codec is null ? "unreadable by the decoder: a placeholder anyway" : "readable: refused by the cap");
                Assert.True(codec is null ? unreadable : tooLarge, row);
                Assert.False(Contains(pdf, file), row);
            }
        }

        // Soundness against SkPDF itself: uncapped, a picture the plan passes through appears whole in the PDF.
        if (passedThrough)
            using (var ctx = new RenderContext(_dir, Open))
                Assert.True(Contains(Pdf(element, ctx), file), row);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThePdfPictureCap_IsAVerdictForThatPdfOnly(bool pdfFirst)
    {
        // A PNG over the cap is a placeholder in the PDF, and is drawn in a bitmap, in either order, in one context.
        File.WriteAllBytes(Path.Combine(_dir, "teal.png"), Png(SKColors.Teal));
        var page = new Page(100, 100) { Elements = [new ImageElement { Bounds = new Box(0, 0, 100, 100), Source = "teal.png" }] };
        using var ctx = new RenderContext(_dir, Open with { MaxPdfPicturePixels = 48 * 32 - 1 });
        SKColor Bitmap() { using var bmp = Exporter.RenderBitmap(page, 72, ctx); return bmp.GetPixel(50, 50); }
        void ToPdf() => Exporter.ToPdf(new PubsmithDocument { Pages = [page] }, ctx, new MemoryStream());

        SKColor drawn;
        if (pdfFirst) { ToPdf(); drawn = Bitmap(); }
        else { drawn = Bitmap(); Assert.DoesNotContain(ctx.Warnings, w => w.Code == RenderWarning.ImageTooLarge); ToPdf(); }
        Assert.Equal(SKColors.Teal, drawn);
        var warning = Assert.Single(ctx.Warnings, w => w.Code == RenderWarning.ImageTooLarge);
        // The rule as users read it: the cap is on a picture that counts as decoded, which SkPDF may still pass through.
        Assert.Contains($"has {48 * 32} pixels, more than the {48 * 32 - 1} allowed for a picture that counts as decoded in a PDF", warning.Message);
    }

    // ---------- a PDF shadow layer over its cap ----------

    private static readonly Rgba Translucent = new(200, 0, 0, 128);   // a translucent fill: the shadow is drawn through a layer

    public static TheoryData<string> Layers() => ["plain", "rotated 30 degrees", "offset", "partly off the page", "a path-warp WordArt"];

    private static (Element Element, SKRect LayerInPage) Layer(string row)
    {
        var shadow = new Shadow(new Rgba(0, 0, 0, 128), 2.25, 3.5);
        var shape = new ShapeElement { Bounds = new Box(20.3, 30.6, 50, 40), Fill = Translucent, Shadow = shadow };
        Element e = row switch
        {
            "plain" => shape,
            "rotated 30 degrees" => shape with { Rotation = 30 },
            "offset" => shape with { Shadow = new Shadow(new Rgba(0, 0, 0, 128), 37.7, -12.2) },
            "partly off the page" => shape with { Bounds = new Box(170.4, 180.2, 50, 40) },
            "a path-warp WordArt" => new TextElement
            {
                Bounds = new Box(60, 60, 60, 30), Fit = TextFit.Stretch, Warp = new TextWarp(WarpKind.Circle), Shadow = shadow,
                Paragraphs = [new Paragraph { Runs = [new TextRun("Around and around", new TextStyle { Family = "Arial" })] }],
            },
            _ => throw new ArgumentException(row),
        };
        // The layer's bounds before mapping: the shape's frame widened by 1 pt; WordArt's ink bounds widened the same way.
        SKRect r;
        if (e is TextElement t)
        {
            using var ctx = new RenderContext(null, Open);
            using var stretched = TextRenderer.PrepareStretched(t, ctx);
            r = stretched.InkBounds;
            r.Inflate(1, 1);
        }
        else r = SKRect.Create((float)e.Bounds.X - 1, (float)e.Bounds.Y - 1, (float)e.Bounds.Width + 2, (float)e.Bounds.Height + 2);
        return (e, r);
    }

    // Under the element's rotation and the shadow's offset, at 1 pixel per point, clipped to the page, rounded out.
    private static long LayerPixels(Element e, SKRect r, double pageW, double pageH)
    {
        var m = SKMatrix.CreateTranslation((float)e.Shadow!.OffsetX, (float)e.Shadow.OffsetY)
            .PreConcat(SKMatrix.CreateRotationDegrees((float)e.Rotation, (float)e.Bounds.CenterX, (float)e.Bounds.CenterY));
        var corners = new[] { new SKPoint(r.Left, r.Top), new SKPoint(r.Right, r.Top), new SKPoint(r.Right, r.Bottom), new SKPoint(r.Left, r.Bottom) }.Select(m.MapPoint).ToArray();
        double l = Math.Max(0, corners.Min(p => p.X)), t = Math.Max(0, corners.Min(p => p.Y));
        double rr = Math.Min(pageW, corners.Max(p => p.X)), b = Math.Min(pageH, corners.Max(p => p.Y));
        return (long)((Math.Ceiling(rr) - Math.Floor(l)) * (Math.Ceiling(b) - Math.Floor(t)));
    }

    [Theory]
    [MemberData(nameof(Layers))]
    public void APdfLayerOverItsCap_IsRefusedBeforeTheLayer(string row)
    {
        var (element, rect) = Layer(row);
        var pixels = LayerPixels(element, rect, 200, 200);
        Assert.True(pixels > 100, $"{row}: {pixels}");

        // At the cap it is drawn; one rounded-out pixel over, it is refused, by name, and the context stays refused.
        using (var at = new RenderContext(null, Open with { MaxPdfLayerPixels = pixels })) Pdf(element, at);
        using var over = new RenderContext(null, Open with { MaxPdfLayerPixels = pixels - 1 });
        var ex = Assert.Throws<RenderLimitException>(() => Pdf(element, over));
        Assert.Equal(RenderLimitException.PdfLayerPixels, ex.Limit);
        Assert.Equal(pixels, ex.Value);
        Assert.Equal(1, ex.Page);
        Assert.Equal(1, ex.Element);
        Assert.Equal(RenderLimitException.PdfLayerPixels, Assert.Throws<RenderLimitException>(() => Pdf(new ShapeElement { Bounds = new Box(0, 0, 5, 5) }, over)).Limit);

        // The cap is the PDF's: a bitmap draws the same layer.
        using var bitmap = new RenderContext(null, Open with { MaxPdfLayerPixels = pixels - 1 });
        using (Exporter.RenderBitmap(new Page(200, 200) { Elements = [element] }, 72, bitmap)) { }
    }

    [Fact]
    public void APdfLayerWhollyOffThePage_IsDrawn()
    {
        // Nothing of it is on the page, so nothing is allocated: no refusal, whatever the cap.
        var element = new ShapeElement { Bounds = new Box(500, 600, 50, 40), Fill = Translucent, Shadow = new Shadow(new Rgba(0, 0, 0, 128), 2.25, 3.5) };
        using var ctx = new RenderContext(null, Open with { MaxPdfLayerPixels = 1 });
        Pdf(element, ctx);
    }

    [Theory]
    [InlineData(double.NaN, 10)]
    [InlineData(double.PositiveInfinity, 10)]
    [InlineData(10, double.NegativeInfinity)]
    public void ANonFinitePdfLayer_IsRefused(double width, double height)
    {
        var element = new ShapeElement { Bounds = new Box(10, 10, width, height), Fill = Translucent, Shadow = new Shadow(new Rgba(0, 0, 0, 128), 2, 2) };
        using var ctx = new RenderContext(null, Open);
        Assert.Equal(RenderLimitException.PdfLayerPixels, Assert.Throws<RenderLimitException>(() => Pdf(element, ctx)).Limit);
    }

    // ---------- a picture file over its size; the font cache; the bitmap ----------

    [Fact]
    public void APictureFileOverItsCap_IsNotRead()
    {
        var file = Png(SKColors.Teal);
        File.WriteAllBytes(Path.Combine(_dir, "teal.png"), file);
        var page = new Page(100, 100) { Elements = [new ImageElement { Bounds = new Box(0, 0, 48, 32), Source = "teal.png" }] };
        using (var over = new RenderContext(_dir, Open with { MaxPictureFileBytes = file.Length - 1 }))
        {
            using (var bmp = Exporter.RenderBitmap(page, 72, over)) Assert.NotEqual(SKColors.Teal, bmp.GetPixel(24, 16));   // the placeholder
            Exporter.ToPdf(new PubsmithDocument { Pages = [page] }, over, new MemoryStream());
            Assert.Equal(0, over.Pictures.BytesRead);
            var warning = Assert.Single(over.Warnings);
            Assert.Equal(RenderWarning.ImageTooLarge, warning.Code);
            Assert.Contains($"{file.Length} bytes", warning.Message);
        }
        using var at = new RenderContext(_dir, Open with { MaxPictureFileBytes = file.Length });
        using (var bmp = Exporter.RenderBitmap(page, 72, at)) Assert.Equal(SKColors.Teal, bmp.GetPixel(24, 16));
        Assert.Equal(file.Length, at.Pictures.BytesRead);
        Assert.Empty(at.Warnings);
    }

    [Fact]
    public void FontResolver_CachesAtMost4096Keys_ThenResolvesUncached()
    {
        // One resolver shared by two contexts, 5,000 names between them: its own limit holds whatever the contexts count.
        var fonts = new FontResolver();
        Page Names(int from) => new(100, 100)
        {
            Elements = [new TextElement { Bounds = new Box(0, 0, 100, 100), Paragraphs = [new Paragraph { Runs = Enumerable.Range(from, 2_500).Select(i => new TextRun("a", new TextStyle { Family = $"No Such Font {i:D5}" })).ToList() }] }],
        };
        using (var a = new RenderContext(null, Open, fonts: fonts)) using (Exporter.RenderBitmap(Names(0), 72, a)) { }
        using (var b = new RenderContext(null, Open, fonts: fonts)) using (Exporter.RenderBitmap(Names(2_500), 72, b)) { }
        Assert.Equal(4_096, FontResolver.MaxCachedKeys);
        Assert.Equal(4_096, fonts.CachedKeys);

        var before = fonts.Resolutions;
        Assert.Equal("Calibri", fonts.Resolve("No Such Font 00000", false, false).Typeface.FamilyName);   // cached
        Assert.Equal(before, fonts.Resolutions);
        for (var i = 0; i < 2; i++) Assert.Equal("Calibri", fonts.Resolve("No Such Font 04999", false, false).Typeface.FamilyName);   // past the cap: resolved each time
        Assert.Equal(before + 2, fonts.Resolutions);
        Assert.Equal(4_096, fonts.CachedKeys);
    }

    [Fact]
    public void MaxPixels_RefusesLetterAt2400Dpi_WithItsMessage()
    {
        // One page's bitmap must stay under SkiaSharp's 2 GiB allocation; Letter at 2,400 dpi (538.56 MP) failed to allocate.
        Assert.Equal(500_000_000, Exporter.MaxPixels);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Exporter.PixelSize(new Page(612, 792), Exporter.MaxDpi));
        Assert.Contains("538560000 pixels, over the 500000000 limit", ex.Message);
        Assert.Contains("--dpi", ex.Message);
    }

    [Fact]
    public void AtThePdfPictureCap_ThePictureIsDecoded()
    {
        File.WriteAllBytes(Path.Combine(_dir, "teal.png"), Png(SKColors.Teal));
        var element = new ImageElement { Bounds = new Box(0, 0, 100, 100), Source = "teal.png" };
        using var ctx = new RenderContext(_dir, Open with { MaxPdfPicturePixels = 48 * 32 });
        Pdf(element, ctx);
        Assert.Empty(ctx.Warnings);
    }
}
