using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pubsmith.Core;
using SkiaSharp;

namespace Pubsmith.Rendering.Tests;

// AC2 (PDF) and AC3 (PNG).
public class ExportTests
{
    [Theory]
    [InlineData(72, 414, 342)]
    [InlineData(150, 863, 713)]
    [InlineData(300, 1725, 1425)]
    public void PixelSize_MatchesDpi(double dpi, int expectedW, int expectedH)
    {
        using var ms = new MemoryStream();
        Exporter.ToPng(new Page(414, 342), dpi, new RenderContext(), ms);

        ms.Position = 0;
        using var bmp = SKBitmap.Decode(ms);
        Assert.Equal(expectedW, bmp.Width);
        Assert.Equal(expectedH, bmp.Height);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(5000)]
    public void InvalidDpi_Throws(double dpi)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Exporter.RenderBitmap(new Page(10, 10), dpi, new RenderContext()));
    }

    [Fact]
    public void Png_BackgroundIsOpaqueWhite()
    {
        using var bmp = Exporter.RenderBitmap(new Page(10, 10), 72, new RenderContext());
        Assert.Equal(SKColors.White, bmp.GetPixel(5, 5));
    }

    [Fact]
    public void MediaBox_EqualsPageSize()
    {
        var doc = new PubsmithDocument { Pages = [new Page(414, 342), new Page(612, 792)] };
        var pdf = PdfText(doc);

        var boxes = Regex.Matches(pdf, @"/MediaBox\s*\[\s*([\d.\-]+)\s+([\d.\-]+)\s+([\d.\-]+)\s+([\d.\-]+)\s*\]")
            .Select(m => Enumerable.Range(1, 4).Select(i => double.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture)).ToArray())
            .ToList();

        Assert.Equal(2, boxes.Count);
        Assert.Contains(boxes, b => Math.Abs(b[2] - b[0] - 414) < 0.01 && Math.Abs(b[3] - b[1] - 342) < 0.01);
        Assert.Contains(boxes, b => Math.Abs(b[2] - b[0] - 612) < 0.01 && Math.Abs(b[3] - b[1] - 792) < 0.01);
    }

    [Fact]
    public void TextPage_EmbedsFontFile()
    {
        var doc = new PubsmithDocument
        {
            Pages =
            [
                new Page(300, 100)
                {
                    Elements = [new TextElement { Bounds = new Box(10, 10, 280, 80), Paragraphs = [new Paragraph { Runs = [new TextRun("Embedded?", new TextStyle { Family = "Arial" })] }] }],
                },
            ],
        };
        var pdf = PdfText(doc);
        Assert.Matches(@"/FontFile[23]?\b", pdf);
    }

    [Fact]
    public void EmptyDocument_Throws()
    {
        Assert.Throws<ArgumentException>(() => Exporter.ToPdf(new PubsmithDocument(), new RenderContext(), new MemoryStream()));
    }

    [Fact]
    public void PdfWarnings_AreCollected()
    {
        var ctx = new RenderContext();
        var doc = new PubsmithDocument
        {
            Pages = [new Page(50, 50) { Elements = [new ImageElement { Bounds = new Box(0, 0, 50, 50), Source = "nowhere.png" }] }],
        };
        Exporter.ToPdf(doc, ctx, new MemoryStream());
        Assert.Equal(RenderWarning.ImageMissing, Assert.Single(ctx.Warnings).Code);
    }

    private static string PdfText(PubsmithDocument doc)
    {
        using var ms = new MemoryStream();
        Exporter.ToPdf(doc, new RenderContext(), ms);
        return Encoding.Latin1.GetString(ms.ToArray());
    }
}
