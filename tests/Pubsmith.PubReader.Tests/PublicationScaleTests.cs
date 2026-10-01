using System.Buffers.Binary;
using System.Text;
using Pubsmith.Core;
using Pubsmith.PubReader.Contents;
using Pubsmith.PubReader.OfficeArt;
using Pubsmith.PubReader.Quill;
using static Pubsmith.PubReader.Tests.HostileInputTests;

namespace Pubsmith.PubReader.Tests;

// The placement budget has two sides, and both are tested here. Ordinary publications, however long, must import
// (a budget that charges them too much is a bug); hostile repetition of one small thing must hit the budget or stay
// small (a budget that misses a path is a bug).
public class PublicationScaleTests
{
    private const double Pt = 12700;
    private static readonly CharFormat Plain = new("Arial", 10, false, false, 0, RgbColor.Black, false, false, 0);
    private static readonly ParaFormat Para = new(ParagraphAlign.Left, new LineSpacing(LineSpacingKind.Multiple, 1), 0, 0, 0, 0, 0);

    private static Story StoryOf(uint id, int paragraphs, int chars = 150) =>
        new(id, Enumerable.Range(0, paragraphs).Select(_ => new StoryParagraph(Para, [new StoryRun(new string('t', chars), Plain)])).ToList());

    private static EscherShape Shape(uint seq, ushort spt = 1, bool positioned = true, IReadOnlyList<EscherShape>? children = null, uint flags = 0) => new()
    {
        Offset = 0, ShapeId = seq, ShapeType = spt, Flags = flags, Seqnum = seq,
        Anchor = positioned ? new AnchorEmu(0, 0, (int)(100 * Pt), (int)(100 * Pt)) : null,
        Props = spt == 202 ? new Dictionary<ushort, uint> { [EscherShape.PropFillFlags] = 0x100000, [EscherShape.PropLineFlags] = 0x80000 } : new Dictionary<ushort, uint>(),
        Complex = new Dictionary<ushort, byte[]>(), Children = children ?? [],
    };

    private static PubImportResult Compose(IReadOnlyList<PageInfo> allPages, IReadOnlyList<PageInfo> contentPages, IReadOnlyList<EscherShape> top,
        Dictionary<uint, ShapeInfo>? shapes = null, Dictionary<uint, Story>? stories = null) =>
        PubImporter.Compose("t", new ContentsModel(612 * 12700, 792 * 12700, allPages, contentPages, shapes ?? [], []),
            new ColorResolver([]), top, stories ?? [], [], []);

    // ---------- ordinary publications import ----------

    [Fact]
    public void ABookWithOneStoryLinkedAcross300Pages_Imports_WithTheStoryPlacedOnce()
    {
        // One linked text box per page (300 different boxes), all sharing one 3,000-paragraph story.
        var boxes = Enumerable.Range(1, 300).Select(i => Shape((uint)i, 202)).ToArray();
        var pages = boxes.Select(b => new PageInfo(1000 + b.Seqnum!.Value, false, null, null, [b.Seqnum!.Value])).ToList();
        var info = boxes.ToDictionary(b => b.Seqnum!.Value, b => new ShapeInfo(b.Seqnum!.Value, 0x01, 9, null, null));

        var r = Compose(pages, pages, boxes, info, new() { [9] = StoryOf(9, 3000) });

        Assert.Equal(300, r.Document.Pages.Count);
        Assert.Equal(3000, r.Document.Pages.SelectMany(p => p.Elements).OfType<TextElement>().Sum(t => t.Paragraphs.Count));   // once, not 300 times
        Assert.Equal(299, r.Issues.Count(i => i.Detail.StartsWith("linked text box")));
    }

    [Fact]
    public void AThousandPagesWithAMaster_Imports()
    {
        // A master with a header bar and a 3-paragraph footer on 1,000 pages, each with its own 20-paragraph story.
        var header = Shape(1);
        var footer = Shape(2, 202);
        var master = new PageInfo(500, true, null, null, [1, 2]);
        var bodies = Enumerable.Range(0, 1000).Select(i => Shape((uint)(10 + i), 202)).ToArray();
        var pages = bodies.Select(b => new PageInfo(2000 + b.Seqnum!.Value, false, 500, null, [b.Seqnum!.Value])).ToList();
        var info = bodies.ToDictionary(b => b.Seqnum!.Value, b => new ShapeInfo(b.Seqnum!.Value, 0x01, 100 + b.Seqnum!.Value, null, null));
        info[2] = new ShapeInfo(2, 0x01, 5, null, null);
        var stories = bodies.ToDictionary(b => 100 + b.Seqnum!.Value, b => StoryOf(100 + b.Seqnum!.Value, 20));
        stories[5] = StoryOf(5, 3, 40);

        var r = Compose([master, .. pages], pages, [header, footer, .. bodies], info, stories);

        Assert.Equal(1000, r.Document.Pages.Count);
        Assert.All(r.Document.Pages, p => Assert.Equal(23, p.Elements.OfType<TextElement>().Sum(t => t.Paragraphs.Count)));   // footer on every page
    }

    [Fact]
    public void AHundredPagesOfTwoHundredShapes_Imports()
    {
        var shapes = Enumerable.Range(1, 20_000).Select(i => Shape((uint)i)).ToArray();
        var pages = Enumerable.Range(0, 100).Select(p => new PageInfo((uint)(30_000 + p), false, null, null, shapes.Skip(p * 200).Take(200).Select(s => s.Seqnum!.Value).ToList())).ToList();
        var r = Compose(pages, pages, shapes);
        Assert.Equal(20_000, r.ShapesRead);
    }

    // ---------- hostile repetition is bounded ----------

    [Fact]
    public void ARepeatedPageListingMissingShapes_HitsTheBudget_WithoutAnIssuePerPlacement()
    {
        // A page naming 2,000 shapes that have no drawing, listed 300 times: each name costs its look-up, and the
        // missing ones are reported once for that page, not once per placement.
        var page = new PageInfo(2, false, null, null, Enumerable.Range(1, 2000).Select(i => (uint)i).ToList());
        var once = Compose([page], [page], []);
        Assert.Equal(2000, once.Issues.Count(i => i.Detail.Contains("has no drawing")));
        Assert.Throws<PubFormatException>(() => Compose([page], Enumerable.Repeat(page, 300).ToList(), []));
    }

    [Fact]
    public void AMasterNamingMissingShapes_IsReportedOncePerShape_NotPerPage()
    {
        var master = new PageInfo(500, true, null, null, Enumerable.Range(1, 10).Select(i => (uint)i).ToList());
        var pages = Enumerable.Range(0, 100).Select(i => new PageInfo((uint)(1000 + i), false, 500, null, [])).ToList();
        var r = Compose([master, .. pages], pages, []);
        Assert.Equal(10, r.Issues.Count(i => i.Detail.Contains("has no drawing")));
    }

    [Fact]
    public void IssuesCountAgainstTheBudget()
    {
        // Shapes without a position each convert to nothing but an issue. 25,000 placements of a page of four:
        // 225,000 units for pages, look-ups and conversions alone (under the budget), 325,000 with their issues.
        EscherShape[] lost = [Shape(1, positioned: false), Shape(2, positioned: false), Shape(3, positioned: false), Shape(4, positioned: false)];
        var page = new PageInfo(9, false, null, null, [1, 2, 3, 4]);
        Assert.Throws<PubFormatException>(() => Compose([page], Enumerable.Repeat(page, 25_000).ToList(), lost));
    }

    [Fact]
    public void EmptyGroupsRepeated_CountTheirConversions()
    {
        // Four empty groups per page, 25,000 pages: 325,000 units with each group's conversion, 225,000 without.
        EscherShape[] groups = [Shape(1, 0, flags: 1), Shape(2, 0, flags: 1), Shape(3, 0, flags: 1), Shape(4, 0, flags: 1)];
        var page = new PageInfo(9, false, null, null, [1, 2, 3, 4]);
        Assert.Throws<PubFormatException>(() => Compose([page], Enumerable.Repeat(page, 25_000).ToList(), groups));
    }

    [Fact]
    public void ALongTextBoxRepeatedOnManyPages_CountsItsCharacters()
    {
        // One 102,400-character run (100 units for its characters) in a text box on a page listed 2,500 times.
        var box = Shape(1, 202);
        var page = new PageInfo(9, false, null, null, [1]);
        var story = new Story(9, [new StoryParagraph(Para, [new StoryRun(new string('c', 102_400), Plain)])]);
        Assert.Throws<PubFormatException>(() => Compose([page], Enumerable.Repeat(page, 2500).ToList(), [box],
            new() { [1] = new ShapeInfo(1, 0x01, 9, null, null) }, new() { [9] = story }));
    }

    // ---------- damage granularity and laziness (round-3 pins) ----------

    [Fact]
    public void DamagedChildOfADrawingContainer_IsCounted_AndTheRestKept()
    {
        static byte[] Rec(int ver, ushort type, byte[] body)
        {
            var b = new byte[8 + body.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)ver);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), type);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)body.Length);
            body.CopyTo(b, 8);
            return b;
        }
        var good = Rec(0xF, 0xF004, Rec(0x12, 0xF00A, [.. U32(1), .. U32(0)]));
        var bad = Rec(0xF, 0xF004, []);
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(4), 10_000);
        var stm = Rec(0xF, 0xF002, [.. good, .. bad]);

        var top = EscherReader.ReadTopLevel(stm, out var damaged);

        Assert.Equal(1u, Assert.Single(top).ShapeId);
        Assert.Equal(1, damaged);
    }

    [Fact]
    public void DamagedColourInAStyle_DoesNotCostTextWhoseRunsNameTheirColour()
    {
        // Style 0's colour container (0x44) holds an unknown block type; the only run has its own colour (0x2E).
        var textOffset = 0x18 + 8 + 7 * 24;
        byte[] colours = [.. U32(1), .. new byte[8], .. U32(10), 0x01, 0x20, .. U32(0x0000FF)];   // one entry: red
        byte[] damagedStyle = [.. U32(12), 0x44, 0x88, .. U32(6), 0x00, 0xFF];
        byte[] stsh = [.. U32(0), .. U32(2), .. new byte[12], .. U32(6), .. U32(18), .. damagedStyle, .. U32(4)];
        byte[] runRecord = [.. U32(10), 0x2E, 0x20, .. U32(0)];
        byte[] fdpc = [.. BitConverter.GetBytes((ushort)1), .. new byte[6], .. U32((uint)(textOffset + 6)), .. BitConverter.GetBytes((ushort)14), .. runRecord];
        var q = HostileInputTests.Quill([("TEXT", Encoding.Unicode.GetBytes("Hi\r")), ("STRS", [.. U32(1), .. U32(4), .. U32(3)]),
            ("SYID", [.. U32(0), .. U32(1), .. U32(7)]), ("PL  ", colours), ("STSH", stsh), ("STSH", stsh), ("FDPC", fdpc)]);

        var run = Assert.Single(Assert.Single(Assert.Single(QuillReader.Read(q, new ColorResolver([0u])).Values).Paragraphs).Runs);

        Assert.Equal("Hi", run.Text);
        Assert.Equal((255, 0, 0), (run.Format.Color.R, run.Format.Color.G, run.Format.Color.B));
    }
}
