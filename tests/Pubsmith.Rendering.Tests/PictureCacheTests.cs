using System.Buffers.Binary;
using System.Text;
using Pubsmith.Core;
using SkiaSharp;

namespace Pubsmith.Rendering.Tests;

// WI-005 AC4 and AC5: the picture cache's reads, decodes and PDF embeds, and the check of a picture's declared size
// (the rules are the ACs'). Counters are the cache's own; limits are injected where the
// defaults would need gigabytes, so a failing run shows a wrong number rather than a crash.
public sealed class PictureCacheTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pubsmith-cache-").FullName;

    public PictureCacheTests()
    {
        Save(Noise(300, 200, 1), "picture.png", SKEncodedImageFormat.Png);
        Save(Noise(300, 200, 2), "photo.jpg", SKEncodedImageFormat.Jpeg);
        Save(Solid(100, 100, SKColors.Red), "a.png", SKEncodedImageFormat.Png);
        Save(Solid(100, 100, SKColors.Lime), "b.png", SKEncodedImageFormat.Png);
        Save(Solid(100, 100, SKColors.Blue), "c.png", SKEncodedImageFormat.Png);
        Save(Solid(60, 40, SKColors.Red), "small.png", SKEncodedImageFormat.Png);
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        Save(Solid(100, 100, SKColors.Blue), Path.Combine("sub", "a.png"), SKEncodedImageFormat.Png);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static SKBitmap Noise(int w, int h, int seed)
    {
        var rng = new Random(seed);
        var bmp = new SKBitmap(w, h);
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) bmp.SetPixel(x, y, new SKColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256)));
        return bmp;
    }

    private static SKBitmap Solid(int w, int h, SKColor color)
    {
        var bmp = new SKBitmap(w, h);
        bmp.Erase(color);
        return bmp;
    }

    private void Save(SKBitmap bmp, string name, SKEncodedImageFormat format)
    {
        using (bmp)
        using (var data = bmp.Encode(format, 90))
            File.WriteAllBytes(Path.Combine(_dir, name), data.ToArray());
    }

    private static ImageElement Picture(string source, int i = 0, Crop? crop = null) =>
        new() { Bounds = new Box(20 + i % 5 * 110, 20 + i / 5 * 80, 100, 70), Source = source, Crop = crop };

    private static Page PageOf(params Element[] elements) => new(612, 792) { Elements = elements };

    private byte[] Pdf(RenderContext ctx, params Page[] pages)
    {
        using var s = new MemoryStream();
        Exporter.ToPdf(new PubsmithDocument { Pages = pages }, ctx, s);
        return s.ToArray();
    }

    // ---------- reads, decodes and embeds ----------

    [Fact]
    public void APictureRepeated_IsEmbeddedOnceInThePdf()
    {
        var once = Pdf(new RenderContext(_dir), PageOf(Picture("picture.png")));
        var pages = Enumerable.Range(0, 5).Select(p => PageOf([.. Enumerable.Range(0, 10).Select(i => (Element)Picture("picture.png", i))])).ToArray();
        var fifty = Pdf(new RenderContext(_dir), pages);
        Assert.True(fifty.Length < 1.5 * once.Length, $"{fifty.Length} bytes for 50 placements against {once.Length} for one");
    }

    [Fact]
    public void AJpeg_PassesThroughThePdf_EvenAfterABitmapRender()
    {
        var page = PageOf(Picture("photo.jpg"));
        var ctx = new RenderContext(_dir);
        Assert.Equal("DCTDecode", Assert.Single(PdfImages.In(Pdf(ctx, page))).Filter);
        Assert.Equal(0, ctx.Pictures.Decodes);   // a PDF draws the encoded picture: we decode nothing

        using (Exporter.RenderBitmap(page, 72, ctx)) { }
        Assert.Equal(1, ctx.Pictures.Decodes);
        Assert.Equal("DCTDecode", Assert.Single(PdfImages.In(Pdf(ctx, page))).Filter);

        // A caller's own PDF canvas after a bitmap render: the bitmap mark was cleared, so the JPEG still passes through.
        using var own = new MemoryStream();
        using (var doc = SKDocument.CreatePdf(own))
        {
            PageRenderer.Render(doc.BeginPage(612, 792), page, ctx);
            doc.EndPage();
            doc.Close();
        }
        Assert.Equal("DCTDecode", Assert.Single(PdfImages.In(own.ToArray())).Filter);
    }

    [Fact]
    public void APictureRepeated_IsReadAndDecodedOnce()
    {
        var ctx = new RenderContext(_dir);
        for (var p = 0; p < 5; p++)
            using (Exporter.RenderBitmap(PageOf([.. Enumerable.Range(0, 10).Select(i => (Element)Picture("picture.png", i))]), 72, ctx)) { }
        Assert.Equal(1, ctx.Pictures.Reads);
        Assert.Equal(1, ctx.Pictures.Decodes);
    }

    private static int Decodes(RenderContext ctx, params string[] sequence)
    {
        foreach (var source in sequence)
        {
            using (Exporter.RenderBitmap(PageOf(Picture(source)), 72, ctx)) { }
            Assert.True(ctx.Pictures.DecodedPixels <= ctx.Pictures.Budget, $"{ctx.Pictures.DecodedPixels} pixels kept, budget {ctx.Pictures.Budget}");
        }
        Assert.True(ctx.Pictures.PeakDecodedPixels <= ctx.Pictures.Budget, $"peak {ctx.Pictures.PeakDecodedPixels} pixels, budget {ctx.Pictures.Budget}");
        return ctx.Pictures.Decodes;
    }

    [Fact]
    public void TheLeastRecentlyUsedIsDropped()
    {
        // Each picture is 10,000 pixels; the budget holds two.
        Assert.Equal(3, Decodes(new RenderContext(_dir, maxPicturePixels: 1_000_000, cacheBudget: 20_000), "a.png", "b.png", "a.png", "c.png", "a.png"));
        Assert.Equal(4, Decodes(new RenderContext(_dir, maxPicturePixels: 1_000_000, cacheBudget: 20_000), "a.png", "b.png", "c.png", "a.png"));
    }

    [Fact]
    public void APictureOverTheBudget_IsDrawnUncached()
    {
        var ctx = new RenderContext(_dir, maxPicturePixels: 1_000_000, cacheBudget: 5_000);
        using var bmp = Exporter.RenderBitmap(PageOf(Picture("a.png")), 72, ctx);
        Assert.Equal(0, ctx.Pictures.Decodes);
        Assert.Equal(0, ctx.Pictures.DecodedPixels);
        Assert.Equal(SKColors.Red, bmp.GetPixel(70, 55));   // drawn all the same, from the encoded picture
    }

    [Fact]
    public void Defaults_ArePinned()
    {
        Assert.Equal(250_000_000, RenderContext.MaxPicturePixels);
        Assert.Equal(100_000_000, RenderContext.PictureCacheBudget);
        var ctx = new RenderContext(_dir);
        Assert.Equal(RenderContext.MaxPicturePixels, ctx.Pictures.MaxPixels);
        Assert.Equal(RenderContext.PictureCacheBudget, ctx.Pictures.Budget);
    }

    [Fact]
    public void DifferentPaths_AreDifferentPictures()
    {
        // Two names, and one name in two folders: three pictures.
        var ctx = new RenderContext(_dir);
        using var bmp = Exporter.RenderBitmap(PageOf(Picture("a.png", 0), Picture("b.png", 1), Picture("sub/a.png", 2)), 72, ctx);
        Assert.Equal(3, ctx.Pictures.Reads);
        Assert.Equal(SKColors.Red, bmp.GetPixel(70, 55));
        Assert.Equal(SKColors.Lime, bmp.GetPixel(180, 55));
        Assert.Equal(SKColors.Blue, bmp.GetPixel(290, 55));
    }

    [Fact]
    public void TheSamePathInAnotherCase_IsOnePicture()
    {
        if (!OperatingSystem.IsWindows()) return;   // the cache compares paths case-insensitively on Windows only
        var ctx = new RenderContext(_dir);
        using (Exporter.RenderBitmap(PageOf(Picture("a.png", 0), Picture("A.PNG", 1)), 72, ctx)) { }
        Assert.Equal(1, ctx.Pictures.Reads);
    }

    [Fact]
    public void AMissingPicture_IsAPlaceholderEverywhere_ReadOnce_AndWarnsAsToday()
    {
        var ctx = new RenderContext(_dir);
        using (var bmp = Exporter.RenderBitmap(PageOf([.. Enumerable.Range(0, 10).Select(i => (Element)Picture("missing.png", i))]), 72, ctx))
            Assert.Equal(new SKColor(0xE0, 0xE0, 0xE0), bmp.GetPixel(60, 30));   // the placeholder's grey
        for (var p = 0; p < 4; p++)
            using (Exporter.RenderBitmap(PageOf([.. Enumerable.Range(0, 10).Select(i => (Element)Picture("missing.png", i))]), 72, ctx)) { }
        Assert.Equal(1, ctx.Pictures.Reads);
        Assert.Single(ctx.Warnings, w => w.Code == RenderWarning.ImageMissing);

        // Two spellings of one missing path: one entry, but two warnings, as each names its own spelling (as today).
        using (Exporter.RenderBitmap(PageOf(Picture("./missing.png")), 72, ctx)) { }
        Assert.Equal(1, ctx.Pictures.Reads);
        Assert.Equal(2, ctx.Warnings.Count(w => w.Code == RenderWarning.ImageMissing));
    }

    [Fact]
    public void APictureThatCannotBeRead_IsUnreadable()
    {
        if (!OperatingSystem.IsWindows()) return;   // the lock below is made with Windows' sharing rules
        var ctx = new RenderContext(_dir);
        using (new FileStream(Path.Combine(_dir, "a.png"), FileMode.Open, FileAccess.Read, FileShare.None))
        using (var bmp = Exporter.RenderBitmap(PageOf(Picture("a.png")), 72, ctx))
            Assert.Equal(new SKColor(0xE0, 0xE0, 0xE0), bmp.GetPixel(60, 30));
        var warning = Assert.Single(ctx.Warnings);
        Assert.Equal(RenderWarning.ImageUnreadable, warning.Code);
        Assert.Equal("Image 'a.png' could not be read.", warning.Message);   // not "decoded": nothing was
    }

    [Fact]
    public void APictureThatCouldNotBeRead_IsReadAgainAtItsNextPlacement()
    {
        if (!OperatingSystem.IsWindows()) return;   // the lock below is made with Windows' sharing rules
        // A file locked by another program is not this picture's verdict. While the lock is held, every lookup reads
        // the file again: a shadowed placement looks it up twice (its shadow, then itself), on every page. Once the file
        // is free, the next lookup reads it, and every later one uses what was read.
        var ctx = new RenderContext(_dir);
        var page = PageOf(Picture("a.png") with { Shadow = new Shadow(new Rgba(0, 0, 0, 128), 4, 4) });
        using (new FileStream(Path.Combine(_dir, "a.png"), FileMode.Open, FileAccess.Read, FileShare.None))
            for (var p = 0; p < 10; p++)
                using (var bmp = Exporter.RenderBitmap(page, 72, ctx))
                    Assert.Equal(new SKColor(0xE0, 0xE0, 0xE0), bmp.GetPixel(60, 30));
        Assert.Equal(20, ctx.Pictures.Reads);   // a cache that stopped retrying within 20 failures fails here or below
        for (var p = 0; p < 2; p++)
            using (var bmp = Exporter.RenderBitmap(page, 72, ctx))
                Assert.Equal(SKColors.Red, bmp.GetPixel(70, 55));
        Assert.Equal(21, ctx.Pictures.Reads);
    }

    public static TheoryData<string> RememberedVerdicts() => ["drawn", "missing", "undecodable", "over the limit"];

    [Theory]
    [MemberData(nameof(RememberedVerdicts))]
    public void EveryVerdictButAReadFailure_IsRemembered_SoTheFileIsReadOnce(string verdict)
    {
        // Whatever the first lookup found is kept for the context; only a read failure is tried again (above).
        // 3 pages of 5 shadowed placements: 30 lookups, 1 read, and at most one warning.
        File.WriteAllText(Path.Combine(_dir, "notes.png"), "not a picture");
        var (source, state, message) = verdict switch
        {
            "drawn" => ("small.png", PictureState.Ok, null),
            "missing" => ("nowhere.png", PictureState.Missing, $"Image 'nowhere.png' not found (looked for '{Path.Combine(_dir, "nowhere.png")}')."),
            "undecodable" => ("notes.png", PictureState.Unreadable, "Image 'notes.png' could not be decoded."),
            "over the limit" => ("small.png", PictureState.TooLarge, "Image 'small.png' declares 2400 pixels, more than the 1000 limit; it was not decoded."),
            _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, null),
        };
        var ctx = new RenderContext(_dir, maxPicturePixels: verdict == "over the limit" ? 1_000 : 1_000_000, cacheBudget: 1_000_000);
        var page = PageOf([.. Enumerable.Range(0, 5).Select(i => (Element)(Picture(source, i) with { Shadow = new Shadow(new Rgba(0, 0, 0, 128), 4, 4) }))]);
        for (var p = 0; p < 3; p++)
            using (Exporter.RenderBitmap(page, 72, ctx)) { }
        Assert.Equal(1, ctx.Pictures.Reads);
        Assert.Equal(state, ctx.Pictures.Get(Path.Combine(_dir, source)).State);   // remembered: still 1 read
        Assert.Equal(1, ctx.Pictures.Reads);
        Assert.Equal(message is null ? [] : [message], ctx.Warnings.Select(w => w.Message));
    }

    // ---------- pictures declaring too many pixels ----------

    /// <summary>A PNG with only a header: signature, an IHDR declaring the size, an empty IDAT and IEND.</summary>
    private string LiarPng(string name, uint width, uint height)
    {
        static byte[] Chunk(string type, byte[] data)
        {
            var typed = (byte[])[.. Encoding.ASCII.GetBytes(type), .. data];
            var c = new byte[12 + data.Length];
            BinaryPrimitives.WriteUInt32BigEndian(c, (uint)data.Length);
            typed.CopyTo(c, 4);
            BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(8 + data.Length), Crc32(typed));
            return c;
        }
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; ihdr[9] = 2;   // 8-bit RGB
        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, .. Chunk("IHDR", ihdr), .. Chunk("IDAT", []), .. Chunk("IEND", [])];
        File.WriteAllBytes(Path.Combine(_dir, name), png);
        return Path.Combine(_dir, name);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    [Fact]
    public void ALiarHeader_IsJudgedTooLarge_WithoutDecoding()
    {
        // 100,000 x 100,000 pixels declared by a 57-byte file. Judged without drawing: a broken check must fail the
        // assertion, not reach a 40 GB allocation.
        var ctx = new RenderContext(_dir);
        Assert.Equal(PictureState.TooLarge, ctx.Pictures.Get(LiarPng("liar.png", 100_000, 100_000)).State);
        Assert.Equal(0, ctx.Pictures.Decodes);
    }

    [Fact]
    public void ASizeThatOverflowsAnInt_IsJudgedTooLarge()
    {
        // 65,536 x 65,536: as an int product, 0.
        var ctx = new RenderContext(_dir);
        Assert.Equal(PictureState.TooLarge, ctx.Pictures.Get(LiarPng("wrap.png", 65_536, 65_536)).State);
    }

    [Fact]
    public void ACopyThatCannotBeAllocated_IsNotMade_AndIsTriedAgain()
    {
        // 1,000,000 x 1,000,000 declared, accepted by limits raised for the test: the 4 TB copy cannot be allocated.
        // The cache gives the encoded image to draw instead (as before WI-005), remembers nothing, and does not throw.
        // Judged without drawing, so nothing here can reach a native allocation that aborts.
        var ctx = new RenderContext(_dir, maxPicturePixels: 10_000_000_000_000, cacheBudget: 10_000_000_000_000);
        var picture = ctx.Pictures.Get(LiarPng("huge.png", 1_000_000, 1_000_000));
        Assert.Equal(PictureState.Ok, picture.State);
        Assert.Null(ctx.Pictures.Decoded(picture));
        Assert.False(picture.DecodeFailed);   // memory may be free next time
        Assert.Null(ctx.Pictures.Decoded(picture));
        Assert.Equal(0, ctx.Pictures.Decodes);
        Assert.Equal(0, ctx.Pictures.DecodedPixels);
    }

    [Fact]
    public void AnOverLimitPicture_IsAPlaceholder_InPngAndPdf()
    {
        var ctx = new RenderContext(_dir, maxPicturePixels: 1_000, cacheBudget: 1_000_000);   // small.png is 2,400 pixels
        using (var bmp = Exporter.RenderBitmap(PageOf(Picture("small.png")), 72, ctx))
            Assert.Equal(new SKColor(0xE0, 0xE0, 0xE0), bmp.GetPixel(60, 30));
        var warning = Assert.Single(ctx.Warnings, w => w.Code == RenderWarning.ImageTooLarge);
        Assert.Equal("Image 'small.png' declares 2400 pixels, more than the 1000 limit; it was not decoded.", warning.Message);
        Assert.Empty(PdfImages.In(Pdf(ctx, PageOf(Picture("small.png")))));
        Assert.Equal(0, ctx.Pictures.Decodes);
    }

    [Fact]
    public void AtTheLimit_IsDrawn_OneMoreIsNot()
    {
        var path = Path.Combine(_dir, "small.png");   // 60 x 40 = 2,400 pixels
        Assert.Equal(PictureState.Ok, new RenderContext(_dir, maxPicturePixels: 2_400, cacheBudget: 1_000_000).Pictures.Get(path).State);
        Assert.Equal(PictureState.TooLarge, new RenderContext(_dir, maxPicturePixels: 2_399, cacheBudget: 1_000_000).Pictures.Get(path).State);
    }
}
