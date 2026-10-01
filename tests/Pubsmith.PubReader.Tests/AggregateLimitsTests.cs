using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Pubsmith.Core;
using Pubsmith.PubReader.Contents;
using Pubsmith.PubReader.OfficeArt;
using Pubsmith.PubReader.Quill;
using static Pubsmith.PubReader.Tests.HostileInputTests;

namespace Pubsmith.PubReader.Tests;

// Limits that hold per item must also hold in aggregate: a small file whose entries all point at one big thing
// (one picture, one run table, one formatting record, one group repeated on many pages) must not multiply it.
public class AggregateLimitsTests
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

    private static byte[] Zeros(int megabytes)
    {
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var mb = new byte[1 << 20];
            for (var i = 0; i < megabytes; i++) z.Write(mb);
        }
        return packed.ToArray();
    }

    /// <summary>A compressed metafile picture record (EMF, DEFLATE) holding <paramref name="compressed"/>.</summary>
    private static byte[] Metafile(byte[] compressed) => Rec(0, 0x3D4, 0xF01A, [.. new byte[16], .. new byte[34], .. compressed]);

    /// <summary>A drawing group whose picture store lists one entry per delay-stream offset given.</summary>
    private static byte[] StorePointingAt(params int[] delayOffsets)
    {
        var entries = delayOffsets.SelectMany(o =>
        {
            var body = new byte[36];
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(20), 1);   // size > 0: the picture is in the delay stream
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(28), o);
            return Rec(2, 2, 0xF007, body);
        }).ToArray();
        return Rec(0xF, 0, 0xF000, Rec(0xF, delayOffsets.Length, 0xF001, entries));
    }

    [Fact]
    public void PictureEntriesSharingOneRecord_ShareOnePicture()
    {
        // 16 entries, one 60 MB compressed picture: read once, not 16 times (about 1 GB).
        var delay = Metafile(Zeros(60));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var blips = EscherReader.ReadBlipStore(StorePointingAt(Enumerable.Repeat(0, 16).ToArray()), delay);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(16, blips.Count);
        Assert.All(blips, b => Assert.Same(blips[0], b));
        Assert.True(allocated < 300L << 20, $"allocated {allocated >> 20} MB");
    }

    [Fact]
    public void ManyCompressedPictures_StopAtThePublicationsBudget()
    {
        // Three different 60 MB pictures in a stream of a few hundred KB: the budget (twice the streams plus 64 MB)
        // takes the first; the others are dropped rather than inflated.
        var one = Metafile(Zeros(60));
        byte[] delay = [.. one, .. one, .. one];
        var blips = EscherReader.ReadBlipStore(StorePointingAt(0, one.Length, 2 * one.Length), delay);
        Assert.Equal(1, blips.Count(b => b is not null));
        Assert.NotNull(blips[0]);
    }

    [Fact]
    public void PicturesSharedByShapes_AreWrittenOnce()
    {
        var blip = new Blip("png", [0x89, 0x50, 0x4E, 0x47]);
        EscherShape Pic(uint seq) => new()
        {
            Offset = 0, ShapeId = seq, ShapeType = 75, Flags = 0, Seqnum = seq, Anchor = new AnchorEmu(0, 0, 127000, 127000),
            Props = new Dictionary<ushort, uint> { [EscherShape.PropPib] = seq }, Complex = new Dictionary<ushort, byte[]>(),
        };
        var page = new PageInfo(9, false, null, null, [1, 2]);
        var contents = new ContentsModel(612 * 12700, 792 * 12700, [page], [page], new Dictionary<uint, ShapeInfo>(), []);
        var r = PubImporter.Compose("t", contents, new ColorResolver([]), [Pic(1), Pic(2)], new Dictionary<uint, Story>(), [blip, blip], []);
        var sources = r.Document.Pages[0].Elements.Cast<ImageElement>().Select(i => i.Source).Distinct().ToList();
        Assert.Single(sources);
        Assert.Single(r.Assets);
    }

    [Fact]
    public void RunTablesSharedByDirectoryEntries_AreAFormatError()
    {
        // Two FDPC directory entries pointing at one 1,000-run table: more runs than the stream can hold.
        var table = new byte[8 + 1000 * 6];
        BinaryPrimitives.WriteUInt16LittleEndian(table, 1000);
        var q = HostileInputTests.Quill(("TEXT", Encoding.Unicode.GetBytes("Hi\r")), ("STRS", [.. U32(1), .. U32(4), .. U32(3)]),
            ("SYID", [.. U32(0), .. U32(1), .. U32(7)]), ("FDPC", table), ("FDPC", new byte[8]));
        int Entry(int i) => 0x18 + 8 + i * 24;
        q.AsSpan(Entry(3) + 16, 8).CopyTo(q.AsSpan(Entry(4) + 16));   // entry 4 now aliases entry 3's offset and length
        var ex = Assert.Throws<PubFormatException>(() => QuillReader.Read(q, new ColorResolver([0u])));
        Assert.Contains("overlap", ex.Message);
    }

    [Fact]
    public void RunsAlternatingBetweenTwoHugeRecords_StayFast()
    {
        // 1,200 runs alternating between two 262,148-byte property records (read from a region of "04 00" flag
        // blocks at two even offsets). Each record must be summarised once, not once per run.
        const int runs = 1200, record = 0x00040004;
        var textOffset = 0x18 + 8 + 4 * 24;
        var table = 8 + runs * 6;
        var fdpc = new byte[table + record + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(fdpc, runs);
        for (var i = 0; i < runs; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(fdpc.AsSpan(8 + i * 4), (uint)(textOffset + (i + 1) * 2));
            BinaryPrimitives.WriteUInt16LittleEndian(fdpc.AsSpan(8 + runs * 4 + i * 2), (ushort)(table + i % 2 * 2));
        }
        for (var at = table; at + 1 < fdpc.Length; at += 2) fdpc[at] = 0x04;
        var q = HostileInputTests.Quill([("TEXT", Encoding.Unicode.GetBytes(new string('x', runs - 1) + "\r")),
            ("STRS", [.. U32(1), .. U32(4), .. U32(runs)]), ("SYID", [.. U32(0), .. U32(1), .. U32(7)]), ("FDPC", fdpc)]);
        var sw = Stopwatch.StartNew();
        var story = Assert.Single(QuillReader.Read(q, new ColorResolver([0u])).Values);
        Assert.Equal(runs - 1, story.Paragraphs.Sum(p => p.Runs.Sum(r => r.Text.Length)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"took {sw.Elapsed}");
    }

    [Fact]
    public void AGroupRepeatedOnManyPages_CountsItsMembersAgainstTheCap()
    {
        // One 2,000-member group listed on 150 pages: 150 shapes read, but 300,000 elements placed.
        var members = Enumerable.Range(0, 2000).Select(i => new EscherShape
        {
            Offset = 0, ShapeId = (uint)(10 + i), ShapeType = 1, Flags = 0x2, Props = new Dictionary<ushort, uint>(), Complex = new Dictionary<ushort, byte[]>(),
            Anchor = new AnchorEmu(0, 0, 12700, 12700),
        }).ToList();
        var group = new EscherShape
        {
            Offset = 0, ShapeId = 1, ShapeType = 0, Flags = 0x1, Seqnum = 1, Anchor = new AnchorEmu(0, 0, 127000, 127000),
            Props = new Dictionary<ushort, uint>(), Complex = new Dictionary<ushort, byte[]>(), Children = members,
        };
        var page = new PageInfo(2, false, null, null, [1]);
        var contents = new ContentsModel(612 * 12700, 792 * 12700, [page], Enumerable.Repeat(page, 150).ToList(), new Dictionary<uint, ShapeInfo>(), []);
        var ex = Assert.Throws<PubFormatException>(() => PubImporter.Compose("t", contents, new ColorResolver([]), [group], new Dictionary<uint, Story>(), [], []));
        Assert.Contains("elements", ex.Message);
    }

    [Fact]
    public void DeletedShapeListedOnAPage_IsNotDrawnOrCounted()
    {
        var shape = new EscherShape
        {
            Offset = 0, ShapeId = 1, ShapeType = 1, Flags = 0x8, Seqnum = 1, Anchor = new AnchorEmu(0, 0, 12700, 12700),
            Props = new Dictionary<ushort, uint>(), Complex = new Dictionary<ushort, byte[]>(),
        };
        var page = new PageInfo(2, false, null, null, [1]);
        var contents = new ContentsModel(612 * 12700, 792 * 12700, [page], [page], new Dictionary<uint, ShapeInfo>(), []);
        var r = PubImporter.Compose("t", contents, new ColorResolver([]), [shape], new Dictionary<uint, Story>(), [], []);
        Assert.Empty(r.Document.Pages[0].Elements);
        Assert.Equal(0, r.ShapesRead);
        Assert.Empty(r.Issues);
    }

    /// <summary>Nested group containers, each led by a group shape (FSP flags <paramref name="leaderFlags"/>).</summary>
    private static byte[] NestedLedGroups(uint leaderFlags, int levels)
    {
        // Level i: SpgrContainer [ SpContainer [ FSP ] , level i+1 ]; innermost: an empty SpContainer.
        var size = 32 * levels + 8;
        var stm = new byte[size];
        var at = 0;
        for (var i = 0; i < levels; i++)
        {
            void Header(int ver, int inst, ushort type, int length)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(stm.AsSpan(at), (ushort)(ver | (inst << 4)));
                BinaryPrimitives.WriteUInt16LittleEndian(stm.AsSpan(at + 2), type);
                BinaryPrimitives.WriteUInt32LittleEndian(stm.AsSpan(at + 4), (uint)length);
                at += 8;
            }
            Header(0xF, 0, 0xF003, size - at - 8);
            Header(0xF, 0, 0xF004, 16);
            Header(2, 0, 0xF00A, 8);
            BinaryPrimitives.WriteUInt32LittleEndian(stm.AsSpan(at), (uint)(i + 1));
            BinaryPrimitives.WriteUInt32LittleEndian(stm.AsSpan(at + 4), leaderFlags);
            at += 8;
        }
        BinaryPrimitives.WriteUInt16LittleEndian(stm.AsSpan(at), 0x000F);
        BinaryPrimitives.WriteUInt16LittleEndian(stm.AsSpan(at + 2), 0xF004);
        return stm;
    }

    [Theory]
    [InlineData(0x1u)]   // ordinary groups: each level's members are the next group
    [InlineData(0x5u)]   // patriarch groups: members stay at the top level, but the containers still nest
    public void DeeplyNestedLedGroups_AreAFormatError_NotAStackOverflow(uint leaderFlags) =>
        Assert.Throws<PubFormatException>(() => EscherReader.ReadTopLevel(NestedLedGroups(leaderFlags, 20_000)));

    [Fact]
    public void OneDamagedShape_LosesOnlyThatShape()
    {
        // In a two-rectangle file, the first shape's first record is made to overrun its container.
        var pkg = PubPackage.Open(Fixtures.Pub("geometry", "two-rects"));
        var esc = (byte[])pkg.EscherStm.Clone();
        var first = EscherReader.ReadTopLevel(esc).First(s => s.ShapeType == 1 && s.Seqnum is not null);   // the first rectangle on the page
        var container = RecordReader.Read(esc, first.Offset);
        BinaryPrimitives.WriteUInt32LittleEndian(esc.AsSpan(container.Body + 4), (uint)container.Length);

        var r = PubImporter.Import(PubPackage.FromStreams("one-bad-shape.pub", pkg.Contents, esc, pkg.EscherDelayStm, pkg.Quill));

        Assert.Single(r.Document.Pages[0].Elements.OfType<ShapeElement>());
        Assert.Contains(r.Issues, i => i.Kind == ImportIssueKind.Dropped && i.Detail.Contains("damaged drawing record"));
    }

    [Fact]
    public void StreamDeclaringMoreThanTheFileHolds_IsRefusedWithoutAllocatingIt()
    {
        // The Contents directory entry (UTF-16 name, stream size at +120) claims 1 GB in a ~100 KB file.
        var path = Path.Combine(Path.GetTempPath(), $"pubsmith-claim-{Guid.NewGuid():N}.pub");
        var bytes = File.ReadAllBytes(Fixtures.Pub("geometry", "base"));
        var name = Encoding.Unicode.GetBytes("Contents");
        var entry = bytes.AsSpan().IndexOf(name);
        Assert.True(entry > 0);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(entry + 120), 0x3FFF0000);
        File.WriteAllBytes(path, bytes);
        try
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            Assert.Throws<PubFormatException>(() => PubPackage.Open(path));
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64L << 20);
        }
        finally { File.Delete(path); }
    }
}
