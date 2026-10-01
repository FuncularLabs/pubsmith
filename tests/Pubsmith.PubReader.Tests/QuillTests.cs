using System.Buffers.Binary;
using System.Text;
using Pubsmith.PubReader.Contents;
using Pubsmith.PubReader.OfficeArt;
using Pubsmith.PubReader.Quill;

namespace Pubsmith.PubReader.Tests;

// Layer 4: Quill text stories and formatting (docs/format/pub-format-notes.md §4).
public class QuillTests
{
    /// <summary>The story of the variant's single text box.</summary>
    private static Story StoryOf(string group, string variant)
    {
        var pkg = PubPackage.Open(Fixtures.Pub(group, variant));
        var contents = ContentsReader.Read(pkg.Contents);
        var stories = QuillReader.Read(pkg.Quill, new ColorResolver(contents.Palette));
        var tb = Assert.Single(EscherReader.ReadShapes(pkg.EscherStm), s => s.ShapeType == 202);
        var textId = contents.Shapes[tb.Seqnum!.Value].TextId!.Value;
        Assert.True(stories.TryGetValue(textId, out var story), $"no story for text id {textId}");
        return story!;
    }

    private static string Plain(Story s) => string.Join("\n", s.Paragraphs.Select(p => string.Concat(p.Runs.Select(r => r.Text))));

    [Fact]
    public void Base_TextAndDefaults()
    {
        var s = StoryOf("text", "base");
        Assert.Equal("Hello Example Text world", Plain(s));
        var run = Assert.Single(Assert.Single(s.Paragraphs).Runs);
        Assert.Equal("Calibri", run.Format.Font);
        Assert.Equal(10, run.Format.SizePt, 2);
        Assert.False(run.Format.Bold);
        Assert.False(run.Format.Italic);
    }

    [Fact]
    public void Bold_IsRead()
    {
        var run = Assert.Single(Assert.Single(StoryOf("text", "bold").Paragraphs).Runs);
        Assert.True(run.Format.Bold);
        Assert.False(run.Format.Italic);
    }

    [Fact]
    public void Size_IsReadInPoints()
    {
        var run = Assert.Single(Assert.Single(StoryOf("text", "size-24").Paragraphs).Runs);
        Assert.Equal(24, run.Format.SizePt, 2);
    }

    [Fact]
    public void RedWord_IsItsOwnRun()
    {
        var runs = Assert.Single(StoryOf("text", "run-red-word").Paragraphs).Runs;
        var red = Assert.Single(runs, r => r.Format.Color is { R: 255, G: 0, B: 0 });
        Assert.Equal("Example", red.Text.Trim());
        Assert.All(runs.Where(r => r != red), r => Assert.Equal((0, 0, 0), (r.Format.Color.R, r.Format.Color.G, r.Format.Color.B)));
    }

    [Fact]
    public void Alignment_Centre()
    {
        Assert.Equal(ParagraphAlign.Center, Assert.Single(StoryOf("text", "align-center").Paragraphs).Format.Alignment);
    }

    [Theory]
    [InlineData(0x0002u, ParagraphAlign.Center)]
    [InlineData(0x8002u, ParagraphAlign.Center)]   // as stored in real files (high bit set)
    [InlineData(0x8001u, ParagraphAlign.Right)]
    [InlineData(0x8006u, ParagraphAlign.Justify)]
    [InlineData(0x8000u, ParagraphAlign.Left)]
    public void Alignment_UsesTheLowByte(uint stored, ParagraphAlign expected) => Assert.Equal(expected, QuillReader.AlignmentFrom(stored));

    [Fact]
    public void LineSpacing_Exact30()
    {
        var f = Assert.Single(StoryOf("text", "ls-exact-30").Paragraphs).Format;
        Assert.Equal(LineSpacingKind.Exact, f.LineSpacing.Kind);
        Assert.Equal(30, f.LineSpacing.Value, 2);
    }

    [Fact]
    public void LineSpacing_DefaultIsMultiple()
    {
        var f = Assert.Single(StoryOf("text", "base").Paragraphs).Format;
        Assert.Equal(LineSpacingKind.Multiple, f.LineSpacing.Kind);
        Assert.InRange(f.LineSpacing.Value, 0.9, 1.3);
    }

    [Fact]
    public void TwoParagraphs_AreSplit()
    {
        var s = StoryOf("text2", "base");
        Assert.Equal(2, s.Paragraphs.Count);
        Assert.Equal("First paragraph here.\nSecond paragraph here.", Plain(s));
    }

    [Fact]
    public void Directory_ListsTheCoreChunks()
    {
        var pkg = PubPackage.Open(Fixtures.Pub("text", "base"));
        var names = QuillReader.ReadDirectory(pkg.Quill).Select(c => c.Name).ToHashSet();
        foreach (var n in new[] { "TEXT", "STRS", "SYID", "FDPC", "FDPP", "STSH", "FONT" }) Assert.Contains(n, names);
    }

    [Fact]
    public void ColorResolver_ReadsLiteralAndPaletteReferences()
    {
        var r = new ColorResolver([0x000000u, 0x00FF8040u]);
        Assert.Equal((0x11, 0x22, 0x33), Tuple(r.Resolve(0x00332211)));
        Assert.Equal((0x40, 0x80, 0xFF), Tuple(r.Resolve(0x08000001)));    // scheme/palette index 1 -> 0x00FF8040 (BGR)
        Assert.Equal((0, 0, 0), Tuple(r.Resolve(0x08000099)));             // out of range -> black
    }

    private static (int, int, int) Tuple(RgbColor c) => (c.R, c.G, c.B);

    [Fact]
    public void Italic_IsRead()
    {
        var run = Assert.Single(Assert.Single(StoryOf("text", "italic").Paragraphs).Runs);
        Assert.True(run.Format.Italic);
        Assert.False(run.Format.Bold);
    }

    [Fact]
    public void SpaceBeforeAndAfter_AreReadSeparately()
    {
        var before = Assert.Single(StoryOf("text", "space-before-12").Paragraphs).Format;
        var noAfter = Assert.Single(StoryOf("text", "space-after-0").Paragraphs).Format;
        var standard = Assert.Single(StoryOf("text", "base").Paragraphs).Format;
        Assert.Equal(12, before.SpaceBeforePt, 2);
        Assert.Equal(standard.SpaceAfterPt, before.SpaceAfterPt, 2);
        Assert.NotEqual(0, standard.SpaceAfterPt, 2);   // so the space-after-0 variant is a real change
        Assert.Equal(0, noAfter.SpaceAfterPt, 2);
        Assert.Equal(0, noAfter.SpaceBeforePt, 2);
    }

    [Theory]
    [InlineData("font-Arial", "Arial")]
    [InlineData("font-TimesNewRoman", "Times New Roman")]
    [InlineData("font-CourierNew", "Courier New")]
    public void Font_IsTheRunsOwnFace(string variant, string family) =>
        Assert.Equal(family, Assert.Single(Assert.Single(StoryOf("text", variant).Paragraphs).Runs).Format.Font);

    [Theory]
    [InlineData("ls-multiple-1.5", 1.5)]
    [InlineData("ls-double", 2.0)]
    public void LineSpacing_Multiples(string variant, double lines)
    {
        var f = Assert.Single(StoryOf("text", variant).Paragraphs).Format;
        Assert.Equal(LineSpacingKind.Multiple, f.LineSpacing.Kind);
        Assert.Equal(lines, f.LineSpacing.Value, 2);
    }

    [Fact]
    public void EmptyParagraph_KeepsItsMarksFormatting()
    {
        // "A", an empty paragraph, "B": one character run covering all of it, 60 pt.
        const string text = "A\r\rB\r";
        var textOffset = 0x18 + 8 + 4 * 24;   // TEXT is the first chunk body (4 chunks in the directory)
        var fdpc = new byte[8 + 4 + 2 + 10];
        BinaryPrimitives.WriteUInt16LittleEndian(fdpc, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(fdpc.AsSpan(8), (uint)(textOffset + text.Length * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(fdpc.AsSpan(12), 14);          // property record right after the tables
        BinaryPrimitives.WriteUInt32LittleEndian(fdpc.AsSpan(14), 10);          // record length, then block 0x0C (size), type 0x20 (u32)
        fdpc[18] = 0x0C; fdpc[19] = 0x20;
        BinaryPrimitives.WriteUInt32LittleEndian(fdpc.AsSpan(20), 60 * 12700);
        var q = HostileInputTests.Quill(("TEXT", Encoding.Unicode.GetBytes(text)),
            ("STRS", [.. HostileInputTests.U32(1), .. HostileInputTests.U32(4), .. HostileInputTests.U32((uint)text.Length)]),
            ("SYID", [.. HostileInputTests.U32(0), .. HostileInputTests.U32(1), .. HostileInputTests.U32(7)]), ("FDPC", fdpc));

        var story = Assert.Single(QuillReader.Read(q, new ColorResolver([0u])).Values);

        Assert.Equal(3, story.Paragraphs.Count);
        var mark = Assert.Single(story.Paragraphs[1].Runs);
        Assert.Equal("", mark.Text);
        Assert.Equal(60, mark.Format.SizePt, 2);
    }
}
