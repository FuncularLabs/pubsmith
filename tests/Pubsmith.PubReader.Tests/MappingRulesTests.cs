using System.Text.Json;
using Pubsmith.Core;
using Pubsmith.PubReader.Contents;
using Pubsmith.PubReader.OfficeArt;
using Pubsmith.PubReader.Quill;

namespace Pubsmith.PubReader.Tests;

// Mapping rules pinned one at a time on synthetic shapes (PubImporter.ConvertOne / Compose), so each rule has a
// test that fails when the rule is changed, and on Publisher-built fixtures where a variant isolates the rule.
public class MappingRulesTests
{
    private static AnchorEmu A(double x, double y, double w, double h)
    {
        var (l, t, r, b) = Fixtures.Edges(x, y, w, h);
        return new AnchorEmu(l, t, r, b);
    }

    private static EscherShape Shape(ushort spt, uint seq = 1, Dictionary<ushort, uint>? props = null, uint flags = 0, AnchorEmu? anchor = null,
        IReadOnlyDictionary<ushort, uint>? tertiary = null, IReadOnlyList<EscherShape>? children = null, AnchorEmu? childAnchor = null,
        AnchorEmu? groupCs = null, Dictionary<ushort, byte[]>? complex = null, bool truncated = false) => new()
    {
        Offset = 0, ShapeId = seq, ShapeType = spt, Flags = flags, Props = props ?? [], Complex = complex ?? [], Seqnum = seq,
        Anchor = anchor ?? A(72, 72, 144, 72), Tertiary = tertiary ?? new Dictionary<ushort, uint>(), Children = children ?? [],
        ChildAnchor = childAnchor, GroupCoordinates = groupCs, Truncated = truncated,
    };

    private const uint FlipH = 0x40, Deleted = 0x08, Group = 0x01;
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47];

    // ---------- fills, lines, flips ----------

    [Theory]
    [InlineData(0x4000u, 64)]    // 25 %
    [InlineData(0xC000u, 191)]   // 75 %: an inverted opacity would give 64
    public void FillOpacity_BecomesAlpha(uint opacity, byte alpha)
    {
        var (els, _) = PubImporter.ConvertOne(Shape(1, props: new() { [EscherShape.PropFillColor] = 0x0000FF, [EscherShape.PropFillOpacity] = opacity }));
        Assert.Equal(new Rgba(255, 0, 0, alpha), Assert.IsType<ShapeElement>(Assert.Single(els)).Fill);
    }

    [Theory]
    [InlineData(0x280020u, true)]    // TertiaryFOPT per-side border flags, bit 19 set: bordered
    [InlineData(0x200020u, false)]   // bit 19 clear: no border
    public void LineVisible_FallsBackToTheTertiaryBorderFlags(uint sides, bool visible) =>
        Assert.Equal(visible, PubImporter.LineVisible(Shape(1, tertiary: new Dictionary<ushort, uint> { [0x057F] = sides })));

    [Fact]
    public void LineVisible_PrimaryFlagsWinOverTheTertiary()
    {
        var s = Shape(1, props: new() { [EscherShape.PropLineFlags] = 0x80000 }, tertiary: new Dictionary<ushort, uint> { [0x057F] = 0x280020 });
        Assert.False(PubImporter.LineVisible(s));
        Assert.True(PubImporter.LineVisible(Shape(1)));   // nothing said: Publisher's default line
    }

    [Fact]
    public void Flip_IsReported()
    {
        var (_, issues) = PubImporter.ConvertOne(Shape(1, flags: FlipH));
        Assert.Contains(issues, i => i.Detail == "rectangle: flip ignored");
        Assert.Contains(PubImporter.Import(Fixtures.Pub("geometry", "flip-h")).Issues, i => i.Detail.Contains("flip ignored"));
    }

    [Fact]
    public void InvisibleLine_DrawsNothing_ButIsConverted()
    {
        var (els, issues) = PubImporter.ConvertOne(Shape(20, props: new() { [EscherShape.PropLineFlags] = 0x80000 }));
        var e = Assert.IsType<ShapeElement>(Assert.Single(els));
        Assert.Null(e.Fill);
        Assert.Null(e.Stroke);
        Assert.Empty(issues);
    }

    [Fact]
    public void RotatedLine_TurnsAboutItsCentre()
    {
        // A horizontal line (height 0) rotated 30 degrees: angle 0 + 30.
        var (els, _) = PubImporter.ConvertOne(Shape(20, anchor: A(100, 100, 200, 0), props: new() { [EscherShape.PropRotation] = 30u << 16 }));
        var e = Assert.IsType<ShapeElement>(Assert.Single(els));
        Assert.Equal(30, e.Rotation, 6);
        Assert.Equal(200, e.Bounds.CenterX, 6);
        Assert.Equal(200, e.Bounds.Width, 6);
    }

    [Fact]
    public void DeletedShape_IsNotDrawn()
    {
        var (els, issues) = PubImporter.ConvertOne(Shape(1, flags: Deleted));
        Assert.Empty(els);
        Assert.Empty(issues);
    }

    [Fact]
    public void TruncatedShape_IsReported() =>
        Assert.Contains(PubImporter.ConvertOne(Shape(1, truncated: true)).Issues, i => i.Detail.Contains("truncated"));

    [Fact]
    public void ShapeWithoutPosition_IsReportedAsDropped()
    {
        var s = Shape(1) with { Anchor = null };
        var (els, issues) = PubImporter.ConvertOne(s);
        Assert.Empty(els);
        Assert.Equal(ImportIssueKind.Dropped, Assert.Single(issues).Kind);
    }

    // ---------- pictures ----------

    private static (List<Element>, IReadOnlyList<ImportIssue>) PictureWithCrop(double left, double right)
    {
        var props = new Dictionary<ushort, uint>
        {
            [EscherShape.PropPib] = 1,
            [EscherShape.PropCropLeft] = (uint)(int)Math.Round(left * 65536),
            [EscherShape.PropCropRight] = (uint)(int)Math.Round(right * 65536),
        };
        return PubImporter.ConvertOne(Shape(75, props: props), blips: [new Blip("png", Png)]);
    }

    [Fact]
    public void LargeCrop_IsKept()
    {
        var (els, issues) = PictureWithCrop(0.6, 0);
        Assert.Equal(0.6, Assert.IsType<ImageElement>(Assert.Single(els)).Crop!.Left, 4);
        Assert.Empty(issues);
    }

    [Theory]
    [InlineData(0.7, 0.5)]    // together more than the whole picture
    [InlineData(-0.1, 0.2)]   // an outset
    public void ImpossibleCrop_IsClampedAndReported(double left, double right)
    {
        var (els, issues) = PictureWithCrop(left, right);
        var crop = Assert.IsType<ImageElement>(Assert.Single(els)).Crop!;
        Assert.InRange(crop.Left, 0, 1);
        Assert.True(crop.Left + crop.Right < 1);
        Assert.Contains(issues, i => i.Detail.Contains("crop clamped"));
    }

    [Fact]
    public void DamagedBitmap_IsAPlaceholderSayingSo()
    {
        var (els, issues) = PubImporter.ConvertOne(Shape(75, props: new() { [EscherShape.PropPib] = 1 }), blips: [new Blip("dib", new byte[10])]);
        Assert.Single(els);
        Assert.Contains(issues, i => i.Kind == ImportIssueKind.Placeholder && i.Detail.Contains("damaged"));
    }

    // ---------- WordArt ----------

    private static TextStyle WordArtStyle(Dictionary<ushort, uint> props, out IReadOnlyList<ImportIssue> issues, ushort spt = 136)
    {
        var complex = new Dictionary<ushort, byte[]> { [EscherShape.PropGtextUnicode] = System.Text.Encoding.Unicode.GetBytes("Hi") };
        var (els, iss) = PubImporter.ConvertOne(Shape(spt, props: props, complex: complex));
        issues = iss;
        return Assert.Single(Assert.Single(Assert.IsType<TextElement>(Assert.Single(els)).Paragraphs).Runs).Style;
    }

    [Theory]
    [InlineData(0xFFFF5700u, false, false)]   // Publisher's plain WordArt
    [InlineData(0xFFFF5720u, true, false)]    // created with FontBold (tools/oracle/New-FeatureCorpus.ps1, F18-F19)
    [InlineData(0xFFFF5710u, false, true)]    // italic: MS-ODRAW gtextFItalic
    [InlineData(0x00005730u, false, false)]   // value bits without their use bits count for nothing
    public void WordArtBoldAndItalic_ComeFromTheGeometryTextFlags(uint flags, bool bold, bool italic)
    {
        var style = WordArtStyle(new() { [0x00FF] = flags }, out _);
        Assert.Equal((bold, italic), (style.Bold, style.Italic));
    }

    [Fact]
    public void UnfilledWordArt_HasATransparentFill()
    {
        Assert.Equal(0, WordArtStyle(new() { [EscherShape.PropFillFlags] = 0x100000 }, out _).Color.A);
        Assert.Equal(255, WordArtStyle(new() { [EscherShape.PropFillFlags] = 0x100010 }, out _).Color.A);
    }

    [Fact]
    public void WordArtFillTransparency_IsKept() =>
        Assert.Equal(128, WordArtStyle(new() { [EscherShape.PropFillOpacity] = 0x8000 }, out _).Color.A);

    [Fact]
    public void ButtonWarp_IsReportedAsAnArch()
    {
        WordArtStyle([], out var issues, spt: 147);
        Assert.Contains(issues, i => i.Detail.Contains("button warp"));
    }

    // ---------- groups ----------

    [Fact]
    public void ChildBox_ScalesEachAxisOnItsOwn()
    {
        // Group coordinates 1000 x 2000 onto a 100 x 100 frame: x scale 0.1, y scale 0.05.
        var box = PubImporter.ChildBox(new AnchorEmu(500, 500, 1000, 2000), new AnchorEmu(0, 0, 1000, 2000), new Box(10, 20, 100, 100));
        Assert.Equal(new Box(60, 45, 50, 75), box);
    }

    [Fact]
    public void ChildBox_UsesTheGroupOrigin()
    {
        var box = PubImporter.ChildBox(new AnchorEmu(1100, 2200, 1200, 2300), new AnchorEmu(1000, 2000, 2000, 3000), new Box(0, 0, 100, 100));
        Assert.Equal(new Box(10, 20, 10, 10), box);
    }

    private static EscherShape TwoMemberGroup(uint flags, int rotation) => Shape(0, seq: 1, flags: Group | flags,
        anchor: A(100, 100, 200, 100), groupCs: new AnchorEmu(0, 0, 200, 100),
        props: rotation == 0 ? null : new() { [EscherShape.PropRotation] = (uint)(rotation << 16) },
        children:
        [
            Shape(1, seq: 2, childAnchor: new AnchorEmu(0, 0, 50, 50)) with { Anchor = null },       // top-left corner
            Shape(1, seq: 3, childAnchor: new AnchorEmu(150, 50, 200, 100)) with { Anchor = null },  // bottom-right corner
        ]);

    [Fact]
    public void RotatedGroup_TurnsItsMembersAboutItsCentre()
    {
        var (els, _) = PubImporter.ConvertOne(TwoMemberGroup(0, 180));
        // Group centre (200, 150). Turned 180 degrees, the top-left member lands bottom-right and vice versa.
        var a = Assert.IsType<ShapeElement>(els[0]);
        var b = Assert.IsType<ShapeElement>(els[1]);
        Assert.Equal((275, 175), (Math.Round(a.Bounds.CenterX, 6), Math.Round(a.Bounds.CenterY, 6)));
        Assert.Equal((125, 125), (Math.Round(b.Bounds.CenterX, 6), Math.Round(b.Bounds.CenterY, 6)));
        Assert.Equal(180, a.Rotation, 6);
    }

    [Fact]
    public void RotatedGroup_TurnsClockwise()
    {
        // Group centre (200, 150). The top-left member's centre (125, 125) is 75 left and 25 up of it; turned 30
        // degrees clockwise (y down) it lands at (147.55, 90.85). Counter-clockwise would give (122.55, 165.85).
        var (els, _) = PubImporter.ConvertOne(TwoMemberGroup(0, 30));
        Assert.Equal(147.548, els[0].Bounds.CenterX, 3);
        Assert.Equal(90.849, els[0].Bounds.CenterY, 3);
        Assert.Equal(252.452, els[1].Bounds.CenterX, 3);   // the other member, mirrored through the centre
        Assert.Equal(209.151, els[1].Bounds.CenterY, 3);
        Assert.Equal(30, els[0].Rotation, 6);
    }

    [Fact]
    public void RotatedGroupOfUnsupportedShapes_IsCountedAsPlaceheld()
    {
        // Turning a member makes a new element; it must stay a placeholder for the accounting.
        var group = Shape(0, seq: 1, flags: Group, anchor: A(100, 100, 200, 100), groupCs: new AnchorEmu(0, 0, 200, 100),
            props: new() { [EscherShape.PropRotation] = 30u << 16 },
            children: [Shape(5, seq: 2, childAnchor: new AnchorEmu(0, 0, 50, 50)) with { Anchor = null }]);
        var page = new PageInfo(9, false, null, null, [1]);
        var contents = new ContentsModel(612 * 12700, 792 * 12700, [page], [page], new Dictionary<uint, ShapeInfo>(), []);
        var r = PubImporter.Compose("t", contents, new ColorResolver([]), [group], new Dictionary<uint, Story>(), [], []);
        Assert.Equal((1, 0, 1), (r.ShapesRead, r.ShapesConverted, r.ShapesPlaceheld));
    }

    [Fact]
    public void FlippedGroup_MirrorsItsMembers_AndSaysTheyAreNotFlipped()
    {
        var (els, issues) = PubImporter.ConvertOne(TwoMemberGroup(FlipH, 0));
        Assert.Equal(275, els[0].Bounds.CenterX, 6);   // the left member is now on the right
        Assert.Equal(125, els[0].Bounds.CenterY, 6);
        Assert.Contains(issues, i => i.Detail.StartsWith("flipped group"));
    }

    [Fact]
    public void EmptyGroup_IsReportedAsDropped()
    {
        var (els, issues) = PubImporter.ConvertOne(Shape(0, flags: Group));
        Assert.Empty(els);
        Assert.Equal(ImportIssueKind.Dropped, Assert.Single(issues).Kind);
    }

    [Fact]
    public void RotatedGroup_FillsTheFramePublisherExported()
    {
        // Publisher exported this 30-degree group as one picture whose frame is the group's rotated bounds
        // (truth/group__rotated.json). The turned members must fill that frame.
        using var truth = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures.Dir, "truth", "group__rotated.json")));
        var b = truth.RootElement.GetProperty("pages")[0].GetProperty("elements")[0].GetProperty("bounds");
        double tx = b.GetProperty("x").GetDouble(), ty = b.GetProperty("y").GetDouble(), tw = b.GetProperty("width").GetDouble(), th = b.GetProperty("height").GetDouble();

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var e in PubImporter.Import(Fixtures.Pub("group", "rotated")).Document.Pages[0].Elements)
        {
            var rad = e.Rotation * Math.PI / 180;
            foreach (var (dx, dy) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
            {
                double x = dx * e.Bounds.Width / 2, y = dy * e.Bounds.Height / 2;
                double px = e.Bounds.CenterX + x * Math.Cos(rad) - y * Math.Sin(rad), py = e.Bounds.CenterY + x * Math.Sin(rad) + y * Math.Cos(rad);
                (minX, minY, maxX, maxY) = (Math.Min(minX, px), Math.Min(minY, py), Math.Max(maxX, px), Math.Max(maxY, py));
            }
        }
        // Our extents are geometry only; Publisher's picture also holds the members' lines and an edge, so ours sit
        // just inside it (about 1.4 pt a side when this was written): same centre, each side within 2 pt.
        // Members left unrotated would span the unrotated group frame instead, far outside these bounds.
        Assert.InRange((minX + maxX) / 2, tx + tw / 2 - 0.3, tx + tw / 2 + 0.3);
        Assert.InRange((minY + maxY) / 2, ty + th / 2 - 0.3, ty + th / 2 + 0.3);
        Assert.InRange(maxX - minX, tw - 4, tw);
        Assert.InRange(maxY - minY, th - 4, th);
    }

    // ---------- text boxes ----------

    [Fact]
    public void VerticalAnchorBottom_IsCarried()
    {
        var t = Assert.IsType<TextElement>(Assert.Single(PubImporter.Import(Fixtures.Pub("text", "vanchor-bottom")).Document.Pages[0].Elements));
        Assert.Equal(TextVerticalAlign.Bottom, t.VerticalAlign);
    }

    [Fact]
    public void EmptyParagraphRun_RaisesNoFormattingNotes()
    {
        // The run that only carries an empty paragraph's height is underlined here: nothing visible is lost.
        var format = new CharFormat("Arial", 12, false, false, Underline: 1, RgbColor.Black, false, false, 0);
        var para = new ParaFormat(ParagraphAlign.Left, new LineSpacing(LineSpacingKind.Multiple, 1), 0, 0, 0, 0, 0);
        var story = new Story(9, [new StoryParagraph(para, [new StoryRun("", format)])]);
        var bare = Shape(202, props: new() { [EscherShape.PropFillFlags] = 0x100000, [EscherShape.PropLineFlags] = 0x80000 });   // no fill, no line
        var (els, issues) = PubImporter.ConvertOne(bare, new ShapeInfo(1, 0x01, 9, null, null), stories: new Dictionary<uint, Story> { [9] = story });
        Assert.Single(Assert.Single(Assert.IsType<TextElement>(Assert.Single(els)).Paragraphs).Runs);
        Assert.Empty(issues);
    }

    // ---------- page composition ----------

    [Fact]
    public void Backgrounds_SitBelowEveryShape_MasterShapesIncluded()
    {
        static EscherShape Filled(uint seq, uint rgb) => Shape(1, seq, new() { [EscherShape.PropFillColor] = rgb });
        var master = new PageInfo(1, IsMaster: true, MasterSeqnum: null, BackgroundSeqnum: 10, ShapeSeqnums: [11]);
        var page = new PageInfo(2, IsMaster: false, MasterSeqnum: 1, BackgroundSeqnum: 20, ShapeSeqnums: [21]);
        var contents = new ContentsModel(612 * 12700, 792 * 12700, [master, page], [page], new Dictionary<uint, ShapeInfo>(), []);
        EscherShape[] top = [Filled(10, 0x0000FF), Filled(11, 0xFF0000), Filled(20, 0x00FF00), Filled(21, 0x00FFFF)];

        var r = PubImporter.Compose("t", contents, new ColorResolver([]), top, new Dictionary<uint, Story>(), [], []);

        var fills = r.Document.Pages[0].Elements.Cast<ShapeElement>().Select(e => e.Fill).ToList();
        Assert.Equal([new Rgba(255, 0, 0), new Rgba(0, 255, 0), new Rgba(0, 0, 255), new Rgba(255, 255, 0)], fills);   // master bg, page bg, master shape, page shape
        Assert.Equal(new Box(0, 0, 612, 792), r.Document.Pages[0].Elements[1].Bounds);
    }

    [Fact]
    public void PageListsAShapeTwice_ItIsDrawnOnce()
    {
        var page = new PageInfo(2, false, null, null, [21, 21]);
        var contents = new ContentsModel(612 * 12700, 792 * 12700, [page], [page], new Dictionary<uint, ShapeInfo>(), []);
        var r = PubImporter.Compose("t", contents, new ColorResolver([]), [Shape(1, 21)], new Dictionary<uint, Story>(), [], []);
        Assert.Single(r.Document.Pages[0].Elements);
        Assert.Equal(1, r.ShapesRead);
    }

    // ---------- colours ----------

    [Theory]
    [InlineData(0x000000FFu, false)]   // literal red
    [InlineData(0x020000FFu, false)]   // fPaletteRGB: still RGB
    [InlineData(0x040000FFu, false)]   // fSystemRGB: still RGB
    [InlineData(0x01000003u, true)]    // a palette index, not a colour
    [InlineData(0x20000003u, true)]    // unknown flags
    public void ColourFlags_OtherThanRgb_AreApproximate(uint reference, bool approximate) =>
        Assert.Equal(approximate, new ColorResolver([]).Resolve(reference).Approximate);
}
