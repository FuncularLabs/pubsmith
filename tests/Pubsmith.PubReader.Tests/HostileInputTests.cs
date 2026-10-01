using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using OpenMcdf;
using Pubsmith.Core;
using Pubsmith.PubReader.Contents;
using Pubsmith.PubReader.OfficeArt;
using Pubsmith.PubReader.Quill;
using Xunit.Abstractions;

namespace Pubsmith.PubReader.Tests;

// Pubsmith opens files from strangers. Every malformed input must end in PubFormatException (or a reported
// issue), never another exception type, a crash, unbounded memory or unbounded time.
public class HostileInputTests(ITestOutputHelper output)
{
    // ---------- byte builders ----------

    private static byte[] Rec(int ver, int inst, ushort type, byte[] body)
    {
        var b = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)(ver | (inst << 4)));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), type);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)body.Length);
        body.CopyTo(b, 8);
        return b;
    }

    internal static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }

    /// <summary>A Quill stream: directory at 0x18 listing the given chunks, chunk bodies after it.</summary>
    internal static byte[] Quill(params (string Name, byte[] Body)[] chunks)
    {
        var dirStart = 0x18;
        var bodyStart = dirStart + 8 + chunks.Length * 24;
        var total = bodyStart + chunks.Sum(c => c.Body.Length);
        var q = new byte[total];
        BinaryPrimitives.WriteUInt16LittleEndian(q.AsSpan(dirStart + 2), (ushort)chunks.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(q.AsSpan(dirStart + 4), 0xFFFFFFFF);
        var at = bodyStart;
        for (var i = 0; i < chunks.Length; i++)
        {
            var e = dirStart + 8 + i * 24;
            Encoding.ASCII.GetBytes(chunks[i].Name).CopyTo(q, e + 2);
            BinaryPrimitives.WriteUInt32LittleEndian(q.AsSpan(e + 16), (uint)at);
            BinaryPrimitives.WriteUInt32LittleEndian(q.AsSpan(e + 20), (uint)chunks[i].Body.Length);
            chunks[i].Body.CopyTo(q, at);
            at += chunks[i].Body.Length;
        }
        return q;
    }

    private static readonly ColorResolver Colors = new([0u]);

    // STRS: count, skip, lengths. Skip 4 = the lengths follow the skip field (Publisher writes 8, with one spare u32).
    private static (string, byte[])[] MinimalStory(string text) =>
    [
        ("TEXT", Encoding.Unicode.GetBytes(text)),
        ("STRS", [.. U32(1), .. U32(4), .. U32((uint)text.Length)]),
        ("SYID", [.. U32(0), .. U32(1), .. U32(7)]),
    ];

    /// <summary>A chain of <paramref name="levels"/> containers of <paramref name="type"/>, each holding only the next.</summary>
    private static byte[] Nested(ushort type, int levels)
    {
        var stm = new byte[8 * (levels + 1)];
        for (var i = 0; i <= levels; i++)
        {
            var at = 8 * i;
            BinaryPrimitives.WriteUInt16LittleEndian(stm.AsSpan(at), 0x000F);
            BinaryPrimitives.WriteUInt16LittleEndian(stm.AsSpan(at + 2), i < levels ? type : (ushort)0xF004);
            BinaryPrimitives.WriteUInt32LittleEndian(stm.AsSpan(at + 4), (uint)(stm.Length - at - 8));
        }
        return stm;
    }

    // ---------- OfficeArt ----------

    [Fact]
    public void DeeplyNestedGroups_AreAFormatError_NotAStackOverflow() =>
        Assert.Throws<PubFormatException>(() => EscherReader.ReadTopLevel(Nested(0xF003, 20_000)));

    [Fact]
    public void NestedDrawingContainers_AreAlsoCapped() =>
        // Drawing containers (0xF002) inside drawing containers add no group depth, but do recurse.
        Assert.Throws<PubFormatException>(() => EscherReader.ReadTopLevel(Nested(0xF002, 20_000)));

    [Fact]
    public void ModestNesting_IsRead() => Assert.Single(EscherReader.ReadTopLevel(Nested(0xF002, 10)));

    [Fact]
    public void RecordHeader_NearIntMax_IsNotRead()
    {
        Assert.False(RecordReader.TryRead(new byte[64], int.MaxValue - 4, out _));
        Assert.False(RecordReader.TryRead(new byte[64], -1, out _));
    }

    [Fact]
    public void PropertyCount_BeyondTheRecord_IsReportedAsTruncated()
    {
        // 4095 entries declared, one present, marked complex with a huge length.
        var body = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(body, 0x8000 | 0x00C0);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(2), 0x7FFFFFFF);
        var props = new Dictionary<ushort, uint>(); var complex = new Dictionary<ushort, byte[]>();
        Assert.True(EscherReader.ReadProperties(body, 4095, props, complex));
        Assert.True(props.ContainsKey(0x00C0));
        Assert.Empty(complex[0x00C0]);
    }

    [Fact]
    public void ComplexValue_LongerThanTheRecord_IsTruncated()
    {
        // One entry (fits), whose complex value claims 100 bytes where 4 remain.
        var body = new byte[6 + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(body, 0x8000 | 0x00C0);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(2), 100);
        var complex = new Dictionary<ushort, byte[]>();
        Assert.True(EscherReader.ReadProperties(body, 1, new Dictionary<ushort, uint>(), complex));
        Assert.Equal(4, complex[0x00C0].Length);
    }

    [Fact]
    public void Properties_ThatFit_AreNotTruncated()
    {
        var body = new byte[6 + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(body, 0x8000 | 0x00C0);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(2), 4);
        var complex = new Dictionary<ushort, byte[]>();
        Assert.False(EscherReader.ReadProperties(body, 1, new Dictionary<ushort, uint>(), complex));
        Assert.Equal(4, complex[0x00C0].Length);
    }

    [Theory]
    [InlineData(0x80000001u)]
    [InlineData(0xFFFFFFFFu)]
    public void PictureIndex_OutOfIntRange_IsNull(uint pib)
    {
        var s = new EscherShape { Offset = 0, ShapeId = 1, ShapeType = 75, Flags = 0, Props = new Dictionary<ushort, uint> { [EscherShape.PropPib] = pib }, Complex = new Dictionary<ushort, byte[]>() };
        Assert.Null(s.PictureIndex);
    }

    private static byte[] MetafileStore(byte[] compressed)
    {
        var header = new byte[34]; header[32] = 0;   // compression = DEFLATE
        var blip = Rec(0, 0x3D4, 0xF01A, [.. new byte[16], .. header, .. compressed]);
        var fbse = Rec(2, 2, 0xF007, [.. new byte[36], .. blip]);
        return Rec(0xF, 0, 0xF000, Rec(0xF, 1, 0xF001, fbse));
    }

    [Fact]
    public void CompressedPicture_Bomb_IsDropped_Quickly()
    {
        // 96 MB of zeros deflates to ~100 KB; inflation must stop at the cap, not allocate it all.
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var zeros = new byte[1 << 20];
            for (var i = 0; i < 96; i++) z.Write(zeros);
        }
        var sw = Stopwatch.StartNew();
        var blips = EscherReader.ReadBlipStore(MetafileStore(packed.ToArray()), []);
        Assert.Null(Assert.Single(blips));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }

    [Fact]
    public void CompressedPicture_UnderTheCap_Inflates()
    {
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true)) z.Write(new byte[1000]);
        var blip = Assert.Single(EscherReader.ReadBlipStore(MetafileStore(packed.ToArray()), []));
        Assert.Equal(1000, blip!.Data.Length);
    }

    [Fact]
    public void CorruptCompressedPicture_IsNull_NotInvalidData() =>
        Assert.Null(Assert.Single(EscherReader.ReadBlipStore(MetafileStore([0x78, 0x9C, 0xFF, 0xFF, 0xFF, 0x00]), [])));

    [Fact]
    public void DamagedPictureStore_LosesThePictures_NotTheFile()
    {
        // A drawing-group container whose picture store child overruns it, ahead of a real drawing stream.
        var pkg = PubPackage.Open(Fixtures.Pub("picture", "base"));
        var store = Rec(0xF, 1, 0xF001, new byte[8]);
        BinaryPrimitives.WriteUInt32LittleEndian(store.AsSpan(4), 100);
        byte[] escher = [.. Rec(0xF, 0, 0xF000, store), .. pkg.EscherStm];

        var r = PubImporter.Import(PubPackage.FromStreams("store.pub", pkg.Contents, escher, pkg.EscherDelayStm, pkg.Quill));

        Assert.Contains(r.Issues, i => i.Kind == ImportIssueKind.Dropped && i.Detail.StartsWith("the picture store could not be read"));
        Assert.Contains(r.Issues, i => i.Kind == ImportIssueKind.Placeholder && i.Shape == "picture");
    }

    [Fact]
    public void DamagedContainer_IsCounted()
    {
        // A valid shape, then a shape container header whose length overruns the stream.
        var good = Rec(0xF, 0, 0xF004, []);
        var bad = Rec(0xF, 0, 0xF004, new byte[8]);
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(4), 0x00FFFFFF);
        var shapes = EscherReader.ReadTopLevel([.. good, .. bad], out var damaged);
        Assert.Single(shapes);
        Assert.Equal(1, damaged);
    }

    // ---------- Contents blocks ----------

    [Fact]
    public void VariableBlock_WithLengthThatWrapsInt_IsAFormatError()
    {
        var b = new byte[16];
        b[0] = 0x01; b[1] = 0x88;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(2), 0x7FFFFFFF);   // dataOffset + len wraps negative
        Assert.Throws<PubFormatException>(() => Blocks.ReadAt(b, 0));
    }

    [Fact]
    public void Block_AtANegativeOffset_IsAFormatError() => Assert.Throws<PubFormatException>(() => Blocks.ReadAt(new byte[16], -4));

    /// <summary>
    /// A Contents stream: a 0x20-byte header, the given chunk bodies, then a trailer whose chunk directory lists
    /// each (type, index into <paramref name="chunks"/>) entry.
    /// </summary>
    private static byte[] ContentsWith(byte[][] chunks, params (byte Type, int Chunk)[] entries)
    {
        var body = new List<byte>(new byte[0x20]);
        var offsets = new List<int>();
        foreach (var c in chunks) { offsets.Add(body.Count); body.AddRange(U32((uint)(4 + c.Length))); body.AddRange(c); }
        var trailer = body.Count;
        var dir = new List<byte>();
        foreach (var (type, chunk) in entries)
            dir.AddRange([0x05, 0x88, .. U32(4 + 12), 0x02, 0x20, .. U32(type), 0x04, 0x20, .. U32((uint)offsets[chunk])]);
        body.AddRange(U32((uint)(4 + 6 + dir.Count)));
        body.AddRange([0x01, 0x90, .. U32((uint)(4 + dir.Count)), .. dir]);
        var c0 = body.ToArray();
        c0[0] = 0xE8; c0[1] = 0xAC; c0[2] = 0x2C;
        BinaryPrimitives.WriteUInt32LittleEndian(c0.AsSpan(0x1A), (uint)trailer);
        return c0;
    }

    [Fact]
    public void DirectoryPointingManyEntriesAtOneChunk_IsAFormatError()
    {
        var big = Enumerable.Repeat(new byte[] { 0x01, 0x00 }, 40_000).SelectMany(b => b).ToArray();   // 40,000 flag blocks
        var ex = Assert.Throws<PubFormatException>(() => ContentsReader.Read(ContentsWith([big], [.. Enumerable.Repeat(((byte)0x01, 0), 10)])));
        Assert.Contains("overlapping", ex.Message);
    }

    [Fact]
    public void ImpossiblePageSize_IsAFormatError()
    {
        // Document chunk (0x44): block 0x12 holding width 0x01 and height 0x02, in EMU.
        byte[] doc = [0x12, 0x88, .. U32(4 + 12), 0x01, 0x20, .. U32(2_000_000_000), 0x02, 0x20, .. U32(10_058_400)];
        var ex = Assert.Throws<PubFormatException>(() => ContentsReader.Read(ContentsWith([doc], (0x44, 0))));
        Assert.Contains("impossible page size", ex.Message);
    }

    [Fact]
    public void TrailerOffset_NearIntMax_IsAFormatError()
    {
        var c = new byte[64];
        c[0] = 0xE8; c[1] = 0xAC; c[2] = 0x2C;
        BinaryPrimitives.WriteUInt32LittleEndian(c.AsSpan(0x1A), 0x7FFFFFFE);
        Assert.Throws<PubFormatException>(() => ContentsReader.Read(c));
    }

    // ---------- Quill ----------

    [Fact]
    public void ColourList_ZeroLengthEntries_IsAFormatError_Quickly()
    {
        var pl = new byte[12 + 8];
        BinaryPrimitives.WriteUInt32LittleEndian(pl, 0x7FFFFFFF);   // claimed entries, each "0 bytes long"
        var q = Quill([.. MinimalStory("Hi\r"), ("PL  ", pl)]);
        var sw = Stopwatch.StartNew();
        Assert.Throws<PubFormatException>(() => QuillReader.Read(q, Colors));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ColourEntry_OfZeroLength_IsAFormatError()
    {
        var pl = new byte[12 + 8];
        BinaryPrimitives.WriteUInt32LittleEndian(pl, 1);   // one entry, and it says it is 0 bytes long
        Assert.Throws<PubFormatException>(() => QuillReader.Read(Quill([.. MinimalStory("Hi\r"), ("PL  ", pl)]), Colors));
    }

    [Fact]
    public void FontName_PastItsChunk_IsAFormatError()
    {
        // One font whose name claims 1,000 characters in a 32-byte chunk (the stream carries on after it).
        var font = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(font.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(font.AsSpan(8 + 12 + 4), 1000);
        var q = Quill([("FONT", font), .. MinimalStory("Hi\r"), ("PAD ", new byte[4096])]);
        Assert.Throws<PubFormatException>(() => QuillReader.Read(q, Colors));
    }

    [Fact]
    public void DirectoryEntry_OutsideTheStream_IsAFormatError()
    {
        var q = Quill(MinimalStory("Hi\r"));
        BinaryPrimitives.WriteUInt32LittleEndian(q.AsSpan(0x18 + 8 + 16), (uint)q.Length);   // TEXT now starts at the end, 6 bytes long
        Assert.Throws<PubFormatException>(() => QuillReader.ReadDirectory(q));
    }

    [Fact]
    public void StoryLengthTable_ShorterThanItsHeader_IsAFormatError()
    {
        // STRS of 4 bytes (no stories): its skip field would be read from the next chunk.
        var q = Quill(("TEXT", Encoding.Unicode.GetBytes("Hi\r")), ("STRS", U32(0)), ("SYID", [.. U32(0), .. U32(0)]), ("PAD ", new byte[64]));
        Assert.Throws<PubFormatException>(() => QuillReader.Read(q, Colors));
    }

    [Fact]
    public void IdTable_ShorterThanItsHeader_AtTheEndOfTheStream_IsAFormatError()
    {
        // SYID of 4 bytes, last in the stream: its count would be read past the end.
        var q = Quill(("TEXT", Encoding.Unicode.GetBytes("Hi\r")), ("STRS", [.. U32(0), .. U32(4)]), ("SYID", U32(0)));
        Assert.Throws<PubFormatException>(() => QuillReader.Read(q, Colors));
    }

    [Fact]
    public void IdTable_LongerThanItsChunk_IsAFormatError()
    {
        // SYID claims 2 ids but holds 1; the bytes after it are another chunk.
        var q = Quill(("TEXT", Encoding.Unicode.GetBytes("A\rB\r")), ("STRS", [.. U32(2), .. U32(4), .. U32(2), .. U32(2)]),
            ("SYID", [.. U32(0), .. U32(2), .. U32(7)]), ("PAD ", new byte[64]));
        Assert.Throws<PubFormatException>(() => QuillReader.Read(q, Colors));
    }

    [Fact]
    public void PropertyRecords_AddingUpPastTheBudget_AreAFormatError()
    {
        // 20 runs, each pointing 2 bytes further into one long region of flag blocks ("04 00"): read from any even
        // offset, the region is a valid 262,148-byte property record. Parsed once per offset: over the budget.
        const int runs = 20, record = 0x00040004;
        var textOffset = 0x18 + 8 + 4 * 24;
        var table = 8 + runs * 6;
        var fdpc = new byte[table + record + runs * 2];
        BinaryPrimitives.WriteUInt16LittleEndian(fdpc, runs);
        for (var i = 0; i < runs; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(fdpc.AsSpan(8 + i * 4), (uint)(textOffset + (i + 1) * 2));
            BinaryPrimitives.WriteUInt16LittleEndian(fdpc.AsSpan(8 + runs * 4 + i * 2), (ushort)(table + i * 2));
        }
        for (var at = table; at + 1 < fdpc.Length; at += 2) fdpc[at] = 0x04;
        var q = Quill([.. MinimalStory(new string('x', runs - 1) + "\r"), ("FDPC", fdpc)]);
        var ex = Assert.Throws<PubFormatException>(() => QuillReader.Read(q, Colors));
        Assert.Contains("formatting", ex.Message);
    }

    [Fact]
    public void Directory_WithMoreChunksThanAnyFile_IsAFormatError()
    {
        // Two directory pages of 65,535 (empty but valid) entries each.
        const int per = 0xFFFF;
        var second = 0x18 + 8 + per * 24;
        var q = new byte[second + 8 + per * 24];
        BinaryPrimitives.WriteUInt16LittleEndian(q.AsSpan(0x18 + 2), per);
        BinaryPrimitives.WriteUInt32LittleEndian(q.AsSpan(0x18 + 4), (uint)second);
        BinaryPrimitives.WriteUInt16LittleEndian(q.AsSpan(second + 2), per);
        BinaryPrimitives.WriteUInt32LittleEndian(q.AsSpan(second + 4), 0xFFFFFFFF);
        Assert.Throws<PubFormatException>(() => QuillReader.ReadDirectory(q));
    }

    [Fact]
    public void StyleSheet_HugeCount_IsAFormatError()
    {
        var stsh = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(stsh.AsSpan(4), 0x7FFFFFFF);
        var q = Quill([.. MinimalStory("Hi\r"), ("STSH", stsh), ("STSH", stsh)]);
        Assert.Throws<PubFormatException>(() => QuillReader.Read(q, Colors));
    }

    [Fact]
    public void FontTable_HugeCount_IsAFormatError()
    {
        var font = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(font.AsSpan(4), 0x40000000);   // 4n wraps int
        var q = Quill([.. MinimalStory("Hi\r"), ("FONT", font)]);
        Assert.Throws<PubFormatException>(() => QuillReader.Read(q, Colors));
    }

    [Theory]
    [InlineData(0xFFFFFFF0u, 1u)]   // negative skip
    [InlineData(12u, 1u)]           // skip past the STRS chunk (into SYID)
    [InlineData(4u, 0xFFFFFFF0u)]   // negative story length
    [InlineData(4u, 0x7FFFFFFFu)]   // story longer than the stream
    [InlineData(4u, 5u)]            // story longer than the TEXT chunk (3 characters), still inside the stream
    public void StoryTable_Hostile_IsAFormatError(uint skip, uint len)
    {
        var q = Quill(("TEXT", Encoding.Unicode.GetBytes("Hi\r")), ("STRS", [.. U32(1), .. U32(skip), .. U32(len)]), ("SYID", [.. U32(0), .. U32(1), .. U32(7)]));
        Assert.Throws<PubFormatException>(() => QuillReader.Read(q, Colors));
    }

    [Fact]
    public void RunTable_CountBeyondTheChunk_IsAFormatError()
    {
        var fdpc = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(fdpc, 0xFFFF);
        var q = Quill([.. MinimalStory("Hi\r"), ("FDPC", fdpc)]);
        Assert.Throws<PubFormatException>(() => QuillReader.Read(q, Colors));
    }

    [Fact]
    public void RunPropertyOffset_PastTheStream_IsAFormatError()
    {
        // One run whose property record offset (u16, relative to the chunk) points past the end of the stream.
        var fdpc = new byte[8 + 4 + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(fdpc, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(fdpc.AsSpan(8), 0x7FFFFFFF);
        BinaryPrimitives.WriteUInt16LittleEndian(fdpc.AsSpan(12), 0xFFF0);
        var q = Quill([.. MinimalStory("Hi\r"), ("FDPC", fdpc)]);
        Assert.Throws<PubFormatException>(() => QuillReader.Read(q, Colors));
    }

    [Fact]
    public void ExtraStories_AreReported_NotDropped()
    {
        var q = Quill(("TEXT", Encoding.Unicode.GetBytes("A\rB\r")), ("STRS", [.. U32(2), .. U32(4), .. U32(2), .. U32(2)]), ("SYID", [.. U32(0), .. U32(1), .. U32(7)]));
        var problems = new List<ImportIssue>();
        var stories = QuillReader.Read(q, Colors, problems);
        Assert.Single(stories);
        Assert.Contains(problems, p => p.Detail.Contains("1 text story has no id"));
    }

    [Fact]
    public void DuplicateTextIds_AreReported()
    {
        var q = Quill(("TEXT", Encoding.Unicode.GetBytes("A\rB\r")), ("STRS", [.. U32(2), .. U32(4), .. U32(2), .. U32(2)]), ("SYID", [.. U32(0), .. U32(2), .. U32(7), .. U32(7)]));
        var problems = new List<ImportIssue>();
        QuillReader.Read(q, Colors, problems);
        Assert.Contains(problems, p => p.Detail.Contains("text id 7"));
    }

    [Fact]
    public void WellFormedStories_ReportNothing()
    {
        var problems = new List<ImportIssue>();
        Assert.Single(QuillReader.Read(Quill(MinimalStory("Hi\r")), Colors, problems));
        Assert.Empty(problems);
    }

    [Fact]
    public void ManyRuns_OverLongText_StaysLinear()
    {
        // 200,000 characters, one run per 10: a per-character linear run search is ~2 x 10^9 steps.
        const int chars = 200_000, per = 10, n = chars / per;
        var text = new string('x', chars - 1) + "\r";
        var fdpc = new byte[8 + n * 4 + n * 2];
        BinaryPrimitives.WriteUInt16LittleEndian(fdpc, n);
        var textOffset = 0x18 + 8 + 4 * 24;   // TEXT is the first chunk body (4 chunks in the directory)
        for (var i = 0; i < n; i++) BinaryPrimitives.WriteUInt32LittleEndian(fdpc.AsSpan(8 + i * 4), (uint)(textOffset + (i + 1) * per * 2));
        var q = Quill([.. MinimalStory(text), ("FDPC", fdpc)]);
        var sw = Stopwatch.StartNew();
        var story = Assert.Single(QuillReader.Read(q, Colors).Values);
        Assert.Equal(chars - 1, story.Paragraphs.Sum(p => p.Runs.Sum(r => r.Text.Length)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"took {sw.Elapsed}");
    }

    [Fact]
    public void RunLookup_KeepsFirstCoveringRunSemantics()
    {
        // Runs out of order: ends 40, 20, 60. The first run in list order whose end is past the position wins.
        var runs = new QuillReader.RunIndex([(40, 1), (20, 2), (60, 3)]);
        Assert.Equal(1, runs.Find(10));
        Assert.Equal(1, runs.Find(30));
        Assert.Equal(3, runs.Find(45));
        Assert.Equal(-1, runs.Find(60));
    }

    // ---------- package ----------

    [Fact]
    public void OversizedStream_IsAFormatError()
    {
        using var root = RootStorage.CreateInMemory();
        using (var s = root.CreateStream("Contents")) s.Write(new byte[100]);
        Assert.Throws<PubFormatException>(() => PubPackage.ReadStream(root, "Contents", "x.pub", maxBytes: 50));
        Assert.Equal(100, PubPackage.ReadStream(root, "Contents", "x.pub", maxBytes: 100)!.Length);
    }

    // ---------- importer ----------

    [Fact]
    public void AnchorExtremes_DoNotOverflow()
    {
        var wide = PubImporter.BoxFromAnchor(new AnchorEmu(int.MinValue, int.MinValue, int.MaxValue, int.MaxValue), 612, 792);
        Assert.Equal(((double)int.MaxValue - int.MinValue) / 12700, wide.Width);
        var flipped = PubImporter.BoxFromAnchor(new AnchorEmu(0, 0, int.MinValue, int.MinValue), 612, 792);   // Math.Abs(int.MinValue) throws
        Assert.Equal(-(double)int.MinValue / 12700, flipped.Width);
    }

    [Fact]
    public void GroupCoordinates_AtIntExtremes_MapOntoTheGroupFrame()
    {
        var cs = new AnchorEmu(int.MinValue, int.MinValue, int.MaxValue, int.MaxValue);
        var box = PubImporter.ChildBox(cs, cs, new Box(10, 20, 100, 50));
        Assert.Equal(10, box.X, 6); Assert.Equal(20, box.Y, 6); Assert.Equal(100, box.Width, 6); Assert.Equal(50, box.Height, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(39)]
    public void TruncatedDib_IsNull(int length) => Assert.Null(PubImporter.DibToBmp(new byte[length]));

    [Fact]
    public void DibWithAnImpossiblePalette_IsNull()
    {
        var dib = new byte[60];
        BinaryPrimitives.WriteInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 8);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(32), 1_000_000);   // colours used
        Assert.Null(PubImporter.DibToBmp(dib));
    }

    /// <summary>Rewrites the seqnum (ClientData 0xF011, tag 0x6801) of every shape in the drawing stream.</summary>
    private static byte[] BreakSeqnums(byte[] escher)
    {
        var esc = (byte[])escher.Clone();
        for (var i = 0; i + 8 <= esc.Length; i++)
        {
            if (esc[i + 2] != 0x11 || esc[i + 3] != 0xF0 || !RecordReader.TryRead(esc, i, out var r)) continue;
            for (var p = r.Body + 4; p + 6 <= r.End; p += 6)
                if (BinaryPrimitives.ReadUInt16LittleEndian(esc.AsSpan(p)) == 0x6801) BinaryPrimitives.WriteUInt32LittleEndian(esc.AsSpan(p + 2), 0xDEAD);
        }
        return esc;
    }

    [Fact]
    public void ShapeListedOnPage_WithoutDrawing_IsReported()
    {
        var pkg = PubPackage.Open(Fixtures.Pub("geometry", "base"));
        var r = PubImporter.Import(PubPackage.FromStreams("broken.pub", pkg.Contents, BreakSeqnums(pkg.EscherStm), pkg.EscherDelayStm, pkg.Quill));
        var issue = Assert.Single(r.Issues, i => i.Kind == ImportIssueKind.Dropped);
        Assert.Contains("has no drawing", issue.Detail);
        Assert.Equal(1, issue.Page);
        Assert.Equal(r.ShapesRead, r.ShapesConverted + r.ShapesPlaceheld);
    }

    [Fact]
    public void UnreadableText_KeepsTheShapes_AndSaysSo()
    {
        var pkg = PubPackage.Open(Fixtures.Pub("text", "frame-fill-line"));
        var quill = (byte[])pkg.Quill.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(quill.AsSpan(0x18 + 2), 0xFFFF);   // directory claims 65,535 chunks
        var r = PubImporter.Import(PubPackage.FromStreams("notext.pub", pkg.Contents, pkg.EscherStm, pkg.EscherDelayStm, quill));
        Assert.Contains(r.Issues, i => i.Kind == ImportIssueKind.Dropped && i.Detail.StartsWith("text could not be read"));
        Assert.Contains(r.Document.Pages[0].Elements, e => e is ShapeElement);
    }

    [Fact]
    public void UnexpectedFailure_IsWrappedAsAFormatErrorNamingTheFile()
    {
        // No Contents at all is not something the reader checks for; the safety net must still turn it into a
        // format error (with the cause attached), not let it escape.
        var ex = Assert.Throws<PubFormatException>(() => PubImporter.Import(PubPackage.FromStreams("odd.pub", null!, [], [], [])));
        Assert.IsType<NullReferenceException>(ex.InnerException);
        Assert.Contains("'odd.pub'", ex.Message);
        Assert.Contains("bug", ex.Message);
    }

    [Fact]
    public void ReaderFormatErrors_NameTheFile()
    {
        var pkg = PubPackage.Open(Fixtures.Pub("text", "base"));
        var ex = Assert.Throws<PubFormatException>(() => PubImporter.Import(PubPackage.FromStreams("named.pub", new byte[8], pkg.EscherStm, [], pkg.Quill)));
        Assert.StartsWith("'named.pub' is damaged:", ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void ShapeCap_StopsAPageListRepeatedWithoutEnd()
    {
        var page = new PageInfo(2, false, null, null, [1]);
        var pages = Enumerable.Repeat(page, PubImporter.MaxElements + 1).ToList();
        var contents = new ContentsModel(612 * 12700, 792 * 12700, [page], pages, new Dictionary<uint, ShapeInfo>(), []);
        var shape = new EscherShape { Offset = 0, ShapeId = 1, ShapeType = 1, Flags = 0, Props = new Dictionary<ushort, uint>(), Complex = new Dictionary<ushort, byte[]>(), Seqnum = 1, Anchor = new AnchorEmu(0, 0, 12700, 12700) };
        Assert.Throws<PubFormatException>(() => PubImporter.Compose("t", contents, new ColorResolver([]), [shape], new Dictionary<uint, Story>(), [], []));
    }

    [Fact]
    public void RealFixtures_ReportNoDamage()
    {
        // The damage reports must not fire on well-formed Publisher output (false alarms would teach users to ignore them).
        foreach (var file in Directory.GetFiles(Fixtures.Dir, "*.pub"))
        {
            var r = PubImporter.Import(file);
            Assert.DoesNotContain(r.Issues, i => i.Kind == ImportIssueKind.Dropped);
            Assert.DoesNotContain(r.Issues, i => i.Detail.Contains("truncated"));
        }
    }

    /// <summary>
    /// Property test: corrupted variants of real Publisher-built files. The only acceptable outcomes are a document
    /// or a PubFormatException raised on purpose (no inner exception: the importer's safety net did not have to
    /// catch anything), each within a time bound. Fixed seeds, so a failure names a reproducible case.
    /// </summary>
    [Theory]
    [InlineData("text", "base", 1)]
    [InlineData("text", "run-red-word", 2)]
    [InlineData("picture", "two", 3)]
    [InlineData("pages", "master-rect", 4)]
    [InlineData("group", "two", 5)]
    [InlineData("wordart", "arch", 6)]
    [InlineData("table", "2x2", 7)]
    public void CorruptedFiles_OnlyEverFailAsFormatErrors(string group, string variant, int seed)
    {
        var pkg = PubPackage.Open(Fixtures.Pub(group, variant));
        var rng = new Random(seed);
        int ok = 0, rejected = 0;
        for (var iter = 0; iter < 1000; iter++)
        {
            byte[][] s = [(byte[])pkg.Contents.Clone(), (byte[])pkg.EscherStm.Clone(), (byte[])pkg.Quill.Clone(), (byte[])pkg.EscherDelayStm.Clone()];
            var which = iter % 4;
            if (s[which].Length < 16) which = 1;
            var t = s[which];
            var mode = rng.Next(4);
            switch (mode)
            {
                case 0: for (var k = rng.Next(1, 8); k > 0; k--) t[rng.Next(t.Length)] = (byte)rng.Next(256); break;
                case 1: BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(rng.Next(t.Length - 4)), (uint)rng.NextInt64(uint.MaxValue)); break;
                case 2: BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(rng.Next(t.Length - 4)), rng.Next(4) switch { 0 => 0x7FFFFFFF, 1 => 0xFFFFFFFF, 2 => 0x80000000, _ => 0 }); break;
                default: s[which] = t[..rng.Next(t.Length)]; break;
            }
            var sw = Stopwatch.StartNew();
            try { PubImporter.Import(PubPackage.FromStreams("fuzz.pub", s[0], s[1], s[3], s[2])); ok++; }
            catch (PubFormatException ex) when (ex.InnerException is null) { rejected++; }
            catch (Exception ex) { Assert.Fail($"{group}/{variant} seed {seed} case {iter} (stream {which}, mode {mode}): {ex}"); }
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"{group}/{variant} seed {seed} case {iter} took {sw.Elapsed}");
        }
        output.WriteLine($"{group}/{variant}: {ok} imported, {rejected} rejected as format errors");
    }
}
