using Pubsmith.Core;
using SkiaSharp;

namespace Pubsmith.Rendering.Tests;

// Documents can come from anywhere (an imported .pub, a hand-edited JSON file). Rendering one must not allocate
// without bound, and must not read files outside the document's folder or touch the network.
public sealed class LimitsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pubsmith-limits-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData(1e7, 1e7, 300)]   // a page the size of a city block
    [InlineData(612, 1e9, 72)]    // one enormous side
    public void HugePage_IsRefused_BeforeAllocating(double w, double h, double dpi)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Exporter.RenderBitmap(new Page(w, h), dpi, new RenderContext()));
        Assert.Contains("--dpi", ex.Message);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-5, 100)]
    [InlineData(double.NaN, 100)]
    [InlineData(100, double.PositiveInfinity)]
    public void ImpossiblePageSize_IsRefused(double w, double h)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Exporter.RenderBitmap(new Page(w, h), 72, new RenderContext()));
        Assert.Throws<ArgumentOutOfRangeException>(() => Exporter.ToPdf(new PubsmithDocument { Pages = [new Page(w, h)] }, new RenderContext(), Stream.Null));
    }

    [Fact]
    public void LetterAtMaxDpi_IsWithinTheCap()
    {
        var (w, h) = Exporter.PixelSize(new Page(612, 792), Exporter.MaxDpi);
        Assert.Equal((20400, 26400), (w, h));
    }

    private string Png(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bmp = new SKBitmap(4, 4);
        bmp.Erase(SKColors.Red);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private RenderWarning? RenderWithImage(string source, string? baseDirectory)
    {
        var ctx = new RenderContext(baseDirectory);
        var page = new Page(20, 20) { Elements = [new ImageElement { Bounds = new Box(0, 0, 20, 20), Source = source }] };
        using var _ = Exporter.RenderBitmap(page, 72, ctx);
        return ctx.Warnings.SingleOrDefault();
    }

    [Fact]
    public void ImageInsideTheDocumentFolder_Loads()
    {
        Png(Path.Combine(_dir, "doc", "art", "a.png"));
        Assert.Null(RenderWithImage("art/a.png", Path.Combine(_dir, "doc")));
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("art/../../outside.png")]
    public void RelativePathEscapingTheDocumentFolder_IsRefused(string source)
    {
        Png(Path.Combine(_dir, "outside.png"));
        Directory.CreateDirectory(Path.Combine(_dir, "doc"));
        var w = RenderWithImage(source, Path.Combine(_dir, "doc"));
        Assert.Equal(RenderWarning.ImageRefused, w?.Code);
    }

    [Fact]
    public void AbsolutePathOutsideTheDocumentFolder_IsRefused()
    {
        var outside = Png(Path.Combine(_dir, "outside.png"));
        Directory.CreateDirectory(Path.Combine(_dir, "doc"));
        Assert.Equal(RenderWarning.ImageRefused, RenderWithImage(outside, Path.Combine(_dir, "doc"))?.Code);
    }

    [Fact]
    public void SiblingFolderWithTheSamePrefix_IsRefused()
    {
        // "doc-evil" starts with "doc" but is not inside it.
        Png(Path.Combine(_dir, "doc-evil", "a.png"));
        Directory.CreateDirectory(Path.Combine(_dir, "doc"));
        Assert.Equal(RenderWarning.ImageRefused, RenderWithImage("../doc-evil/a.png", Path.Combine(_dir, "doc"))?.Code);
    }

    [Theory]
    [InlineData(@"\\attacker.invalid\share\a.png")]
    [InlineData("//attacker.invalid/share/a.png")]
    [InlineData(@"\\?\UNC\attacker.invalid\share\a.png")]
    [InlineData(@"\\.\UNC\attacker.invalid\share\a.png")]
    [InlineData(@"\??\UNC\attacker.invalid\share\a.png")]                         // not rewritten by GetFullPath
    [InlineData(@"\\?\GLOBALROOT\Device\Mup\attacker.invalid\share\a.png")]
    [InlineData(@"\\.\GLOBALROOT\Device\Mup\attacker.invalid\share\a.png")]
    public void NetworkPaths_AreRefused_WithOrWithoutABaseDirectory(string source)
    {
        Assert.Equal(RenderWarning.ImageRefused, RenderWithImage(source, null)?.Code);
        Assert.Equal(RenderWarning.ImageRefused, RenderWithImage(source, _dir)?.Code);
    }

    [Fact]
    public void InvalidImagePath_RefusesThatImageOnly()
    {
        Png(Path.Combine(_dir, "ok.png"));
        var ctx = new RenderContext(_dir);
        var page = new Page(40, 20)
        {
            Elements =
            [
                new ImageElement { Bounds = new Box(0, 0, 20, 20), Source = "bad\0name.png" },
                new ImageElement { Bounds = new Box(20, 0, 20, 20), Source = "ok.png" },
            ],
        };
        using var bmp = Exporter.RenderBitmap(page, 72, ctx);
        Assert.Equal(RenderWarning.ImageRefused, Assert.Single(ctx.Warnings).Code);
        Assert.Equal(SKColors.Red, bmp.GetPixel(30, 10));   // the valid picture still drew
    }

    [Fact]
    public void AbsoluteLocalPath_WithoutABaseDirectory_StillLoads()
    {
        // Programmatic use (no document folder) keeps working with local absolute paths.
        Assert.Null(RenderWithImage(Png(Path.Combine(_dir, "abs.png")), null));
    }
}
