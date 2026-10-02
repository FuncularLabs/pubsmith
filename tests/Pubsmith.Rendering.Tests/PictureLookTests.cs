using Pubsmith.Core;
using SkiaSharp;

namespace Pubsmith.Rendering.Tests;

// WI-005 AC3: pictures look as they did when each placement drew the crop's source rectangle into the frame. That
// drawing is kept here as the oracle; the renderer may draw pictures any cheaper way that matches it.
public sealed class PictureLookTests : IDisposable
{
    private const double Dpi = 150;
    private readonly string _dir = Directory.CreateTempSubdirectory("pubsmith-picture-").FullName;

    public PictureLookTests()
    {
        // A detailed picture (so a crop offset by a pixel shows), and a JPEG whose EXIF says "turn 90 degrees".
        using var bmp = new SKBitmap(90, 60);
        for (var y = 0; y < 60; y++) for (var x = 0; x < 90; x++) bmp.SetPixel(x, y, new SKColor((byte)(x * 37 % 256), (byte)(y * 53 % 256), (byte)((x + y) * 11 % 256)));
        File.WriteAllBytes(Path.Combine(_dir, "picture.png"), Encode(bmp, SKEncodedImageFormat.Png));
        using var halves = new SKBitmap(60, 40);
        for (var y = 0; y < 40; y++) for (var x = 0; x < 60; x++) halves.SetPixel(x, y, x < 30 ? new SKColor(220, 0, 0) : new SKColor(0, 0, 220));
        var jpeg = Encode(halves, SKEncodedImageFormat.Jpeg);
        File.WriteAllBytes(Path.Combine(_dir, "turned.jpg"), WithExifOrientation(jpeg, 6));
        // A JPEG with distinct quadrants for each EXIF orientation, so every one of the 8 turns and flips shows.
        using var quarters = new SKBitmap(60, 40);
        for (var y = 0; y < 40; y++) for (var x = 0; x < 60; x++)
            quarters.SetPixel(x, y, (x < 30, y < 20) switch { (true, true) => new SKColor(220, 0, 0), (false, true) => new SKColor(0, 160, 0), (true, false) => new SKColor(0, 0, 220), _ => new SKColor(230, 200, 0) });
        var quartersJpeg = Encode(quarters, SKEncodedImageFormat.Jpeg);
        for (ushort o = 1; o <= 8; o++) File.WriteAllBytes(Path.Combine(_dir, $"orientation{o}.jpg"), WithExifOrientation(quartersJpeg, o));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] Encode(SKBitmap bmp, SKEncodedImageFormat format)
    {
        using var data = bmp.Encode(format, 95);
        return data.ToArray();
    }

    /// <summary>The JPEG with an APP1 Exif segment (one IFD entry: Orientation) inserted after its SOI marker.</summary>
    private static byte[] WithExifOrientation(byte[] jpeg, ushort orientation)
    {
        byte[] exif =
        [
            0xFF, 0xE1, 0x00, 0x22,                                  // APP1, length 34
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'I', (byte)'I', 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, // little-endian TIFF, IFD0 at 8
            0x01, 0x00,                                              // one entry
            0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00,          // Orientation, SHORT, count 1
            (byte)orientation, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,                                  // no next IFD
        ];
        return [.. jpeg[..2], .. exif, .. jpeg[2..]];
    }

    public static TheoryData<string> Kinds() => ["uncropped", "cropped", "cropped on one side in a fractional frame", "cropped with an oval mask and a line", "rotated and cropped"];

    private static ImageElement Make(string kind) => kind switch
    {
        "uncropped" => new ImageElement { Bounds = new Box(40, 30, 180, 120), Source = "picture.png" },
        "cropped" => new ImageElement { Bounds = new Box(40, 30, 180, 120), Source = "picture.png", Crop = new Crop(0.1, 0.2, 0.25, 0.05) },
        "cropped on one side in a fractional frame" => new ImageElement { Bounds = new Box(40.37, 30.61, 180.4, 120.3), Source = "picture.png", Crop = new Crop(0, 0, 0.3, 0) },
        "cropped with an oval mask and a line" => new ImageElement
        {
            Bounds = new Box(40, 30, 180, 120), Source = "picture.png", Crop = new Crop(0.15, 0.1, 0.05, 0.3), Mask = ShapeKind.Ellipse,
            Stroke = new Stroke(new Rgba(0, 0, 0), 3),
        },
        "rotated and cropped" => new ImageElement { Bounds = new Box(40, 30, 180, 120), Rotation = 25, Source = "picture.png", Crop = new Crop(0.2, 0, 0.2, 0.2) },
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>Today's picture drawing: the crop's source rectangle, scaled into the frame, under the oval mask; then the line.</summary>
    private SKBitmap Oracle(Page page)
    {
        var (w, h) = Exporter.PixelSize(page, Dpi);
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        canvas.Scale((float)(Dpi / 72));
        foreach (var img in page.Elements.OfType<ImageElement>())
        {
            canvas.Save();
            if (img.Rotation != 0) canvas.RotateDegrees((float)img.Rotation, (float)img.Bounds.CenterX, (float)img.Bounds.CenterY);
            var dst = SKRect.Create((float)img.Bounds.X, (float)img.Bounds.Y, (float)img.Bounds.Width, (float)img.Bounds.Height);
            using var image = SKImage.FromEncodedData(Path.Combine(_dir, img.Source));
            var c = img.Crop ?? new Crop(0, 0, 0, 0);
            var src = new SKRect((float)(c.Left * image.Width), (float)(c.Top * image.Height), (float)((1 - c.Right) * image.Width), (float)((1 - c.Bottom) * image.Height));
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
            if (img.Stroke is { Width: > 0 } s)
            {
                using var sp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)s.Width, Color = new SKColor(s.Color.R, s.Color.G, s.Color.B, s.Color.A) };
                if (oval) canvas.DrawOval(dst, sp); else canvas.DrawRect(dst, sp);
            }
            canvas.Restore();
        }
        return bmp;
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void EveryPicture_LooksAsBefore(string kind)
    {
        var page = new Page(260, 180) { Elements = [Make(kind)] };
        using var expected = Oracle(page);
        using var actual = Exporter.RenderBitmap(page, Dpi, new RenderContext(_dir));
        var score = ImageComparer.Score(expected, actual);
        Assert.True(score <= 0.1, $"{kind}: score {score:F3}");
    }

    [Fact]
    public void TheOracle_SeesACropOffByAFewPixels()
    {
        var page = new Page(260, 180) { Elements = [Make("cropped")] };
        var off = Make("cropped") with { Crop = new Crop(0.1 + 3.0 / 90, 0.2, 0.25 - 3.0 / 90, 0.05) };
        using var expected = Oracle(page);
        using var actual = Exporter.RenderBitmap(new Page(260, 180) { Elements = [off] }, Dpi, new RenderContext(_dir));
        Assert.True(ImageComparer.Score(expected, actual) > 0.5);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void EveryExifOrientation_IsDrawnAsToday(int orientation)
    {
        var page = new Page(160, 160) { Elements = [new ImageElement { Bounds = new Box(20, 20, 120, 120), Source = $"orientation{orientation}.jpg" }] };
        using var expected = Oracle(page);
        using var actual = Exporter.RenderBitmap(page, Dpi, new RenderContext(_dir));
        var score = ImageComparer.Score(expected, actual);
        Assert.True(score <= 0.1, $"orientation {orientation}: score {score:F3}");
    }

    [Fact]
    public void AnExifRotatedJpeg_IsEmbeddedTurned_InPdf()
    {
        // Stored 60 x 40 with orientation 6: the PDF holds it turned, 40 x 60.
        var page = new Page(100, 100) { Elements = [new ImageElement { Bounds = new Box(30, 20, 40, 60), Source = "turned.jpg" }] };
        using var pdf = new MemoryStream();
        Exporter.ToPdf(new PubsmithDocument { Pages = [page] }, new RenderContext(_dir), pdf);
        var image = Assert.Single(PdfImages.In(pdf.ToArray()));
        Assert.Equal((40, 60), (image.Width, image.Height));
    }

    [Fact]
    public void AnExifRotatedJpeg_IsDrawnTurned()
    {
        // Stored 60 x 40, left half red, right half blue; orientation 6 turns it a quarter clockwise, so in a 40 x 60
        // frame the top half is red and the bottom half blue.
        var page = new Page(100, 100) { Elements = [new ImageElement { Bounds = new Box(30, 20, 40, 60), Source = "turned.jpg" }] };
        using var bmp = Exporter.RenderBitmap(page, 72, new RenderContext(_dir));
        foreach (var x in new[] { 38, 62 })
        {
            Assert.True(bmp.GetPixel(x, 32) is { Red: > 150, Blue: < 80 }, $"({x}, 32) is {bmp.GetPixel(x, 32)}");
            Assert.True(bmp.GetPixel(x, 68) is { Blue: > 150, Red: < 80 }, $"({x}, 68) is {bmp.GetPixel(x, 68)}");
        }
    }
}
