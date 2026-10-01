using Pubsmith.Core;
using SkiaSharp;

namespace Pubsmith.Rendering.Tests;

// AC6: missing fonts and images are reported, never silent.
public class FontAndWarningTests
{
    private const string Missing = "Definitely Missing Font XYZ";

    [Fact]
    public void InstalledFamily_ResolvesWithoutSubstitution()
    {
        var f = new FontResolver().Resolve("Arial", bold: false, italic: false);
        Assert.False(f.IsSubstitute);
        Assert.Equal("Arial", f.Typeface.FamilyName, ignoreCase: true);
    }

    [Fact]
    public void StyleInTheFamilyName_IsResolved()
    {
        // Windows exposes "Arial Black" as family "Arial" at weight 900; a document naming "Arial Black" must get it.
        var f = new FontResolver().Resolve("Arial Black", bold: false, italic: false);
        Assert.False(new FontResolver().Resolve("Arial Black", bold: true, italic: false).IsSubstitute);
        Assert.False(f.IsSubstitute);
        Assert.StartsWith("Arial", f.Typeface.FamilyName);
        Assert.True(f.Typeface.FontStyle.Weight >= 800, $"weight {f.Typeface.FontStyle.Weight}");
    }

    [Theory]
    [InlineData("Arial Black", "Arial", 900, 5)]
    [InlineData("Gill Sans Ultra Bold", "Gill Sans", 800, 5)]
    [InlineData("Arial Narrow", "Arial", 400, 3)]
    [InlineData("Segoe UI Semibold", "Segoe UI", 600, 5)]
    [InlineData("Copperplate Gothic Bold", "Copperplate Gothic", 700, 5)]
    [InlineData("Amasis MT Pro Black", "Amasis MT Pro", 900, 5)]
    public void FamilyName_SplitsIntoBaseWeightAndWidth(string name, string baseName, int weight, int width)
    {
        var (b, w, wd) = FontResolver.SplitStyle(name)!.Value;
        Assert.Equal((baseName, weight, width), (b, w, wd));
    }

    [Theory]
    [InlineData("Segoe UI Light", @"C:\Windows\Fonts\segoeuil.ttf")]
    [InlineData("Arial Narrow", @"C:\Windows\Fonts\ARIALNB.TTF")]
    public void StyledFaceName_InBold_IsNotASubstitute(string face, string fontFile)
    {
        // A bolded "Light" or "Narrow" face is judged against the weight asked for (700), not the name's (300/400).
        if (!File.Exists(fontFile)) return;   // face not installed: passes without asserting (xUnit 2 cannot skip at run time)
        var f = new FontResolver().Resolve(face, bold: true, italic: false);
        Assert.False(f.IsSubstitute, $"{face} bold resolved to {f.Typeface.FamilyName}");
        Assert.True(f.Typeface.FontStyle.Weight >= 600, $"weight {f.Typeface.FontStyle.Weight}");
    }

    [Fact]
    public void PlainFamily_HasNoStyleSuffix() => Assert.Null(FontResolver.SplitStyle("Calibri"));

    [Fact]
    public void BoldItalic_IsReflectedInTypeface()
    {
        var f = new FontResolver().Resolve("Arial", bold: true, italic: true);
        Assert.True(f.Typeface.FontStyle.Weight >= (int)SKFontStyleWeight.Bold);
        Assert.NotEqual(SKFontStyleSlant.Upright, f.Typeface.FontStyle.Slant);
    }

    [Fact]
    public void MissingFamily_ReportsSubstitute()
    {
        var f = new FontResolver().Resolve(Missing, false, false);
        Assert.True(f.IsSubstitute);
        Assert.Equal(Missing, f.RequestedFamily);
        Assert.False(string.IsNullOrWhiteSpace(f.Typeface.FamilyName));
        Assert.NotEqual(Missing, f.Typeface.FamilyName, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingFont_WarningNamesFamilyAndSubstitute_Once()
    {
        var ctx = Fixtures.Context();
        var page = new Page(200, 100)
        {
            Elements =
            [
                Text("one", Missing), Text("two", Missing),   // same missing font twice: one warning
            ],
        };

        using var _ = Exporter.RenderBitmap(page, 72, ctx);

        var w = Assert.Single(ctx.Warnings);
        Assert.Equal(RenderWarning.FontSubstituted, w.Code);
        Assert.Contains(Missing, w.Message);
        var substitute = new FontResolver().Resolve(Missing, false, false).Typeface.FamilyName;
        Assert.Contains(substitute, w.Message);
    }

    [Fact]
    public void MissingImage_DrawsPlaceholderAndWarns()
    {
        var ctx = Fixtures.Context();
        var page = new Page(200, 200)
        {
            Elements = [new ImageElement { Bounds = new Box(0, 0, 200, 200), Source = "assets/does-not-exist.png" }],
        };

        using var bmp = Exporter.RenderBitmap(page, 72, ctx);

        var w = Assert.Single(ctx.Warnings);
        Assert.Equal(RenderWarning.ImageMissing, w.Code);
        Assert.Contains("does-not-exist.png", w.Message);
        // The placeholder is visible: the frame is not left blank white.
        Assert.NotEqual(SKColors.White, bmp.GetPixel(100, 100));
    }

    [Fact]
    public void UnreadableImage_WarnsAsUnreadable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pubsmith-bad-{Guid.NewGuid():N}.png");
        File.WriteAllText(path, "this is not a png");
        try
        {
            var ctx = new RenderContext();
            var page = new Page(50, 50) { Elements = [new ImageElement { Bounds = new Box(0, 0, 50, 50), Source = path }] };
            using var _ = Exporter.RenderBitmap(page, 72, ctx);
            Assert.Equal(RenderWarning.ImageUnreadable, Assert.Single(ctx.Warnings).Code);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void InstalledFont_ProducesNoWarnings()
    {
        var ctx = Fixtures.Context();
        using var _ = Exporter.RenderBitmap(new Page(200, 100) { Elements = [Text("fine", "Arial")] }, 72, ctx);
        Assert.Empty(ctx.Warnings);
    }

    private static TextElement Text(string s, string family) => new()
    {
        Bounds = new Box(0, 0, 200, 50),
        Paragraphs = [new Paragraph { Runs = [new TextRun(s, new TextStyle { Family = family })] }],
    };
}
