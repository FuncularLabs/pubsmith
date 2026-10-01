using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using OpenMcdf;
using Pubsmith.Core;
using Pubsmith.PubReader.Contents;
using Pubsmith.PubReader.OfficeArt;
using Pubsmith.PubReader.Quill;
using static Pubsmith.PubReader.Tests.HostileInputTests;

namespace Pubsmith.PubReader.Tests;

// Second set of aggregate and damage-granularity pins: work that produces nothing still counts, text placed many
// times counts by its size, damage inside groups is counted and costs only what it must, and caches keep apart what
// must stay apart.
public class AggregateLimitsRound3Tests
{
    private static byte[] Rec(int ver, int inst, ushort type, byte[] body)
    {
        var b = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)(ver | (inst << 4)));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), type);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)body.Length);
        body.CopyTo(b, 8);
        return b;
    }

    /// <summary>A shape container holding only its FSP record (shape id, flags).</summary>
    private static byte[] Sp(uint id, uint flags) => Rec(0xF, 0, 0xF004, Rec(2, 1, 0xF00A, [.. U32(id), .. U32(flags)]));

    private static PubImportResult OnRepeatedPage(EscherShape[] top, int times, Dictionary<uint, ShapeInfo>? shapes = null, Dictionary<uint, Story>? stories = null)
    {
        var page = new PageInfo(2, false, null, null, top.Select(s => s.Seqnum!.Value).ToList());
        var contents = new ContentsModel(612 * 12700, 792 * 12700, [page], Enumerable.Repeat(page, times).ToList(), shapes ?? [], []);
        return PubImporter.Compose("t", contents, new ColorResolver([]), top, stories ?? [], [], []);
    }

    private static EscherShape Shape(uint seq, ushort spt = 1, AnchorEmu? anchor = null, Dictionary<ushort, uint>? props = null) => new()
    {
        Offset = 0, ShapeId = seq, ShapeType = spt, Flags = 0, Seqnum = seq, Anchor = anchor,
        Props = props ?? [], Complex = new Dictionary<ushort, byte[]>(),
    };

    // ---------- placement budget ----------

    [Fact]
    public void ShapesThatDrawNothing_StillCountAgainstTheCap()
    {
        // Four shapes without a position on a page listed 100,000 times: 400,000 conversions, nothing drawn.
        EscherShape[] top = [Shape(1), Shape(2), Shape(3), Shape(4)];
        var ex = Assert.Throws<PubFormatException>(() => OnRepeatedPage(top, 100_000));
        Assert.Contains("places more than", ex.Message);
    }

    [Fact]
    public void ALongStoryRepeatedOnManyPages_CountsByItsSize()
    {
        // One 1,000-paragraph story in a text box on a page listed 1,000 times.
        var format = new CharFormat("Arial", 10, false, false, 0, RgbColor.Black, false, false, 0);
        var para = new ParaFormat(ParagraphAlign.Left, new LineSpacing(LineSpacingKind.Multiple, 1), 0, 0, 0, 0, 0);
        var story = new Story(9, Enumerable.Range(0, 1000).Select(_ => new StoryParagraph(para, [new StoryRun("x", format)])).ToList());
        var box = Shape(1, 202, new AnchorEmu(0, 0, 127000, 127000), new() { [EscherShape.PropFillFlags] = 0x100000, [EscherShape.PropLineFlags] = 0x80000 });
        Assert.Throws<PubFormatException>(() => OnRepeatedPage([box], 1000, new() { [1] = new ShapeInfo(1, 0x01, 9, null, null) }, new() { [9] = story }));
    }

    [Fact]
    public void LongWordArtRepeatedOnManyPages_CountsByItsText()
    {
        // WordArt holding 102,400 characters (100 units each time it is placed) on a page listed 2,500 times.
        var wordArt = Shape(1, 136, new AnchorEmu(0, 0, 127000, 127000)) with
        {
            Complex = new Dictionary<ushort, byte[]> { [EscherShape.PropGtextUnicode] = Encoding.Unicode.GetBytes(new string('W', 102_400)) },
        };
        Assert.Throws<PubFormatException>(() => OnRepeatedPage([wordArt], 2500));
    }

    [Fact]
    public void PagesAlone_CountAgainstTheCap()
    {
        var page = new PageInfo(2, false, null, null, []);
        var contents = new ContentsModel(612 * 12700, 792 * 12700, [page], Enumerable.Repeat(page, PubImporter.MaxElements + 1).ToList(), new Dictionary<uint, ShapeInfo>(), []);
        Assert.Throws<PubFormatException>(() => PubImporter.Compose("t", contents, new ColorResolver([]), [], new Dictionary<uint, Story>(), [], []));
    }

    [Fact]
    public void Backgrounds_CountAgainstTheCap()
    {
        // Page plus background is 2 units: just over half the cap in pages is over the cap.
        var page = new PageInfo(2, false, null, 7, []);
        var contents = new ContentsModel(612 * 12700, 792 * 12700, [page], Enumerable.Repeat(page, PubImporter.MaxElements / 2 + 1).ToList(), new Dictionary<uint, ShapeInfo>(), []);
        Assert.Throws<PubFormatException>(() => PubImporter.Compose("t", contents, new ColorResolver([]), [Shape(7)], new Dictionary<uint, Story>(), [], []));
    }

    // ---------- damage inside groups ----------

    [Fact]
    public void DamagedChildOfAGroup_IsCounted_AndEarlierMembersKept()
    {
        var bad = Sp(3, 0x2);
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(4), 10_000);   // runs past the stream
        var stm = Rec(0xF, 0, 0xF003, [.. Sp(1, 0x1), .. Sp(2, 0x2), .. bad]);

        var top = EscherReader.ReadTopLevel(stm, out var damaged);

        var group = Assert.Single(top);
        Assert.Equal(2u, Assert.Single(group.Children).ShapeId);
        Assert.Equal(1, damaged);
    }

    [Fact]
    public void DamagedGroupLeader_KeepsItsMembers()
    {
        var leader = Sp(1, 0x1);
        BinaryPrimitives.WriteUInt32LittleEndian(leader.AsSpan(8 + 4), 100);   // its FSP record overruns the leader
        var stm = Rec(0xF, 0, 0xF003, [.. leader, .. Sp(2, 0x2)]);

        var top = EscherReader.ReadTopLevel(stm, out var damaged);

        Assert.Equal(2u, Assert.Single(top).ShapeId);
        Assert.Equal(1, damaged);
    }

    // ---------- caches ----------

    [Fact]
    public void ParagraphsAlternatingBetweenTwoHugeRecords_StayFast()
    {
        // 4,000 one-letter paragraphs whose paragraph runs alternate between two 262,148-byte records.
        const int paragraphs = 4000, record = 0x00040004;
        var textOffset = 0x18 + 8 + 4 * 24;
        var table = 8 + paragraphs * 6;
        var fdpp = new byte[table + record + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(fdpp, paragraphs);
        for (var i = 0; i < paragraphs; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(fdpp.AsSpan(8 + i * 4), (uint)(textOffset + (i + 1) * 4));
            BinaryPrimitives.WriteUInt16LittleEndian(fdpp.AsSpan(8 + paragraphs * 4 + i * 2), (ushort)(table + i % 2 * 2));
        }
        for (var at = table; at + 1 < fdpp.Length; at += 2) fdpp[at] = 0x04;
        var text = string.Concat(Enumerable.Repeat("x\r", paragraphs));
        var q = HostileInputTests.Quill([("TEXT", Encoding.Unicode.GetBytes(text)), ("STRS", [.. U32(1), .. U32(4), .. U32((uint)text.Length)]),
            ("SYID", [.. U32(0), .. U32(1), .. U32(7)]), ("FDPP", fdpp)]);
        var sw = Stopwatch.StartNew();
        Assert.Equal(paragraphs, Assert.Single(QuillReader.Read(q, new ColorResolver([0u])).Values).Paragraphs.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"took {sw.Elapsed}");
    }

    [Fact]
    public void ParagraphFacts_KeepTheFirstOfRepeatedBlocks()
    {
        Block Plain(int at, byte id, uint value) => new(at, id, 0x20, at + 2, 4, value);
        var facts = QuillReader.ParaFactsOf([Plain(0, 0x19, 1), Plain(6, 0x19, 0), Plain(12, 0x04, 2), Plain(18, 0x04, 1)]);
        Assert.Equal(1u, facts.StyleIndex);
        Assert.Equal(2u, facts.Val(0x04));
    }

    [Fact]
    public void PictureCache_KeepsTheTwoStreamsApart()
    {
        // One picture embedded in the drawing stream at offset 60, another in the delay stream at offset 60.
        byte[] Png(byte marker) => Rec(0, 0x6E0, 0xF01E, [.. new byte[16], 0xFF, 0x89, 0x50, 0x4E, 0x47, marker]);
        var fbse = new byte[36];
        var delayed = new byte[36];
        BinaryPrimitives.WriteInt32LittleEndian(delayed.AsSpan(20), 1);
        BinaryPrimitives.WriteInt32LittleEndian(delayed.AsSpan(28), 60);
        var stm = Rec(0xF, 0, 0xF000, Rec(0xF, 2, 0xF001, [.. Rec(2, 6, 0xF007, [.. fbse, .. Png(1)]), .. Rec(2, 6, 0xF007, delayed)]));
        byte[] delay = [.. new byte[60], .. Png(2)];

        var blips = EscherReader.ReadBlipStore(stm, delay);

        Assert.Equal(1, blips[0]!.Data[^1]);
        Assert.Equal(2, blips[1]!.Data[^1]);
    }

    // ---------- damage that must not cost the text ----------

    [Fact]
    public void DamagedFontInAStyle_DoesNotCostTextWhoseRunsNameTheirFont()
    {
        // Style 0's font container holds an unknown block type; the only run names its own font, so the style's font
        // is never consulted (as before the facts rewrite) and the text imports.
        var textOffset = 0x18 + 8 + 7 * 24;
        byte[] font = [.. U32(0), .. U32(1), .. new byte[12], .. new byte[4], .. BitConverter.GetBytes((ushort)5), .. Encoding.Unicode.GetBytes("Arial"), .. new byte[4]];
        byte[] damagedStyle = [.. U32(12), 0x24, 0x88, .. U32(6), 0x01, 0xFF];
        byte[] stsh = [.. U32(0), .. U32(2), .. new byte[12], .. U32(6), .. U32(18), .. damagedStyle, .. U32(4)];
        byte[] runRecord = [.. U32(22), 0x24, 0x88, .. U32(16), 0x01, 0x88, .. U32(10), 0x00, 0x20, .. U32(0)];
        byte[] fdpc = [.. BitConverter.GetBytes((ushort)1), .. new byte[6], .. U32((uint)(textOffset + 6)), .. BitConverter.GetBytes((ushort)14), .. runRecord];
        var q = HostileInputTests.Quill([("TEXT", Encoding.Unicode.GetBytes("Hi\r")), ("STRS", [.. U32(1), .. U32(4), .. U32(3)]),
            ("SYID", [.. U32(0), .. U32(1), .. U32(7)]), ("FONT", font), ("STSH", stsh), ("STSH", stsh), ("FDPC", fdpc)]);

        var run = Assert.Single(Assert.Single(Assert.Single(QuillReader.Read(q, new ColorResolver([0u])).Values).Paragraphs).Runs);

        Assert.Equal("Hi", run.Text);
        Assert.Equal("Arial", run.Format.Font);
    }

    [Fact]
    public void GivingUpOnPictures_IsChargedToo()
    {
        // 100 different records, each a 60 MB compressed picture: the first fits the budget, the second uses up the
        // rest while failing, and the other 98 must not each inflate up to the budget again.
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.SmallestSize, leaveOpen: true))   // ~60 KB, so 100 copies keep the budget near 64 MB
        {
            var mb = new byte[1 << 20];
            for (var i = 0; i < 60; i++) z.Write(mb);
        }
        var one = Rec(0, 0x3D4, 0xF01A, [.. new byte[16], .. new byte[34], .. packed.ToArray()]);
        var delay = new byte[one.Length * 100];
        for (var i = 0; i < 100; i++) one.CopyTo(delay, i * one.Length);
        var entries = Enumerable.Range(0, 100).SelectMany(i =>
        {
            var body = new byte[36];
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(20), 1);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(28), i * one.Length);
            return Rec(2, 2, 0xF007, body);
        }).ToArray();
        var stm = Rec(0xF, 0, 0xF000, Rec(0xF, 100, 0xF001, entries));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var blips = EscherReader.ReadBlipStore(stm, delay);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(1, blips.Count(b => b is not null));
        Assert.True(allocated < 400L << 20, $"allocated {allocated >> 20} MB");
    }

    // ---------- fixture hygiene ----------

    [Fact]
    public void Fixtures_CarryANeutralAuthorAndPrinter()
    {
        // Publisher writes the author and the default printer into every file it saves. Fixtures are scrubbed to
        // neutral values; a regenerated fixture that skipped the scrub fails here instead of being published.
        foreach (var file in Directory.GetFiles(Fixtures.Dir, "*.pub"))
        {
            using var root = RootStorage.OpenRead(file);
            var summary = PubPackage.ReadStream(root, "\u0005SummaryInformation", file)!;
            var contents = PubPackage.ReadStream(root, "Contents", file)!;
            var strings = SummaryStrings(summary);
            Assert.True(strings.Count > 0 && strings.All(v => v.Trim() == "Pubsmith"), $"{Path.GetFileName(file)}: summary strings {string.Join(" | ", strings)}");
            Assert.True(contents.AsSpan().IndexOf(Encoding.Unicode.GetBytes("Generic Printer (local)")) >= 0, $"{Path.GetFileName(file)}: printer is not the neutral one");
        }
    }

    /// <summary>Every string property (VT_LPSTR, VT_LPWSTR) in an OLE property-set stream's first section.</summary>
    private static List<string> SummaryStrings(byte[] ps)
    {
        var section = (int)BinaryPrimitives.ReadUInt32LittleEndian(ps.AsSpan(28 + 16));
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(ps.AsSpan(section + 4));
        var strings = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var at = section + (int)BinaryPrimitives.ReadUInt32LittleEndian(ps.AsSpan(section + 8 + i * 8 + 4));
            var type = BinaryPrimitives.ReadUInt32LittleEndian(ps.AsSpan(at));
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(ps.AsSpan(at + 4));
            if (type == 0x1E) strings.Add(Encoding.Latin1.GetString(ps, at + 8, length).TrimEnd('\0'));
            else if (type == 0x1F) strings.Add(Encoding.Unicode.GetString(ps, at + 8, length * 2).TrimEnd('\0'));
        }
        return strings;
    }
}
