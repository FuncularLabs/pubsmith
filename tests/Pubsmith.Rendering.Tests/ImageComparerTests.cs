using SkiaSharp;

namespace Pubsmith.Rendering.Tests;

// AC5: the comparer reproduces the ImageMagick pipeline of the LibreOffice baseline and Measure-PubLayoutFidelity.ps1
// (resize 800x800!, grayscale, gaussian blur sigma 1.5, RMSE as a percentage).
public class ImageComparerTests
{
    [Fact]
    public void Identical_ScoresZero()
    {
        var golden = Fixtures.Golden("F01-page-custom-size");
        Assert.Equal(0, ImageComparer.Score(golden, golden), 6);
    }

    [Fact]
    public void LibreOfficeF01Pair_MatchesImageMagickScore()
    {
        // ImageMagick 7.1.1 scored this exact pair 4.10 (0.0409869) on 2026-09-27.
        var score = ImageComparer.Score(
            Fixtures.Golden("F01-page-custom-size"),
            Path.Combine(Fixtures.Dir, "libreoffice", "F01-page-custom-size.png"));
        Assert.InRange(score, 3.6, 4.6);
    }

    [Fact]
    public void BlackVersusWhite_ScoresHundred()
    {
        using var black = Solid(SKColors.Black, 50, 50);
        using var white = Solid(SKColors.White, 50, 50);
        Assert.Equal(100, ImageComparer.Score(black, white), 1);
    }

    [Theory]
    // Rec.709 luma: red weighs 0.2126, green 0.7152, blue 0.0722, so each primary against white scores
    // (1 - weight) x 100. Kills a comparer that reads one channel or averages R, G and B.
    [InlineData(255, 0, 0, 78.74)]
    [InlineData(0, 255, 0, 28.48)]
    [InlineData(0, 0, 255, 92.78)]
    public void ColourWeighting_UsesRec709Luma(byte r, byte g, byte b, double expected)
    {
        using var colour = Solid(new SKColor(r, g, b), 30, 30);
        using var white = Solid(SKColors.White, 30, 30);
        Assert.Equal(expected, ImageComparer.Score(colour, white), 1);
    }

    [Fact]
    public void DifferentSizes_AreNormalised()
    {
        using var small = Solid(SKColors.Gray, 40, 30);
        using var large = Solid(SKColors.Gray, 400, 900);
        Assert.Equal(0, ImageComparer.Score(small, large), 3);
    }

    [Fact]
    public void Transparency_IsFlattenedOntoWhite()
    {
        using var clear = Solid(SKColors.Transparent, 20, 20);
        using var white = Solid(SKColors.White, 20, 20);
        Assert.Equal(0, ImageComparer.Score(clear, white), 3);
    }

    [Fact]
    public void MissingFile_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() => ImageComparer.Score("nope-a.png", Fixtures.Golden("F01-page-custom-size")));
    }

    private static SKBitmap Solid(SKColor color, int w, int h)
    {
        var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        bmp.Erase(color);
        return bmp;
    }
}
