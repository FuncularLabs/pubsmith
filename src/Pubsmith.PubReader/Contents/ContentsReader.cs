using System.Buffers.Binary;

namespace Pubsmith.PubReader.Contents;

public sealed record PageInfo(uint Seqnum, bool IsMaster, uint? MasterSeqnum, uint? BackgroundSeqnum, IReadOnlyList<uint> ShapeSeqnums);

public sealed record ShapeInfo(uint Seqnum, byte ChunkType, uint? TextId, int? VerticalAlign, uint? CropShapeType);

/// <summary>What the Contents stream says about the publication (pages, masters, shape records, palette).</summary>
public sealed record ContentsModel(
    long PageWidthEmu,
    long PageHeightEmu,
    IReadOnlyList<PageInfo> Pages,
    IReadOnlyList<PageInfo> ContentPages,
    IReadOnlyDictionary<uint, ShapeInfo> Shapes,
    IReadOnlyList<uint> Palette);

/// <summary>Reads the Contents stream (docs/format/pub-format-notes.md §1).</summary>
public static class ContentsReader
{
    private const byte Shape = 0x01, Table = 0x10, AltShape = 0x20, Group = 0x30, Logo = 0x31, PageChunk = 0x43, Document = 0x44, PaletteChunk = 0x5C;

    private sealed record ChunkRef(uint Seqnum, byte Type, int Offset);

    /// <summary>Largest page side accepted: 480 inches (40 feet), far beyond any real publication.</summary>
    public const long MaxPageEmu = 480L * 914400;

    public static ContentsModel Read(byte[] contents)
    {
        if (contents.Length < 0x1E) throw new PubFormatException("Contents is too short to hold a trailer offset.");
        var rawTrailer = BinaryPrimitives.ReadUInt32LittleEndian(contents.AsSpan(0x1A));
        if (rawTrailer == 0 || rawTrailer + 4L > contents.Length) throw new PubFormatException($"Contents trailer offset {rawTrailer} is outside the stream.");
        var trailer = (int)rawTrailer;
        var trailerEnd = (int)Math.Min(contents.Length, trailer + (long)BinaryPrimitives.ReadUInt32LittleEndian(contents.AsSpan(trailer)));

        var chunks = new List<ChunkRef>();
        var directory = Blocks.Sequence(contents, trailer + 4, trailerEnd).FirstOrDefault(b => b.Type == 0x90);
        if (directory.Type != 0x90) throw new PubFormatException("Contents trailer has no chunk directory.");
        uint seq = 0;
        foreach (var entry in Blocks.Children(contents, directory))
        {
            if (entry.Type == 0x88)
            {
                byte? type = null; int? offset = null;
                foreach (var sub in Blocks.Children(contents, entry))
                {
                    if (sub.Id == 0x02) type = (byte)sub.Value;
                    else if (sub.Id == 0x04) offset = (int)sub.Value;
                }
                if (type is not null && offset is not null) chunks.Add(new ChunkRef(seq, type.Value, offset.Value));
            }
            seq++;   // the seqnum is the position in the directory, counting every child
        }

        long width = 0, height = 0;
        var order = new List<uint>();
        var pages = new Dictionary<uint, PageInfo>();
        var shapes = new Dictionary<uint, ShapeInfo>();
        var palette = new List<uint>();

        // Chunks do not overlap in real files, so their bodies add up to at most the stream. A directory that points
        // many entries at the same large chunk would re-parse it once per entry; stop well before that gets slow.
        long parsed = 0;
        foreach (var c in chunks)
        {
            var body = ChunkBody(contents, c);
            parsed += body.Count > 0 ? body[^1].End - c.Offset : 0;
            if (parsed > 2L * contents.Length + 65536) throw new PubFormatException("Contents chunk directory points at overlapping chunks.");
            switch (c.Type)
            {
                case Document:
                    foreach (var b in body)
                    {
                        if (b.Id == 0x12 && b.IsContainer)
                            foreach (var d in Blocks.Children(contents, b)) { if (d.Id == 0x01) width = d.Value; else if (d.Id == 0x02) height = d.Value; }
                        else if (b.Id == 0x02 && b.IsContainer)
                            foreach (var p in Blocks.Children(contents, b)) if (p.Id == 0x00 && !p.IsContainer && p.DataLength == 4) order.Add(p.Value);
                    }
                    break;
                case PageChunk:
                    {
                        bool master = false; uint? applied = null, bg = null; var list = new List<uint>();
                        foreach (var b in body)
                        {
                            if (b.Id == 0x02 && b.IsContainer) { foreach (var s in Blocks.Children(contents, b)) if (s.Type == 0x70) list.Add(s.Value); }
                            else if (b.Id == 0x0E && b.IsString) master = b.DataLength > 4 && contents.AsSpan(b.DataOffset + 4, b.DataLength - 4).ContainsAnyExcept((byte)0);
                            else if (b.Id == 0x0D && !b.IsContainer) applied = b.Value;
                            else if (b.Id == 0x0A && !b.IsContainer) bg = b.Value;
                        }
                        pages[c.Seqnum] = new PageInfo(c.Seqnum, master, applied, bg, list);
                        break;
                    }
                case Shape or AltShape or Table or Group or Logo:
                    {
                        uint? textId = null, crop = null; int? valign = null;
                        foreach (var b in body)
                        {
                            if (b.IsContainer || b.IsString) continue;
                            switch (b.Id)
                            {
                                case 0x27 when c.Type is not (Group or Logo): textId = b.Value; break;
                                case 0x35: valign = (int)b.Value; break;
                                case 0xB7: crop = b.Value == 0 ? null : b.Value; break;
                            }
                        }
                        shapes[c.Seqnum] = new ShapeInfo(c.Seqnum, c.Type, textId, valign, crop);
                        break;
                    }
                case PaletteChunk:
                    foreach (var b in body.Where(b => b.Type == 0xA0))
                        foreach (var e in Blocks.Children(contents, b))
                        {
                            if (e.Type == 0x88) palette.Add(Blocks.Children(contents, e).Where(x => x.Id == 0x01).Select(x => (uint?)x.Value).FirstOrDefault() ?? 0);   // missing -> black
                            else if (e.Type == 0x78) palette.Add(0);
                        }
                    break;
            }
        }
        if (width <= 0 || height <= 0) throw new PubFormatException("Contents has no document page size.");
        if (width > MaxPageEmu || height > MaxPageEmu)
            throw new PubFormatException($"Contents gives an impossible page size ({width / 914400.0:0.#} x {height / 914400.0:0.#} in).");
        if (palette.Count < 8) palette.Insert(0, 0);   // libmspub's observed fix-up: an implicit black slot 0

        // Content pages. INFERRED from 36 real files (Publisher 2003-365) and the diff corpus: the document page
        // list is always [masters...][content pages...][4 internal pages], the internal ones last (one of them
        // holds scratch-area objects, which Publisher does not print). libmspub skips them by hard-coded seqnums
        // (0x10D, 0x110, 0x113, 0x117), which the 2012 calendars disprove (their internal pages are 269/272/273/277).
        // Neither page block 0x06 nor document block 0x23 is a page type/count (both disproved by the same files).
        // The four are counted on the RAW list: an internal entry is not always a PAGE chunk (in some files it is
        // empty or another type), so filtering to known pages first would eat a real page instead.
        const int InternalPages = 4;
        var listed = order.Where(s => !(pages.TryGetValue(s, out var p) && p.IsMaster)).ToList();
        if (listed.Count <= InternalPages)
            throw new PubFormatException($"Contents page list has {listed.Count} non-master entries; expected content pages followed by {InternalPages} internal pages.");
        var contentSeqnums = listed.Take(Math.Max(0, listed.Count - InternalPages)).ToList();
        var missing = contentSeqnums.Where(s => !pages.ContainsKey(s)).ToList();
        if (missing.Count > 0)
            throw new PubFormatException($"Contents lists content page(s) {string.Join(", ", missing)} with no PAGE chunk.");
        var content = contentSeqnums.Where(pages.ContainsKey).Select(s => pages[s]).ToList();
        return new ContentsModel(width, height, pages.Values.OrderBy(p => p.Seqnum).ToList(), content, shapes, palette);
    }

    private static List<Block> ChunkBody(byte[] contents, ChunkRef c)
    {
        if (c.Offset < 0 || c.Offset + 4L > contents.Length) throw new PubFormatException($"Chunk {c.Seqnum} offset {c.Offset} is outside Contents.");
        var len = BinaryPrimitives.ReadUInt32LittleEndian(contents.AsSpan(c.Offset));
        if (len < 4 || c.Offset + (long)len > contents.Length) throw new PubFormatException($"Chunk {c.Seqnum} (type 0x{c.Type:X2}) has an impossible length {len}.");
        return Blocks.Sequence(contents, c.Offset + 4, c.Offset + (int)len).ToList();
    }
}
