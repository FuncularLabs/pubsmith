using Pubsmith.PubReader;
using Pubsmith.PubReader.OfficeArt;

namespace Pubsmith.PubReader.Tests;

// Layer 1 (compound file) and layer 2 (OfficeArt records, MS-ODRAW) of the native reader.
public class PackageAndOfficeArtTests
{
    [Fact]
    public void Open_ReadsTheFourStreams()
    {
        var pkg = PubPackage.Open(Fixtures.Pub("geometry", "base"));
        Assert.Equal(new byte[] { 0xE8, 0xAC, 0x2C, 0x00 }, pkg.Contents[..4]);
        Assert.NotEmpty(pkg.EscherStm);
        Assert.NotNull(pkg.EscherDelayStm);
        Assert.NotEmpty(pkg.Quill);
    }

    [Fact]
    public void NotAPubFile_ThrowsPubFormatException()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pubsmith-notpub-{Guid.NewGuid():N}.pub");
        File.WriteAllText(path, "hello, I am not a compound file");
        try
        {
            var ex = Assert.Throws<PubFormatException>(() => PubPackage.Open(path));
            Assert.Contains(Path.GetFileName(path), ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Truncated_ThrowsPubFormatException()
    {
        var bytes = File.ReadAllBytes(Fixtures.Pub("geometry", "base"));
        var path = Path.Combine(Path.GetTempPath(), $"pubsmith-trunc-{Guid.NewGuid():N}.pub");
        File.WriteAllBytes(path, bytes[..(bytes.Length / 3)]);
        try { Assert.Throws<PubFormatException>(() => PubPackage.Open(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingFile_ThrowsPubFormatException()
    {
        Assert.Throws<PubFormatException>(() => PubPackage.Open(Path.Combine(Path.GetTempPath(), "no-such-file.pub")));
    }

    [Fact]
    public void Records_WalkTheDrawingGroupContainer()
    {
        var pkg = PubPackage.Open(Fixtures.Pub("geometry", "base"));
        var first = RecordReader.Read(pkg.EscherStm, 0);
        Assert.Equal(0xF000, first.Type);          // OfficeArtDggContainer
        Assert.True(first.IsContainer);
        Assert.Contains(RecordReader.Children(pkg.EscherStm, first), r => r.Type == 0xF006);   // FDGGBlock
    }

    [Fact]
    public void RectangleShape_HasAnchorRelativeToPageCentre()
    {
        var pkg = PubPackage.Open(Fixtures.Pub("geometry", "base"));
        var rect = Assert.Single(EscherReader.ReadShapes(pkg.EscherStm), s => s.ShapeType == 1 && s.Anchor is { } a && a.Right != a.Left);
        Assert.Equal(Fixtures.Edges(72, 72, 144, 72), (rect.Anchor!.Value.Left, rect.Anchor.Value.Top, rect.Anchor.Value.Right, rect.Anchor.Value.Bottom));
    }

    [Fact]
    public void MovedRectangle_AnchorLeftMoves()
    {
        var pkg = PubPackage.Open(Fixtures.Pub("geometry", "left-100"));
        var rect = Assert.Single(EscherReader.ReadShapes(pkg.EscherStm), s => s.ShapeType == 1 && s.Anchor is { } a && a.Right != a.Left);
        Assert.Equal(Fixtures.Edges(100, 72, 144, 72), (rect.Anchor!.Value.Left, rect.Anchor.Value.Top, rect.Anchor.Value.Right, rect.Anchor.Value.Bottom));
    }

    [Fact]
    public void Rotation_IsFixedPointDegrees()
    {
        var pkg = PubPackage.Open(Fixtures.Pub("geometry", "rot-30"));
        var rect = Assert.Single(EscherReader.ReadShapes(pkg.EscherStm), s => s.ShapeType == 1 && s.Anchor is { } a && a.Right != a.Left);
        Assert.Equal(30.0, rect.RotationDegrees, 3);
    }

    [Fact]
    public void FlipH_IsReadFromShapeFlags()
    {
        var pkg = PubPackage.Open(Fixtures.Pub("geometry", "flip-h"));
        var rect = Assert.Single(EscherReader.ReadShapes(pkg.EscherStm), s => s.ShapeType == 1 && s.Anchor is { } a && a.Right != a.Left);
        Assert.True(rect.FlipH);
        Assert.False(rect.FlipV);
    }

    [Fact]
    public void FillColour_IsReadAsRgb()
    {
        var pkg = PubPackage.Open(Fixtures.Pub("fill", "fill-red"));
        var rect = Assert.Single(EscherReader.ReadShapes(pkg.EscherStm), s => s.ShapeType == 1 && s.Anchor is { } a && a.Right != a.Left);
        var c = rect.FillColor;
        Assert.NotNull(c);
        Assert.False(c!.Value.IsSchemeIndex);
        Assert.Equal((255, 0, 0), (c.Value.R, c.Value.G, c.Value.B));
    }

    [Fact]
    public void Pictures_ResolveToTheirOwnImages()
    {
        var pkg = PubPackage.Open(Fixtures.Pub("picture", "two"));
        var blips = EscherReader.ReadBlipStore(pkg.EscherStm, pkg.EscherDelayStm);
        var pics = EscherReader.ReadShapes(pkg.EscherStm).Where(s => s.PictureIndex is not null).ToList();
        Assert.Equal(2, pics.Count);
        foreach (var p in pics)
        {
            var blip = blips[p.PictureIndex!.Value - 1];   // pib is 1-based
            Assert.NotNull(blip);
            Assert.Equal("png", blip!.Extension);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, blip.Data[..4]);
        }
    }
}
