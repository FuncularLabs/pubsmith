using System.Diagnostics;
using System.Text;
using Pubsmith.Core;
using Pubsmith.PubReader.Contents;
using Pubsmith.PubReader.OfficeArt;
using Pubsmith.PubReader.Quill;
using static Pubsmith.PubReader.Tests.HostileInputTests;

namespace Pubsmith.PubReader.Tests;

// Three limits bound what an import produces. The unit budget bounds how many things are placed; the text budget
// bounds the strings they carry (text, WordArt, picture paths, and font names beyond their first 32 characters); and
// the document's JSON has a hard limit, enforced while it is streamed, so that whatever the shapes, no import writes
// more than PubImporter.MaxJsonBytes. Ordinary publications stay far inside all three.
public class OutputCeilingTests
{
    private static readonly CharFormat Plain = new("Arial", 10, false, false, 0, RgbColor.Black, false, false, 0);
    private static readonly ParaFormat Para = new(ParagraphAlign.Left, new LineSpacing(LineSpacingKind.Multiple, 1), 0, 0, 0, 0, 0);

    private static EscherShape Shape(uint seq, ushort spt = 1, Dictionary<ushort, uint>? props = null, Dictionary<ushort, byte[]>? complex = null) => new()
    {
        Offset = 0, ShapeId = seq, ShapeType = spt, Flags = 0, Seqnum = seq, Anchor = new AnchorEmu(0, 0, 1_270_000, 1_270_000),
        Props = props ?? (spt == 202 ? new Dictionary<ushort, uint> { [EscherShape.PropFillFlags] = 0x100000, [EscherShape.PropLineFlags] = 0x80000 } : []),
        Complex = complex ?? [], Children = [],
    };

    private static EscherShape Member(ushort spt, Dictionary<ushort, uint> props, AnchorEmu? anchor = null) => new()
    {
        Offset = 0, ShapeId = 0, ShapeType = spt, Flags = 0, Seqnum = null, Anchor = anchor ?? new AnchorEmu(0, 0, 1_270_000, 1_270_000),
        Props = props, Complex = new Dictionary<ushort, byte[]>(), Children = [],
    };

    private static EscherShape Group(IEnumerable<EscherShape> members) => Shape(1, 0) with { Flags = 0x1, Children = members.ToArray() };

    private static PubImportResult Repeated(PageInfo page, int times, IReadOnlyList<EscherShape> top, Dictionary<uint, ShapeInfo>? info = null,
        Dictionary<uint, Story>? stories = null, string name = "t", IReadOnlyList<Blip?>? blips = null) =>
        PubImporter.Compose(name, new ContentsModel(612 * 12700, 792 * 12700, [page], Enumerable.Repeat(page, times).ToList(), info ?? [], []),
            new ColorResolver([]), top, stories ?? [], blips ?? [], []);

    private static PubImportResult TextBoxRepeated(Story story, int times) =>
        Repeated(new PageInfo(9, false, null, null, [1]), times, [Shape(1, 202)], new() { [1] = new ShapeInfo(1, 0x01, story.TextId, null, null) }, new() { [story.TextId] = story });

    private static void RejectedForItsText(Func<PubImportResult> import)
    {
        var sw = Stopwatch.StartNew();
        var ex = Assert.Throws<PubFormatException>(() => import());
        Assert.Contains("characters of text", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }

    private static string TempDir() => Directory.CreateTempSubdirectory("pubsmith-out-").FullName;

    // ---------- text placed has its own budget ----------

    [Fact]
    public void AStoryRepeatedUpToTheUnitBudget_IsRejectedForItsText_FromBytes()
    {
        // 20 paragraphs of 11,263 characters (12 units each) in one text box, on a page listed until the units almost
        // run out: under the unit budget, it would write about 1.4 billion characters of JSON.
        var text = string.Concat(Enumerable.Repeat(new string('é', 11_263) + (char)13, 20));
        var quill = HostileInputTests.Quill([("TEXT", Encoding.Unicode.GetBytes(text)), ("STRS", [.. U32(1), .. U32(4), .. U32((uint)text.Length)]), ("SYID", [.. U32(0), .. U32(1), .. U32(9)])]);

        var one = PubImporter.Import(PubPackage.FromStreams("one.pub", ContentsWithTextBox(1), TextBoxStm(), [], quill));
        Assert.Equal(20, one.Document.Pages[0].Elements.OfType<TextElement>().Single().Paragraphs.Count);
        var times = PubImporter.MaxElements / one.UnitsPlaced;

        RejectedForItsText(() => PubImporter.Import(PubPackage.FromStreams("big.pub", ContentsWithTextBox(times), TextBoxStm(), [], quill)));
    }

    [Fact]
    public void LongWordArtRepeated_IsRejectedForItsText()
    {
        // 1,023 characters (one unit of text) on a page listed 60,000 times: 240,000 units, 61 million characters.
        var wordArt = Shape(1, 136, complex: new() { [EscherShape.PropGtextUnicode] = Encoding.Unicode.GetBytes(new string('é', 1023)) });
        RejectedForItsText(() => Repeated(new PageInfo(9, false, null, null, [1]), 60_000, [wordArt]));
    }

    [Fact]
    public void FontNamesCountAsText_BeyondTheirFirst32Characters()
    {
        // 1,000 one-character runs in a 256-character font, on a page listed 240 times: 240,960 units, and 54 million
        // characters (224 of each font name, and the text).
        var font = Plain with { Font = new string('F', PubImporter.MaxFontName) };
        var story = new Story(9, [new StoryParagraph(Para, Enumerable.Range(0, 1000).Select(_ => new StoryRun("x", font)).ToList())]);
        RejectedForItsText(() => TextBoxRepeated(story, 240));
    }

    [Fact]
    public void WordArtFontNamesCountAsText_BeyondTheirFirst32Characters()
    {
        // "x" in a 256-character font on a page listed 60,000 times: 240,000 units, 13.5 million characters.
        var wordArt = Shape(1, 136, complex: new()
        {
            [EscherShape.PropGtextUnicode] = Encoding.Unicode.GetBytes("x"),
            [EscherShape.PropGtextFont] = Encoding.Unicode.GetBytes(new string('é', PubImporter.MaxFontName)),
        });
        RejectedForItsText(() => Repeated(new PageInfo(9, false, null, null, [1]), 60_000, [wordArt]));
    }

    [Fact]
    public void WordArtInAnOrdinaryFont_CostsOnlyItsText()
    {
        var wordArt = Shape(1, 136, complex: new() { [EscherShape.PropGtextUnicode] = Encoding.Unicode.GetBytes("x") });   // in Arial
        Assert.Equal(1, Repeated(new PageInfo(9, false, null, null, [1]), 1, [wordArt]).TextPlaced);
    }

    [Fact]
    public void PicturePathsCountAsText()
    {
        // A publication with a 250-character name: each picture's path repeats it. 200 pictures in a group on a page
        // listed 1,200 times: 243,600 units, 64 million characters of paths.
        var props = new Dictionary<ushort, uint> { [EscherShape.PropPib] = 1 };
        RejectedForItsText(() => Repeated(new PageInfo(9, false, null, null, [1]), 1_200, [Group(Enumerable.Range(0, 200).Select(_ => Member(75, props)))],
            name: new string('n', 250), blips: [new Blip("png", [1, 2, 3])]));
    }

    [Fact]
    public void ALongNovel_Imports_ItsTextIsCountedExactly_AndItsJsonIsFarBelowTheLimit()
    {
        // 1,000 pages, each with its own 40 paragraphs of 150 characters in 4 runs, in "Palatino Linotype": 6 million
        // characters of text (real font names cost nothing), 203,000 units.
        var font = Plain with { Font = "Palatino Linotype" };
        var bodies = Enumerable.Range(0, 1000).Select(i => Shape((uint)(10 + i), 202)).ToArray();
        var pages = bodies.Select(b => new PageInfo(5000 + b.Seqnum!.Value, false, null, null, [b.Seqnum!.Value])).ToList();
        var info = bodies.ToDictionary(b => b.Seqnum!.Value, b => new ShapeInfo(b.Seqnum!.Value, 0x01, 100 + b.Seqnum!.Value, null, null));
        var stories = bodies.ToDictionary(b => 100 + b.Seqnum!.Value, b => new Story(100 + b.Seqnum!.Value,
            Enumerable.Range(0, 40).Select(_ => new StoryParagraph(Para, [.. new[] { 37, 38, 37, 38 }.Select(n => new StoryRun(new string('t', n), font))])).ToList()));

        var r = PubImporter.Compose("t", new ContentsModel(612 * 12700, 792 * 12700, pages, pages, info, []), new ColorResolver([]), bodies, stories, [], []);

        Assert.Equal(1000, r.Document.Pages.Count);
        Assert.Equal(6_000_000L, r.TextPlaced);
        var dir = TempDir();
        try
        {
            var json = new FileInfo(PubImporter.WriteTo(r, dir, "t")).Length;
            Assert.True(json < PubImporter.MaxJsonBytes / 2, $"{json >> 20} MB of JSON");
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- issues found while parsing ----------

    [Fact]
    public void ParseTimeIssues_CountAgainstTheUnits()
    {
        var issues = Enumerable.Repeat(new ImportIssue(0, null, "publication", ImportIssueKind.Dropped, "x"), PubImporter.MaxElements).ToList();
        var page = new PageInfo(9, false, null, null, []);
        Assert.Throws<PubFormatException>(() => PubImporter.Compose("t", new ContentsModel(612 * 12700, 792 * 12700, [page], [page], new Dictionary<uint, ShapeInfo>(), []),
            new ColorResolver([]), [], new Dictionary<uint, Story>(), [], issues));
    }

    [Fact]
    public void DuplicateTextIds_AreReportedOnce()
    {
        // 100,000 stories, all with text id 7: one issue, not 99,999.
        const int n = 100_000;
        var q = HostileInputTests.Quill(("TEXT", Encoding.Unicode.GetBytes(string.Concat(Enumerable.Repeat("a\r", n)))),
            ("STRS", [.. U32(n), .. U32(4), .. Enumerable.Repeat(U32(2), n).SelectMany(b => b)]),
            ("SYID", [.. U32(0), .. U32(n), .. Enumerable.Repeat(U32(7), n).SelectMany(b => b)]));
        var problems = new List<ImportIssue>();

        Assert.Single(QuillReader.Read(q, new ColorResolver([0u]), problems));

        var problem = Assert.Single(problems);
        Assert.Equal(ImportIssueKind.Dropped, problem.Kind);
        Assert.Contains($"{n - 1} stories", problem.Detail);
    }

    // ---------- font names: cut, reported once, and read past ----------

    [Fact]
    public void QuillFontNames_AreCutAndReported_AndTheNextFontIsRead()
    {
        // A 5,000-character name, then "Arial", used by the only run: the reader must advance past the whole name.
        var problems = new List<ImportIssue>();
        var stories = QuillReader.Read(QuillWithFonts(1, 7, new string('N', 5000), "Arial"), new ColorResolver([0u]), problems);

        Assert.Equal("Arial", Assert.Single(stories.Values).Paragraphs[0].Runs[0].Format.Font);
        var problem = Assert.Single(problems);
        Assert.Equal(ImportIssueKind.Approximated, problem.Kind);   // the text is placed, in a substitute font
        Assert.Contains("font 0", problem.Detail);
        Assert.Contains($"{PubImporter.MaxFontName}", problem.Detail);
    }

    [Fact]
    public void ManyCutFontNames_AreReportedOnce()
    {
        var problems = new List<ImportIssue>();
        var stories = QuillReader.Read(QuillWithFonts(3, 7, new string('A', 300), new string('B', 400), new string('C', 500), "Arial"), new ColorResolver([0u]), problems);

        Assert.Equal("Arial", Assert.Single(stories.Values).Paragraphs[0].Runs[0].Format.Font);
        var problem = Assert.Single(problems);
        Assert.Equal(ImportIssueKind.Approximated, problem.Kind);
        Assert.Contains("3 font names", problem.Detail);
    }

    [Fact]
    public void ACutFontName_IsReportedThroughImport_AsApproximated()
    {
        var r = PubImporter.Import(PubPackage.FromStreams("f.pub", ContentsWithTextBox(1), TextBoxStm(), [], QuillWithFonts(0, 9, new string('N', 5000))));

        Assert.Equal(PubImporter.MaxFontName, r.Document.Pages[0].Elements.OfType<TextElement>().Single().Paragraphs[0].Runs[0].Style.Family.Length);
        Assert.Contains(r.Issues, i => i.Kind == ImportIssueKind.Approximated && i.Detail.StartsWith("font 0:"));
        Assert.DoesNotContain(r.Issues, i => i.Kind == ImportIssueKind.Dropped);
    }

    [Fact]
    public void RunFontNamesLongerThanTheCap_AreCutAndReported()
    {
        // Stories built elsewhere than the reader (which already cuts) are cut when placed, and reported.
        var story = new Story(9, [new StoryParagraph(Para, [new StoryRun("Hi", Plain with { Font = new string('F', PubImporter.MaxFontName + 1) })])]);
        var r = TextBoxRepeated(story, 1);
        Assert.Equal(PubImporter.MaxFontName, r.Document.Pages[0].Elements.OfType<TextElement>().Single().Paragraphs[0].Runs[0].Style.Family.Length);
        Assert.Contains(r.Issues, i => i.Detail == $"text: font name longer than {PubImporter.MaxFontName} characters cut");
    }

    // ---------- the JSON written has a hard limit ----------

    [Fact]
    public void AHostileDocumentWithinBothBudgets_IsRefusedAtTheJsonLimit()
    {
        // Text boxes drawn with a frame, as members of a group: each costs one unit but writes two elements. A page of
        // 1,000 of them listed 248 times stays inside both budgets (248,744 units, no text) and would write about
        // 330 MB of JSON: refused once the limit is reached, and nothing is left behind.
        var props = new Dictionary<ushort, uint>
        {
            [EscherShape.PropFillFlags] = 0x100010, [EscherShape.PropLineFlags] = 0x80008, [0x023F] = 0x20002, [0x0201] = 0x112233,
            [EscherShape.PropRotation] = (uint)(30.3 * 65536),
        };
        var group = Group(Enumerable.Range(0, 1000).Select(_ => Member(202, props, new AnchorEmu(12_345, 67_891, 1_234_567, 987_653))));
        var r = Repeated(new PageInfo(9, false, null, null, [1]), 248, [group]);
        Assert.Equal(0, r.TextPlaced);
        Assert.True(r.UnitsPlaced <= PubImporter.MaxElements);
        var dir = TempDir();
        try
        {
            var sw = Stopwatch.StartNew();
            Assert.Throws<DocumentTooLargeException>(() => PubImporter.WriteTo(r, dir, "t"));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"took {sw.Elapsed}");
            Assert.False(File.Exists(Path.Combine(dir, "t.json")));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void WriteTo_ARefusedDocument_ChangesNothingInTheFolder()
    {
        // An earlier import of "flyer" with a picture; then a larger "flyer" whose picture 1 differs, refused at the
        // limit. Every file in the folder (document, report, picture) must be exactly as it was.
        // The larger one's text is underlined, so its report (an issue) differs from the earlier one's too.
        static PubImportResult Flyer(byte[] picture, int textLength, int underline) => Repeated(new PageInfo(9, false, null, null, [1, 2]), 1,
            [Shape(1, 75, new Dictionary<ushort, uint> { [EscherShape.PropPib] = 1 }), Shape(2, 202)],
            new() { [2] = new ShapeInfo(2, 0x01, 9, null, null) },
            new() { [9] = new Story(9, [new StoryParagraph(Para, [new StoryRun(new string('x', textLength), Plain with { Underline = underline })])]) },
            name: "flyer", blips: [new Blip("png", picture)]);
        static Dictionary<string, byte[]> Snapshot(string dir) =>
            Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(dir, f), File.ReadAllBytes);
        var dir = TempDir();
        try
        {
            var path = PubImporter.WriteTo(Flyer([1, 2, 3], 2, 0), dir, "flyer", 1 << 20);
            var before = Snapshot(dir);
            Assert.Contains(Path.Combine("flyer.assets", "img1.png"), before.Keys);

            Assert.Throws<DocumentTooLargeException>(() => PubImporter.WriteTo(Flyer([9, 9, 9, 9, 9, 9, 9], 10_000, 1), dir, "flyer", new FileInfo(path).Length + 100));

            var after = Snapshot(dir);
            Assert.Equal(before.Keys.Order(), after.Keys.Order());
            Assert.All(before, kv => Assert.Equal(kv.Value, after[kv.Key]));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void WriteTo_ThatFailsToMoveItsDocument_RemovesNoEarlierPicture()
    {
        // An earlier "flyer" with two pictures; then a re-import with one, whose document cannot be moved into place
        // (its name is taken by a folder). The earlier import's second picture may only be removed once the new
        // document is in place, so here it must stay.
        static PubImportResult Flyer(int pictures) => Repeated(new PageInfo(9, false, null, null, [.. Enumerable.Range(1, pictures).Select(i => (uint)i)]), 1,
            [.. Enumerable.Range(1, pictures).Select(i => Shape((uint)i, 75, new Dictionary<ushort, uint> { [EscherShape.PropPib] = (uint)i }))],
            name: "flyer", blips: [new Blip("png", [1, 2, 3]), new Blip("png", [4, 5, 6])]);
        var dir = TempDir();
        try
        {
            var json = PubImporter.WriteTo(Flyer(2), dir, "flyer");
            var second = Path.Combine(dir, "flyer.assets", "img2.png");
            Assert.True(File.Exists(second));
            File.Delete(json);
            Directory.CreateDirectory(json);

            var ex = Xunit.Record.Exception(() => PubImporter.WriteTo(Flyer(1), dir, "flyer"));

            Assert.True(ex is IOException or UnauthorizedAccessException, $"unexpected {ex}");
            Assert.True(File.Exists(second), "the earlier import's second picture was removed before the new document was in place");
            Assert.True(Directory.Exists(json));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void WriteTo_StreamsItsJson()
    {
        // About 30 MB of document and 30 MB of report: written as streams, saving allocates a small fraction of that.
        var story = new Story(9, [new StoryParagraph(Para, Enumerable.Range(0, 100_000).Select(_ => new StoryRun("x", Plain)).ToList())]);
        var r = TextBoxRepeated(story, 1);
        r = r with { Issues = Enumerable.Range(0, 150_000).Select(i => new ImportIssue(1, (uint)i, "shape", ImportIssueKind.Approximated, "shape: flip ignored")).ToList() };
        var dir = TempDir();
        try
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var json = PubImporter.WriteTo(r, dir, "t");
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            var written = new FileInfo(json).Length + new FileInfo(Path.Combine(dir, "t.import.json")).Length;
            Assert.True(written > 20L << 20, $"only {written >> 20} MB written");
            Assert.True(allocated < written / 4, $"allocated {allocated >> 20} MB to write {written >> 20} MB");
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- byte builders ----------

    private static byte[] U16(ushort v) => BitConverter.GetBytes(v);

    private static byte[] Rec(int ver, int inst, ushort type, byte[] body) =>
        [.. U16((ushort)(ver | (inst << 4))), .. U16(type), .. U32((uint)body.Length), .. body];

    /// <summary>EscherStm: one text box (type 202, seqnum 6) at 0,0, 100 pt square.</summary>
    private static byte[] TextBoxStm()
    {
        var anchor = Rec(0, 0, 0xF010, [.. U32(28), .. U16(0x2001), .. U32(0), .. U16(0x2002), .. U32(0), .. U16(0x2003), .. U32(1_270_000), .. U16(0x2004), .. U32(1_270_000)]);
        var box = Rec(0xF, 0, 0xF004, [.. Rec(2, 202, 0xF00A, [.. U32(1), .. U32(0)]), .. Rec(0, 0, 0xF011, [.. U32(10), .. U16(0x6801), .. U32(6)]), .. anchor]);
        return Rec(0xF, 0, 0xF002, box);
    }

    /// <summary>Contents: document (seqnum 0), page (1) listing shape 6 and placed <paramref name="times"/> times, internal pages 2-5, a text shape chunk (6) with text id 9.</summary>
    private static byte[] ContentsWithTextBox(int times)
    {
        byte[] doc = [0x12, 0x88, .. U32(4 + 12), 0x01, 0x20, .. U32(612 * 12700), 0x02, 0x20, .. U32(792 * 12700),
            0x02, 0x88, .. U32((uint)(4 + 6 * (times + 4))), .. Enumerable.Repeat<byte[]>([0x00, 0x20, .. U32(1)], times).SelectMany(b => b),
            .. new[] { 2u, 3u, 4u, 5u }.SelectMany(q => (byte[])[0x00, 0x20, .. U32(q)])];
        byte[] page = [0x02, 0x88, .. U32(4 + 6), 0x00, 0x70, .. U32(6)];
        byte[] shape = [0x27, 0x20, .. U32(9)];
        byte[][] chunks = [doc, page, shape];
        var body = new List<byte>(new byte[0x20]);
        var offsets = new List<int>();
        foreach (var c in chunks) { offsets.Add(body.Count); body.AddRange(U32((uint)(4 + c.Length))); body.AddRange(c); }
        var trailer = body.Count;
        var dir = new List<byte>();
        foreach (var (type, chunk) in new (byte, int)[] { (0x44, 0), (0x43, 1), (0x43, 1), (0x43, 1), (0x43, 1), (0x43, 1), (0x01, 2) })
            dir.AddRange([0x05, 0x88, .. U32(4 + 12), 0x02, 0x20, .. U32(type), 0x04, 0x20, .. U32((uint)offsets[chunk])]);
        body.AddRange(U32((uint)(4 + 6 + dir.Count)));
        body.AddRange([0x01, 0x90, .. U32((uint)(4 + dir.Count)), .. dir]);
        var c0 = body.ToArray();
        c0[0] = 0xE8; c0[1] = 0xAC; c0[2] = 0x2C;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(c0.AsSpan(0x1A), (uint)trailer);
        return c0;
    }

    /// <summary>A Quill stream with the given fonts and one story ("Hi", text id <paramref name="textId"/>) whose run is in font <paramref name="fontIndex"/>.</summary>
    private static byte[] QuillWithFonts(uint fontIndex, uint textId, params string[] names)
    {
        var font = new List<byte>();
        font.AddRange(U32(0)); font.AddRange(U32((uint)names.Length)); font.AddRange(new byte[12]);
        foreach (var _ in names) font.AddRange(new byte[4]);
        foreach (var n in names) { font.AddRange(U16((ushort)n.Length)); font.AddRange(Encoding.Unicode.GetBytes(n)); font.AddRange(new byte[4]); }
        var textOffset = 0x18 + 8 + 5 * 24;
        byte[] runRecord = [.. U32(22), 0x24, 0x88, .. U32(16), 0x01, 0x88, .. U32(10), 0x00, 0x20, .. U32(fontIndex)];
        byte[] fdpc = [.. U16(1), .. new byte[6], .. U32((uint)(textOffset + 6)), .. U16(14), .. runRecord];
        return HostileInputTests.Quill([("TEXT", Encoding.Unicode.GetBytes("Hi\r")), ("STRS", [.. U32(1), .. U32(4), .. U32(3)]),
            ("SYID", [.. U32(0), .. U32(1), .. U32(textId)]), ("FONT", [.. font]), ("FDPC", fdpc)]);
    }
}
