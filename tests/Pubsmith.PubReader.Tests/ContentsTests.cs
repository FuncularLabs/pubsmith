using Pubsmith.PubReader;
using Pubsmith.PubReader.Contents;
using Pubsmith.PubReader.OfficeArt;

namespace Pubsmith.PubReader.Tests;

// Layer 3: Publisher's private "Contents" stream: chunk directory, document, pages, masters, shape links.
public class ContentsTests
{
    private static (ContentsModel Contents, IReadOnlyList<EscherShape> Shapes) Load(string group, string variant)
    {
        var pkg = PubPackage.Open(Fixtures.Pub(group, variant));
        return (ContentsReader.Read(pkg.Contents), EscherReader.ReadShapes(pkg.EscherStm));
    }

    [Theory]
    [InlineData("geometry", "base", 612, 792)]
    [InlineData("pages", "size-5.75x4.75", 414, 342)]
    public void PageSize_IsReadInEmu(string group, string variant, double w, double h)
    {
        var (c, _) = Load(group, variant);
        Assert.Equal(w * 12700, c.PageWidthEmu);
        Assert.Equal(h * 12700, c.PageHeightEmu);
    }

    [Fact]
    public void OnePageDocument_HasOneContentPage_ListingTheRectangle()
    {
        var (c, shapes) = Load("geometry", "base");
        var page = Assert.Single(c.ContentPages);
        var rect = Assert.Single(shapes, s => s.ShapeType == 1 && s.Anchor is { } a && a.Right != a.Left);
        Assert.NotNull(rect.Seqnum);
        Assert.Contains(rect.Seqnum!.Value, page.ShapeSeqnums);
    }

    [Fact]
    public void SecondPage_OwnsTheSecondRectangle()
    {
        var (c, shapes) = Load("pages", "two-rect-p2");
        Assert.Equal(2, c.ContentPages.Count);
        var red = Assert.Single(shapes, s => s.FillColor is { R: 255, G: 0, B: 0 });
        var blue = Assert.Single(shapes, s => s.FillColor is { R: 0, G: 0, B: 255 });
        Assert.Contains(blue.Seqnum!.Value, c.ContentPages[0].ShapeSeqnums);
        Assert.Contains(red.Seqnum!.Value, c.ContentPages[1].ShapeSeqnums);
        Assert.DoesNotContain(red.Seqnum!.Value, c.ContentPages[0].ShapeSeqnums);
    }

    [Fact]
    public void MasterPage_IsIdentified_AndAppliedToThePage()
    {
        var (c, shapes) = Load("pages", "master-rect");
        var page = Assert.Single(c.ContentPages);
        Assert.NotNull(page.MasterSeqnum);
        var master = Assert.Single(c.Pages, p => p.Seqnum == page.MasterSeqnum);
        Assert.True(master.IsMaster);
        var green = Assert.Single(shapes, s => s.FillColor is { R: 0, G: 200, B: 0 });
        Assert.Contains(green.Seqnum!.Value, master.ShapeSeqnums);
    }

    [Fact]
    public void TextBox_ShapeChunkCarriesATextId()
    {
        var (c, shapes) = Load("text", "base");
        var tb = Assert.Single(shapes, s => s.ShapeType == 202);
        Assert.True(c.Shapes.TryGetValue(tb.Seqnum!.Value, out var info));
        Assert.NotNull(info!.TextId);
    }

    [Fact]
    public void UnknownBlockType_FailsLoudly()
    {
        // id 0x01, type 0x03 (not in the size table): the reader must refuse rather than guess a size.
        var bytes = new byte[] { 0x01, 0x03, 0, 0, 0, 0 };
        Assert.Throws<PubFormatException>(() => Blocks.ReadAt(bytes, 0));
    }

    [Fact]
    public void Palette_IsRead()
    {
        var (c, _) = Load("fill", "scheme-accent1");
        Assert.NotEmpty(c.Palette);
    }
}
