using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Pubsmith.Core;
using Pubsmith.PubReader.Contents;
using Pubsmith.PubReader.OfficeArt;
using Pubsmith.PubReader.Quill;
using static Pubsmith.PubReader.Tests.HostileInputTests;

namespace Pubsmith.PubReader.Tests;

// The budget only bounds what it is charged for: every unit of work done per placement must be charged, and the
// output must stay proportional to the units charged. Each repetition below would take minutes, or gigabytes, if
// its work were free; charged, it reaches the budget (or finishes) in well under a few seconds.
public class WorkPerUnitTests
{
    private static readonly CharFormat Plain = new("Arial", 10, false, false, 0, RgbColor.Black, false, false, 0);
    private static readonly ParaFormat Para = new(ParagraphAlign.Left, new LineSpacing(LineSpacingKind.Multiple, 1), 0, 0, 0, 0, 0);

    private static EscherShape Shape(uint? seq, ushort spt = 1, uint flags = 0, IReadOnlyList<EscherShape>? children = null, Dictionary<ushort, byte[]>? complex = null) => new()
    {
        Offset = 0, ShapeId = seq ?? 0, ShapeType = spt, Flags = flags, Seqnum = seq, Anchor = new AnchorEmu(0, 0, 1_270_000, 1_270_000),
        Props = spt == 202 ? new Dictionary<ushort, uint> { [EscherShape.PropFillFlags] = 0x100000, [EscherShape.PropLineFlags] = 0x80000 } : new Dictionary<ushort, uint>(),
        Complex = complex ?? new Dictionary<ushort, byte[]>(), Children = children ?? [],
    };

    private static PubImportResult Repeated(PageInfo page, int times, IReadOnlyList<EscherShape> top, Dictionary<uint, ShapeInfo>? info = null, Dictionary<uint, Story>? stories = null) =>
        PubImporter.Compose("t", new ContentsModel(612 * 12700, 792 * 12700, [page], Enumerable.Repeat(page, times).ToList(), info ?? [], []),
            new ColorResolver([]), top, stories ?? [], [], []);

    private static void ReachesTheBudgetQuickly(Func<PubImportResult> import)
    {
        var sw = Stopwatch.StartNew();
        Assert.Throws<PubFormatException>(() => import());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }

    // ---------- work that used to be free per placement ----------

    [Fact]
    public void DeletedGroupMembers_AreChargedEveryTimeTheGroupIsPlaced()
    {
        var members = Enumerable.Range(0, 20_000).Select(i => Shape(null, flags: 0x8 | 0x2)).ToList();
        var group = Shape(1, 0, 0x1, members);
        ReachesTheBudgetQuickly(() => Repeated(new PageInfo(9, false, null, null, [1]), 10_000, [group]));
    }

    [Fact]
    public void DuplicateEntriesInAPageList_AreEachCharged()
    {
        var page = new PageInfo(9, false, null, null, Enumerable.Repeat(1u, 100_000).ToList());
        ReachesTheBudgetQuickly(() => Repeated(page, 2_000, [Shape(1)]));
    }

    [Fact]
    public void ManyDeletedShapesSharingASeqnum_AreEachCharged()
    {
        var top = Enumerable.Range(0, 20_000).Select(_ => Shape(1, flags: 0x8)).ToArray();
        ReachesTheBudgetQuickly(() => Repeated(new PageInfo(9, false, null, null, [1]), 20_000, top));
    }

    [Fact]
    public void EachParagraphPlacedCostsAUnit()
    {
        // 1,000 one-run paragraphs on a page placed 200 times: 400,000 units with the paragraph unit, 200,000 without.
        var story = new Story(9, Enumerable.Range(0, 1000).Select(_ => new StoryParagraph(Para, [new StoryRun("p", Plain)])).ToList());
        Assert.Throws<PubFormatException>(() => Repeated(new PageInfo(9, false, null, null, [1]), 200, [Shape(1, 202)],
            new() { [1] = new ShapeInfo(1, 0x01, 9, null, null) }, new() { [9] = story }));
    }

    // ---------- strings that used to be repeated or unbounded ----------

    [Fact]
    public void WordArtStrings_AreDecodedOncePerShape_AndTheFontNameIsCapped()
    {
        // A 1,000,000-character font name on WordArt placed 300 times.
        var wordArt = Shape(1, 136, complex: new Dictionary<ushort, byte[]>
        {
            [EscherShape.PropGtextUnicode] = Encoding.Unicode.GetBytes("Hi"),
            [EscherShape.PropGtextFont] = Encoding.Unicode.GetBytes(new string('F', 1_000_000)),
        });
        var before = GC.GetAllocatedBytesForCurrentThread();
        var r = Repeated(new PageInfo(9, false, null, null, [1]), 300, [wordArt]);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 64L << 20, $"allocated {allocated >> 20} MB");
        var family = r.Document.Pages[0].Elements.OfType<TextElement>().Single().Paragraphs[0].Runs[0].Style.Family;
        Assert.Equal(PubImporter.MaxFontName, family.Length);
        Assert.Contains(r.Issues, i => i.Detail.Contains("font name"));
    }

    [Fact]
    public void QuillFontNames_AreCapped()
    {
        // One font whose name is 65,535 characters long, used by the only run.
        var textOffset = 0x18 + 8 + 5 * 24;
        byte[] font = [.. U32(0), .. U32(1), .. new byte[12], .. new byte[4], .. BitConverter.GetBytes((ushort)0xFFFF), .. Encoding.Unicode.GetBytes(new string('N', 0xFFFF)), .. new byte[4]];
        byte[] runRecord = [.. U32(22), 0x24, 0x88, .. U32(16), 0x01, 0x88, .. U32(10), 0x00, 0x20, .. U32(0)];
        byte[] fdpc = [.. BitConverter.GetBytes((ushort)1), .. new byte[6], .. U32((uint)(textOffset + 6)), .. BitConverter.GetBytes((ushort)14), .. runRecord];
        var q = HostileInputTests.Quill([("TEXT", Encoding.Unicode.GetBytes("Hi\r")), ("STRS", [.. U32(1), .. U32(4), .. U32(3)]),
            ("SYID", [.. U32(0), .. U32(1), .. U32(7)]), ("FONT", font), ("FDPC", fdpc)]);

        var run = Assert.Single(Assert.Single(Assert.Single(QuillReader.Read(q, new ColorResolver([0u])).Values).Paragraphs).Runs);

        Assert.Equal(PubImporter.MaxFontName, run.Format.Font.Length);
    }

    [Theory]
    [InlineData(1)]   // text boxes of long runs in a long font
    [InlineData(2)]   // WordArt with long text
    [InlineData(3)]   // many shapes, each with issues
    public void OutputStaysProportionalToTheUnitsCharged(int scenario)
    {
        PubImportResult r = scenario switch
        {
            1 => Repeated(new PageInfo(9, false, null, null, [1]), 20, [Shape(1, 202)], new() { [1] = new ShapeInfo(1, 0x01, 9, null, null) },
                new() { [9] = new Story(9, Enumerable.Range(0, 50).Select(_ => new StoryParagraph(Para,
                    [new StoryRun(new string('é', 3000), Plain with { Font = new string('F', 0xFFFF) })])).ToList()) }),
            2 => Repeated(new PageInfo(9, false, null, null, [1]), 20, [Shape(1, 136, complex: new Dictionary<ushort, byte[]>
                { [EscherShape.PropGtextUnicode] = Encoding.Unicode.GetBytes(new string('é', 50_000)) })]),
            _ => Repeated(new PageInfo(9, false, null, null, Enumerable.Range(1, 200).Select(i => (uint)i).ToList()), 100,
                Enumerable.Range(1, 200).Select(i => Shape((uint)i, flags: 0x40)).ToArray()),
        };
        var json = DocumentJson.Serialize(r.Document).Length + System.Text.Json.JsonSerializer.Serialize(r.Issues).Length;
        Assert.True(r.UnitsPlaced > 0);
        Assert.True(json <= 8192L * r.UnitsPlaced, $"{json} characters of output for {r.UnitsPlaced} units");
    }

    // ---------- reporting granularity ----------

    [Fact]
    public void TwoPageListsNamingTheSameMissingShape_ReportItTwice()
    {
        var a = new PageInfo(1, false, null, null, [77]);
        var b = new PageInfo(2, false, null, null, [77]);
        var r = PubImporter.Compose("t", new ContentsModel(612 * 12700, 792 * 12700, [a, b], [a, b], new Dictionary<uint, ShapeInfo>(), []),
            new ColorResolver([]), [], new Dictionary<uint, Story>(), [], []);
        Assert.Equal(2, r.Issues.Count(i => i.Detail.Contains("has no drawing")));
    }
}
