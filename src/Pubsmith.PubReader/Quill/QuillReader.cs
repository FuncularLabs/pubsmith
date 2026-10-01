using System.Buffers.Binary;
using System.Text;
using Pubsmith.PubReader.Contents;

namespace Pubsmith.PubReader.Quill;

/// <summary>Reads text stories and their formatting from Quill/QuillSub/CONTENTS (docs/format/pub-format-notes.md §4).</summary>
public static class QuillReader
{
    private const double EmuPerPt = 12700;

    /// <summary>Directory entries beyond this are treated as damage (real files list a few dozen chunks).</summary>
    public const int MaxChunks = 100_000;

    /// <summary>
    /// Property-record bytes parsed per stream (each record once) before the formatting is treated as damaged.
    /// Real files use kilobytes; the cap stops tables that point many entries into one huge record. A parsed block
    /// is a 20-byte struct per 2 bytes at worst, plus list growth, so this also bounds their memory (about 80 MB).
    /// </summary>
    public const long PropertyBudget = 4L << 20;

    /// <summary>The empty property record (no style, no run): one shared list, never modified.</summary>
    private static readonly List<Block> None = [];

    /// <summary>The chunk directory: a linked list of pages starting at 0x18, 24-byte entries.</summary>
    public static IReadOnlyList<QuillChunk> ReadDirectory(byte[] q)
    {
        var chunks = new List<QuillChunk>();
        var seen = new HashSet<long>();
        long page = 0x18;
        while (page + 8 <= q.Length && seen.Add(page))
        {
            var at = (int)page;
            var count = BinaryPrimitives.ReadUInt16LittleEndian(q.AsSpan(at + 2));
            var next = BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(at + 4));
            if (chunks.Count + count > MaxChunks) throw new PubFormatException($"Quill directory lists more than {MaxChunks} chunks.");
            for (var i = 0; i < count; i++)
            {
                var e = at + 8 + (long)i * 24;
                if (e + 24 > q.Length) throw new PubFormatException($"Quill directory entry at {e} runs past the stream.");
                var entry = (int)e;
                var name = Encoding.ASCII.GetString(q, entry + 2, 4);
                var id = BinaryPrimitives.ReadUInt16LittleEndian(q.AsSpan(entry + 6));
                var offset = BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(entry + 16));
                var length = BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(entry + 20));
                if (offset + (long)length > q.Length) throw new PubFormatException($"Quill chunk {name} at {offset} (+{length}) is outside the stream.");
                chunks.Add(new QuillChunk(name, id, (int)offset, (int)length));
            }
            if (next == 0xFFFFFFFF) break;
            page = next;
        }
        return chunks;
    }

    /// <summary>Stories keyed by text id (the Contents shape record's 0x27). Empty when the file has no text.</summary>
    /// <param name="problems">Receives what could not be read as stored, as publication issues, each kind once: text
    /// that could not be attached to a text id (dropped) and font names cut to <see cref="PubImporter.MaxFontName"/>
    /// (approximated).</param>
    public static IReadOnlyDictionary<uint, Story> Read(byte[] q, ColorResolver colors, ICollection<ImportIssue>? problems = null)
    {
        var result = new Dictionary<uint, Story>();
        if (q.Length < 0x20) return result;
        var dir = ReadDirectory(q);
        QuillChunk? One(string n) => dir.FirstOrDefault(c => c.Name == n);
        var text = One("TEXT"); var strs = One("STRS"); var syid = One("SYID");
        if (text is null || strs is null || syid is null) return result;

        var reader = new Reader(q);
        var fonts = ReadFonts(q, One("FONT"), problems);
        var textColors = ReadColorList(q, One("PL  "));
        var stsh = dir.Where(c => c.Name == "STSH").ToList();
        var styles = stsh.Count > 1 ? ReadStyleSheet(reader, stsh[1]) : [];
        var charRuns = new RunIndex(ReadRunTables(q, dir, "FDPC"));
        var paraRuns = new RunIndex(ReadRunTables(q, dir, "FDPP"));

        // Stories: STRS lengths (UTF-16 code units), SYID text ids, TEXT concatenated. Each table stays inside its chunk.
        Need(strs, 8); Need(syid, 8);
        var nStories = BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(strs.Offset));
        var skip = BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(strs.Offset + 4));
        var lengthsAt = strs.Offset + 4L + skip;
        var nIds = BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(syid.Offset + 4));
        if (lengthsAt + nStories * 4L > strs.Offset + (long)strs.Length || 8 + nIds * 4L > syid.Length)
            throw new PubFormatException("Quill STRS/SYID tables run past their chunks.");

        var textEnd = text.Offset + (long)text.Length;
        long start = 0;   // in code units from the TEXT chunk start
        var paired = Math.Min(nStories, nIds);
        var reused = 0;
        uint firstReused = 0;
        for (var j = 0; j < paired; j++)
        {
            var len = BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan((int)(lengthsAt + j * 4L)));
            var textId = BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(syid.Offset + 8 + j * 4));
            if (text.Offset + (start + len) * 2 > textEnd) throw new PubFormatException($"Quill story {j} runs past the TEXT data.");
            if (result.ContainsKey(textId) && reused++ == 0) firstReused = textId;
            result[textId] = BuildStory(reader, textId, text.Offset, (int)start, (int)len, charRuns, paraRuns, styles, fonts, textColors, colors);
            start += len;
        }
        // Each kind of problem is reported once, however often it occurs, so a file cannot multiply its issues.
        if (reused > 0)
            problems?.Add(Problem(ImportIssueKind.Dropped, reused == 1 ? $"text id {firstReused} is used by more than one story; the last one is kept"
                : $"{reused} stories reuse a text id already used (the first: {firstReused}); for each id, the last story is kept"));
        if (nStories > nIds)
            problems?.Add(Problem(ImportIssueKind.Dropped, nStories - nIds == 1 ? "1 text story has no id; its text was not imported" : $"{nStories - nIds} text stories have no id; their text was not imported"));
        return result;
    }

    private static void Need(QuillChunk c, long bytes)
    {
        if (c.Length < bytes) throw new PubFormatException($"Quill {c.Name.Trim()} chunk at {c.Offset} is {c.Length} bytes; it needs {bytes}.");
    }

    /// <summary>
    /// All <paramref name="name"/> run tables, in directory order. Each run takes 6 table bytes, so real tables never
    /// hold more runs than the stream has room for; directory entries that point at the same table would repeat it.
    /// </summary>
    private static List<(int End, int PropsOffset)> ReadRunTables(byte[] q, IReadOnlyList<QuillChunk> dir, string name)
    {
        var runs = new List<(int, int)>();
        foreach (var c in dir.Where(c => c.Name == name))
        {
            runs.AddRange(ReadRuns(q, c));
            if (runs.Count * 6L > q.Length) throw new PubFormatException($"Quill {name} run tables overlap: more runs than the stream can hold.");
        }
        return runs;
    }

    /// <summary>FDPC/FDPP: u16 n, 6 unknown bytes, n × u32 absolute text end, n × u16 property-record offsets.</summary>
    private static List<(int End, int PropsOffset)> ReadRuns(byte[] q, QuillChunk c)
    {
        Need(c, 8);
        var n = BinaryPrimitives.ReadUInt16LittleEndian(q.AsSpan(c.Offset));
        Need(c, 8 + n * 6);
        var runs = new List<(int, int)>(n);
        for (var i = 0; i < n; i++)
        {
            var end = BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(c.Offset + 8 + i * 4));
            var prop = BinaryPrimitives.ReadUInt16LittleEndian(q.AsSpan(c.Offset + 8 + n * 4 + i * 2));
            runs.Add(((int)Math.Min(end, int.MaxValue), prop == 0 ? -1 : c.Offset + prop));
        }
        return runs;
    }

    /// <summary>
    /// Formatting runs in file order. <see cref="Find"/> returns the first run, in that order, whose end is past a
    /// position, in O(log n): the running maximum of the ends is non-decreasing, and the first index where it
    /// exceeds the position is exactly the first run whose own end does.
    /// </summary>
    internal sealed class RunIndex
    {
        private readonly int[] _maxEnd;
        private readonly int[] _props;

        public RunIndex(IReadOnlyList<(int End, int PropsOffset)> runs)
        {
            _maxEnd = new int[runs.Count];
            _props = new int[runs.Count];
            var max = int.MinValue;
            for (var i = 0; i < runs.Count; i++)
            {
                max = Math.Max(max, runs[i].End);
                _maxEnd[i] = max;
                _props[i] = runs[i].PropsOffset;
            }
        }

        /// <summary>The property-record offset of the run covering absolute byte <paramref name="at"/> (-1 if none).</summary>
        public int Find(int at)
        {
            int lo = 0, hi = _maxEnd.Length;
            while (lo < hi)
            {
                var mid = (lo + hi) >>> 1;
                if (_maxEnd[mid] > at) hi = mid; else lo = mid + 1;
            }
            return lo < _props.Length ? _props[lo] : -1;
        }
    }

    /// <summary>Parses each property record once (runs and styles share them) and enforces <see cref="PropertyBudget"/>.</summary>
    private sealed class Reader(byte[] q)
    {
        private readonly Dictionary<int, List<Block>> _cache = [];
        private long _budget = PropertyBudget;

        public byte[] Q { get; } = q;

        private readonly Dictionary<List<Block>, CharFacts> _char = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<List<Block>, ParaFacts> _para = new(ReferenceEqualityComparer.Instance);

        /// <summary>What a character-property record says, read once per record (runs and styles reuse records).</summary>
        public CharFacts Char(List<Block> b)
        {
            if (_char.TryGetValue(b, out var known)) return known;
            var facts = CharFactsOf(Q, b);
            _char[b] = facts;
            return facts;
        }

        /// <summary>What a paragraph-property record says, read once per record.</summary>
        public ParaFacts Para(List<Block> b)
        {
            if (_para.TryGetValue(b, out var known)) return known;
            var facts = ParaFactsOf(b);
            _para[b] = facts;
            return facts;
        }

        /// <summary>A property record: u32 length (including itself), then blocks. The list is shared: do not modify it.</summary>
        public List<Block> Props(int at)
        {
            if (at < 0) return None;
            if (_cache.TryGetValue(at, out var cached)) return cached;
            if (at + 4L > Q.Length) throw new PubFormatException($"Quill property record at {at} is outside the stream.");
            var len = BinaryPrimitives.ReadUInt32LittleEndian(Q.AsSpan(at));
            if (len < 4 || at + (long)len > Q.Length) throw new PubFormatException($"Quill property record at {at} has an impossible length {len}.");
            _budget -= len;
            if (_budget < 0) throw new PubFormatException("Quill formatting records add up to more than any real file holds; the text formatting is damaged.");
            var blocks = Blocks.Sequence(Q, at + 4, at + (int)len).ToList();
            _cache[at] = blocks;
            return blocks;
        }
    }

    /// <param name="Font">Read only when asked for (a style's font is consulted only when the run names none, and a
    /// damaged font container in an unused style must not cost the text). Likewise <paramref name="Color"/>.</param>
    internal sealed record CharFacts(bool Bold, bool Italic, bool AllCaps, bool SmallCaps, uint? Size, uint? Underline, uint? SuperSub, Lazy<uint?> Font, Lazy<uint?> Color);

    /// <summary>The facts of a character-property record: booleans by presence, scalars by first plain block.</summary>
    internal static CharFacts CharFactsOf(byte[] q, List<Block> b)
    {
        bool Has(byte id) { foreach (var x in b) if (x.Id == id) return true; return false; }
        uint? Val(byte id) { foreach (var x in b) if (x.Id == id && !x.IsContainer && !x.IsString) return x.Value; return null; }
        return new CharFacts(Has(0x02), Has(0x03), Has(0x14), Has(0x13), Val(0x0C), Val(0x1E), Val(0x0F),
            new Lazy<uint?>(() => FontIndex(q, b)), new Lazy<uint?>(() => ColorIndex(q, b)));
    }

    /// <param name="First">The first value of each non-container, non-string block id.</param>
    /// <param name="StyleIndex">Block 0x19 (the paragraph's style index), as the first non-container 0x19 block.</param>
    internal sealed record ParaFacts(Dictionary<byte, uint> First, uint? StyleIndex)
    {
        public uint? Val(byte id) => First.TryGetValue(id, out var v) ? v : null;
    }

    /// <summary>The facts of a paragraph-property record, keeping the first of any repeated block.</summary>
    internal static ParaFacts ParaFactsOf(List<Block> b)
    {
        var first = new Dictionary<byte, uint>();
        uint? style = null;
        foreach (var x in b)
        {
            if (x.Id == 0x19 && !x.IsContainer && style is null) style = x.Value;
            if (!x.IsContainer && !x.IsString) first.TryAdd(x.Id, x.Value);
        }
        return new ParaFacts(first, style);
    }

    private static ImportIssue Problem(ImportIssueKind kind, string detail) => new(0, null, "publication", kind, detail);

    private static List<string> ReadFonts(byte[] q, QuillChunk? c, ICollection<ImportIssue>? problems)
    {
        var fonts = new List<string>();
        if (c is null) return fonts;
        Need(c, 8);
        var n = BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(c.Offset + 4));
        var end = c.Offset + (long)c.Length;
        var p = c.Offset + 8 + 12 + 4L * n;
        if (p > end) throw new PubFormatException($"Quill FONT chunk lists {n} fonts in {c.Length} bytes.");
        var cut = 0;
        (int Index, int Chars) firstCut = default;
        for (var i = 0; i < n && p + 2 <= end; i++)
        {
            var chars = BinaryPrimitives.ReadUInt16LittleEndian(q.AsSpan((int)p));
            if (p + 2 + chars * 2L > end) throw new PubFormatException($"Quill font name at {p} runs past its chunk.");
            // Real font names are at most 31 characters; one longer than MaxFontName is damage, and is cut so that
            // every run using it cannot repeat it in full. The reader still steps over the whole name.
            fonts.Add(Encoding.Unicode.GetString(q, (int)p + 2, Math.Min((int)chars, PubImporter.MaxFontName) * 2));
            if (chars > PubImporter.MaxFontName && cut++ == 0) firstCut = (i, chars);
            p += 2 + chars * 2 + 4;
        }
        // Reported once: the text is still placed, in a substitute font, so it is approximated, not dropped.
        if (cut > 0)
            problems?.Add(Problem(ImportIssueKind.Approximated, cut == 1
                ? $"font {firstCut.Index}: its name is {firstCut.Chars} characters long, so it was cut to {PubImporter.MaxFontName}; text in it is drawn in a substitute font"
                : $"{cut} font names are longer than {PubImporter.MaxFontName} characters (the first: font {firstCut.Index}); they were cut, and text in them is drawn in a substitute font"));
        return fonts;
    }

    private static List<uint> ReadColorList(byte[] q, QuillChunk? c)
    {
        var list = new List<uint>();
        if (c is null) return list;
        Need(c, 12);
        var n = BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(c.Offset));
        var end = c.Offset + (long)c.Length;
        var p = c.Offset + 12L;
        for (var i = 0; i < n; i++)   // each entry is at least 4 bytes, or the list is damaged: at most Length / 4 rounds
        {
            var len = p + 4 <= end ? BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan((int)p)) : 0;
            if (len < 4 || p + len > end) throw new PubFormatException($"Quill colour entry at {p} has an impossible length {len}.");
            list.Add(First(Blocks.Sequence(q, (int)p + 4, (int)(p + len)), b => b.Id == 0x01) is { } v ? v.Value : 0);   // missing -> black
            p += len;
        }
        return list;
    }

    /// <summary>STSH (second instance): even entries are character styles, odd are paragraph styles.</summary>
    private static List<List<Block>> ReadStyleSheet(Reader reader, QuillChunk c)
    {
        var q = reader.Q;
        Need(c, 20);
        var n = BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(c.Offset + 4));
        if (20 + n * 4L > c.Length) throw new PubFormatException($"Quill style sheet claims {n} styles in {c.Length} bytes.");
        var table = c.Offset + 20;
        var list = new List<List<Block>>((int)n);
        for (var i = 0; i < n; i++)
        {
            var at = table + (long)BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(table + i * 4)) + 2;
            if (at > int.MaxValue) throw new PubFormatException($"Quill style {i} is outside the stream.");
            list.Add(reader.Props((int)at));
        }
        return list;
    }

    private static Story BuildStory(Reader reader, uint textId, int textOffset, int start, int len,
        RunIndex charRuns, RunIndex paraRuns, List<List<Block>> styles, List<string> fonts, List<uint> textColors, ColorResolver colors)
    {
        var q = reader.Q;
        var paragraphs = new List<StoryParagraph>();
        var chars = Encoding.Unicode.GetString(q, textOffset + start * 2, len * 2);
        var paraStart = 0;
        for (var k = 0; k <= chars.Length; k++)
        {
            var atEnd = k == chars.Length;
            if (!atEnd && chars[k] != '\r') continue;
            if (atEnd && k == paraStart) break;   // nothing after the final paragraph mark
            // The paragraph's formatting is the FDPP run that covers its paragraph mark (or its last character).
            var markByte = textOffset + (start + Math.Min(k, chars.Length - 1)) * 2;
            var paraProps = reader.Props(paraRuns.Find(markByte));
            var styleIndex = reader.Para(paraProps).StyleIndex ?? 0u;   // missing -> style 0
            var charStyle = styleIndex * 2L < styles.Count ? styles[(int)styleIndex * 2] : None;
            var paraStyle = styleIndex * 2L + 1 < styles.Count ? styles[(int)styleIndex * 2 + 1] : None;
            var paraFormat = ParseParagraph(reader.Para(paraStyle), reader.Para(paraProps));

            var runs = new List<StoryRun>();
            var buffer = new StringBuilder();
            var currentProps = -2;
            for (var i = paraStart; i < k; i++)
            {
                var props = charRuns.Find(textOffset + (start + i) * 2);
                if (props != currentProps && buffer.Length > 0)
                {
                    runs.Add(new StoryRun(buffer.ToString(), ParseChar(reader, charStyle, currentProps, fonts, textColors, colors)));
                    buffer.Clear();
                }
                currentProps = props;
                var ch = chars[i];
                if (ch is '\v' or '\n') buffer.Append('\n');         // line break
                else if (ch == '\t' || ch >= ' ') buffer.Append(ch);   // other control characters (fields) dropped
            }
            if (buffer.Length > 0) runs.Add(new StoryRun(buffer.ToString(), ParseChar(reader, charStyle, currentProps, fonts, textColors, colors)));
            // An empty paragraph still has a height: that of its paragraph mark's formatting, kept as an empty run.
            if (runs.Count == 0) runs.Add(new StoryRun("", ParseChar(reader, charStyle, charRuns.Find(markByte), fonts, textColors, colors)));
            paragraphs.Add(new StoryParagraph(paraFormat, runs));
            paraStart = k + 1;
        }
        return new Story(textId, paragraphs);
    }

    private static CharFormat ParseChar(Reader reader, List<Block> style, int runProps, List<string> fonts, List<uint> textColors, ColorResolver colors)
    {
        var run = reader.Char(reader.Props(runProps));
        var st = reader.Char(style);

        // Booleans in a run toggle the style's value (XOR); scalars override it.
        var bold = run.Bold ^ st.Bold;
        var italic = run.Italic ^ st.Italic;
        var allCaps = run.AllCaps ^ st.AllCaps;
        var smallCaps = run.SmallCaps ^ st.SmallCaps;
        var sizeEmu = run.Size ?? st.Size;
        var underline = (int)((run.Underline ?? st.Underline ?? 0) & 0xFF);
        var superSub = (int)(run.SuperSub ?? st.SuperSub ?? 0);
        var fontIndex = run.Font.Value ?? st.Font.Value;     // the style is consulted only when the run names none
        var colorIndex = run.Color.Value ?? st.Color.Value;

        var font = fontIndex is { } fi && fi < fonts.Count ? fonts[(int)fi] : fonts.FirstOrDefault() ?? "Calibri";
        var color = colorIndex is { } ci && ci < textColors.Count ? colors.Resolve(textColors[(int)ci]) : RgbColor.Black;
        return new CharFormat(font, sizeEmu is { } s ? s / EmuPerPt : 10, bold, italic, underline, color, allCaps, smallCaps, superSub);
    }

    // Block is a struct: FirstOrDefault on no match returns default(Block), whose Size is still 2, so every
    // lookup here goes through First() with an explicit "found" result instead.
    private static Block? First(IEnumerable<Block> blocks, Func<Block, bool> match)
    {
        foreach (var b in blocks) if (match(b)) return b;
        return null;
    }

    private static uint? FontIndex(byte[] q, List<Block> props)
    {
        if (First(props, b => b.Id == 0x24 && b.IsContainer) is not { } c) return null;
        if (First(Blocks.Children(q, c), b => b.Type == 0x88) is not { } inner) return null;
        return First(Blocks.Children(q, inner), b => !b.IsContainer && !b.IsString) is { } v ? v.Value : null;
    }

    private static uint? ColorIndex(byte[] q, List<Block> props)
    {
        if (First(props, b => b.Id == 0x2E && !b.IsContainer) is { } bare) return bare.Value;
        if (First(props, b => b.Id == 0x44 && b.IsContainer) is not { } c) return null;
        return First(Blocks.Children(q, c), b => b.Id == 0x00 && !b.IsContainer) is { } v ? v.Value : null;
    }

    /// <summary>Paragraph block 0x04: only the LOW byte is the alignment (real files store e.g. 0x8002 = centre).</summary>
    internal static ParagraphAlign AlignmentFrom(uint value) => (value & 0xFF) switch
    {
        1 => ParagraphAlign.Right, 2 => ParagraphAlign.Center, 6 => ParagraphAlign.Justify, _ => ParagraphAlign.Left,
    };

    private static ParaFormat ParseParagraph(ParaFacts style, ParaFacts props)
    {
        uint? Val(byte id) => props.Val(id) ?? style.Val(id);   // the paragraph's own value, else its style's
        var align = AlignmentFrom(Val(0x04) ?? 0);
        var spacing = new LineSpacing(LineSpacingKind.Multiple, 1.0);
        if (Val(0x34) is { } v)
        {
            if ((v & 1) != 0) spacing = new LineSpacing(LineSpacingKind.Exact, (v - 1) / 8.0 / EmuPerPt);
            else if ((v & 2) != 0) spacing = new LineSpacing(LineSpacingKind.Multiple, (v - 2) / 1219200.0);
        }
        double Pt(byte id) => (int)(Val(id) ?? 0) / EmuPerPt;
        return new ParaFormat(align, spacing, Pt(0x12), Pt(0x13), Pt(0x0D), Pt(0x0E), Pt(0x0C));
    }
}
