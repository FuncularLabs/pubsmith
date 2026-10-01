using System.Buffers.Binary;
using System.IO.Compression;

namespace Pubsmith.PubReader.OfficeArt;

/// <summary>A picture from the OfficeArt picture store: original encoded bytes plus a file extension.</summary>
public sealed record Blip(string Extension, byte[] Data);

/// <summary>Reads shapes and pictures from Publisher's OfficeArt streams (MS-ODRAW).</summary>
public static class EscherReader
{
    private const ushort SpContainer = 0xF004, SpgrContainer = 0xF003, DgContainer = 0xF002, DggContainer = 0xF000,
        BStoreContainer = 0xF001, Fbse = 0xF007, Fsp = 0xF00A, Fopt = 0xF00B, SecondaryFopt = 0xF121,
        TertiaryFopt = 0xF122, ClientAnchor = 0xF010;

    /// <summary>
    /// Every shape container in the stream, in stream order. Publisher separates top-level containers with its
    /// own framing bytes, so between records the reader resynchronises on the next OfficeArt container header.
    /// </summary>
    public static IReadOnlyList<EscherShape> ReadShapes(byte[] stm)
    {
        var all = new List<EscherShape>();
        void Flatten(IEnumerable<EscherShape> nodes) { foreach (var n in nodes) { all.Add(n); Flatten(n.Children); } }
        Flatten(ReadTopLevel(stm));
        return all;
    }

    /// <summary>
    /// Top-level drawing elements in stream order (= z-order, bottom first). A group appears as its leader shape
    /// with <see cref="EscherShape.Children"/> holding the group's members.
    /// </summary>
    public static IReadOnlyList<EscherShape> ReadTopLevel(byte[] stm) => ReadTopLevel(stm, out _);

    /// <summary>Containers nested deeper than this are treated as damage (real files nest a handful of levels).</summary>
    public const int MaxNesting = 64;

    /// <param name="damaged">
    /// Damaged drawing records that were skipped: container headers whose length runs past the stream (the reader
    /// resynchronises inside them, so their members may come back ungrouped), and shapes or groups whose records
    /// run past their container (that shape, or the rest of that group, is left out).
    /// </param>
    public static IReadOnlyList<EscherShape> ReadTopLevel(byte[] stm, out int damaged)
    {
        var top = new List<EscherShape>();
        var damage = new Damage();
        var pos = 0;
        while (pos + 8 <= stm.Length)
        {
            if (RecordReader.TryRead(stm, pos, out var r) && r.IsContainer && r.Type is SpContainer or SpgrContainer or DgContainer or DggContainer)
            {
                top.AddRange(Walk(stm, r, 0, 0, damage));
                pos = r.End;
                continue;
            }
            if (LooksLikeDamagedContainer(stm, pos)) damage.Count++;
            pos++;
        }
        damaged = damage.Count;
        return top;
    }

    private sealed class Damage { public int Count; }

    /// <summary>A container's children up to the first one that is damaged (counted); never throws for damage.</summary>
    private static List<Record> ChildrenUpToDamage(byte[] stm, Record parent, Damage damage)
    {
        var list = new List<Record>();
        try { foreach (var c in RecordReader.Children(stm, parent)) list.Add(c); }
        catch (PubFormatException) { damage.Count++; }
        return list;
    }

    /// <summary>A shape, or null (counted as damage) when one of its records runs past the container.</summary>
    private static EscherShape? ParseShapeOrDamage(byte[] stm, Record container, int depth, Damage damage)
    {
        try { return ParseShape(stm, container, depth); }
        catch (PubFormatException) { damage.Count++; return null; }
    }

    // A container header of a drawing type whose length overruns the stream: TryRead refused it only for its length.
    private static bool LooksLikeDamagedContainer(byte[] stm, int pos) =>
        (stm[pos] & 0xF) == 0xF && BinaryPrimitives.ReadUInt16LittleEndian(stm.AsSpan(pos + 2)) is SpContainer or SpgrContainer or DgContainer
        && pos + 8L + BinaryPrimitives.ReadUInt32LittleEndian(stm.AsSpan(pos + 4)) > stm.Length;

    /// <param name="depth">Group nesting of the shapes produced (0 = top level).</param>
    /// <param name="level">Container recursion depth, capped: every nested container counts, groups or not.</param>
    /// <remarks>
    /// Damage inside a shape or group costs that shape (or the rest of that group), counted in
    /// <paramref name="damage"/>; nesting deeper than <see cref="MaxNesting"/> is still a format error for the file.
    /// </remarks>
    private static List<EscherShape> Walk(byte[] stm, Record r, int depth, int level, Damage damage)
    {
        if (level > MaxNesting) throw new PubFormatException($"OfficeArt containers are nested more than {MaxNesting} deep at {r.Offset}.");
        switch (r.Type)
        {
            case SpContainer:
                return ParseShapeOrDamage(stm, r, depth, damage) is { } shape ? [shape] : [];
            case SpgrContainer:
                {
                    // The first SpContainer is the group leader (FSP group flag); everything after it belongs to it.
                    var members = ChildrenUpToDamage(stm, r, damage).Where(c => c.Type is SpContainer or SpgrContainer).ToList();
                    if (members.Count == 0) return [];
                    if (members[0].Type != SpContainer) return members.SelectMany(m => Walk(stm, m, depth + 1, level + 1, damage)).ToList();
                    // A damaged leader loses the grouping, not the members: they are returned at this level.
                    if (ParseShapeOrDamage(stm, members[0], depth, damage) is not { } leader)
                        return members.Skip(1).SelectMany(m => Walk(stm, m, depth, level + 1, damage)).ToList();
                    // The patriarch (the drawing's root group) is not a real group: its members are the top level.
                    if (leader.IsPatriarch) return members.Skip(1).SelectMany(m => Walk(stm, m, depth, level + 1, damage)).ToList();
                    var kids = members.Skip(1).SelectMany(m => Walk(stm, m, depth + 1, level + 1, damage)).ToList();
                    return [leader with { Children = kids }];
                }
            case DgContainer:
                return ChildrenUpToDamage(stm, r, damage).Where(c => c.IsContainer).SelectMany(c => Walk(stm, c, depth, level + 1, damage)).ToList();
            default:
                return [];
        }
    }

    private static EscherShape ParseShape(byte[] stm, Record container, int depth)
    {
        uint spid = 0, flags = 0; ushort spt = 0;
        var props = new Dictionary<ushort, uint>();
        var tertiary = new Dictionary<ushort, uint>();
        var complex = new Dictionary<ushort, byte[]>();
        AnchorEmu? anchor = null, childAnchor = null, groupCs = null;
        uint? seqnum = null;
        var truncated = false;
        foreach (var c in RecordReader.Children(stm, container))
        {
            var body = stm.AsSpan(c.Body, c.Length);
            switch (c.Type)
            {
                case Fsp when c.Length >= 8:
                    spt = c.Instance;
                    spid = BinaryPrimitives.ReadUInt32LittleEndian(body);
                    flags = BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
                    break;
                case Fopt or SecondaryFopt:
                    truncated |= ReadProperties(body, c.Instance, props, complex);
                    break;
                case TertiaryFopt:
                    truncated |= ReadProperties(body, c.Instance, tertiary, new Dictionary<ushort, byte[]>());
                    break;
                case ClientAnchor:
                    anchor = ReadAnchor(body) ?? anchor;
                    break;
                case ClientData:
                    seqnum = ReadTagged(body, 0x6801) ?? seqnum;
                    break;
                case ChildAnchorRec when c.Length >= 16:
                    childAnchor = ReadRect(body);
                    break;
                case Fspgr when c.Length >= 16:
                    groupCs = ReadRect(body);
                    break;
            }
        }
        return new EscherShape
        {
            Offset = container.Offset, ShapeId = spid, ShapeType = spt, Flags = flags, Props = props, Complex = complex, Tertiary = tertiary,
            Anchor = anchor, ChildAnchor = childAnchor, GroupCoordinates = groupCs, Seqnum = seqnum, Depth = depth, Truncated = truncated,
        };
    }

    private const ushort ClientData = 0xF011, ChildAnchorRec = 0xF00F, Fspgr = 0xF009;

    private static AnchorEmu ReadRect(ReadOnlySpan<byte> b) => new(
        BinaryPrimitives.ReadInt32LittleEndian(b), BinaryPrimitives.ReadInt32LittleEndian(b[4..]),
        BinaryPrimitives.ReadInt32LittleEndian(b[8..]), BinaryPrimitives.ReadInt32LittleEndian(b[12..]));

    /// <summary>Publisher client records: u32 size, then (u16 id, u32 value) pairs. Returns the value for <paramref name="id"/>.</summary>
    internal static uint? ReadTagged(ReadOnlySpan<byte> body, ushort id)
    {
        for (var p = 4; p + 6 <= body.Length; p += 6)
            if (BinaryPrimitives.ReadUInt16LittleEndian(body[p..]) == id) return BinaryPrimitives.ReadUInt32LittleEndian(body[(p + 2)..]);
        return null;
    }

    /// <summary>
    /// OfficeArtRGFOPTE: <paramref name="count"/> 6-byte entries (14-bit id, fBid, fComplex, u32), then complex data
    /// in order. Returns true when the record is shorter than its entries or complex data claim (the missing
    /// complex values are read as empty).
    /// </summary>
    internal static bool ReadProperties(ReadOnlySpan<byte> body, int count, Dictionary<ushort, uint> props, Dictionary<ushort, byte[]> complex)
    {
        var truncated = (long)count * 6 > body.Length;
        var dataPos = (int)Math.Min((long)count * 6, body.Length);
        for (var i = 0; i < count && i * 6 + 6 <= body.Length; i++)
        {
            var opid = BinaryPrimitives.ReadUInt16LittleEndian(body[(i * 6)..]);
            var op = BinaryPrimitives.ReadUInt32LittleEndian(body[(i * 6 + 2)..]);
            var id = (ushort)(opid & 0x3FFF);
            props[id] = op;
            if ((opid & 0x8000) != 0)
            {
                var available = body.Length - dataPos;
                if (op > available) truncated = true;
                var len = (int)Math.Min(op, (uint)available);
                complex[id] = body.Slice(dataPos, len).ToArray();
                dataPos += len;
            }
        }
        return truncated;
    }

    /// <summary>
    /// Publisher client anchor: u32 size, then (u16 id, i32 value) pairs; ids 0x2001–0x2004 = left, top, right,
    /// bottom. A zero value is omitted from the record (a shape whose left edge is on the page centre has no
    /// 0x2001), so absent edges are 0.
    /// </summary>
    internal static AnchorEmu? ReadAnchor(ReadOnlySpan<byte> body)
    {
        int l = 0, t = 0, r = 0, b = 0;
        for (var p = 4; p + 6 <= body.Length; p += 6)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(body[p..]);
            var v = BinaryPrimitives.ReadInt32LittleEndian(body[(p + 2)..]);
            switch (id) { case 0x2001: l = v; break; case 0x2002: t = v; break; case 0x2003: r = v; break; case 0x2004: b = v; break; }
        }
        return new AnchorEmu(l, t, r, b);
    }

    /// <summary>
    /// The picture store, indexed like the shapes' 1-based pib (entry 0 = pib 1). An entry is null when the
    /// picture can't be found or decoded; callers must report that, not skip it.
    /// </summary>
    /// <remarks>
    /// Entries that point at the same picture record share one <see cref="Blip"/> (the record is read once). All
    /// pictures together may produce at most twice the size of the two streams plus 64 MB: real files store each
    /// picture once, so only a crafted store (many compressed pictures, or entries aliasing records) reaches it;
    /// pictures past the budget come back null.
    /// </remarks>
    public static IReadOnlyList<Blip?> ReadBlipStore(byte[] stm, byte[] delay)
    {
        var result = new List<Blip?>();
        if (!RecordReader.TryRead(stm, 0, out var dgg) || dgg.Type != DggContainer) return result;
        var budget = 2L * stm.Length + 2L * delay.Length + (64L << 20);
        var read = new Dictionary<(bool Delayed, int Offset), Blip?>();
        Blip? Load(byte[] data, bool delayed, Record r)
        {
            if (read.TryGetValue((delayed, r.Offset), out var known)) return known;
            var blip = ReadBlip(data, r, budget, out var produced);
            budget -= produced;   // charged whether the picture was kept or given up part-way
            read[(delayed, r.Offset)] = blip;
            return blip;
        }
        foreach (var store in RecordReader.Children(stm, dgg).Where(c => c.Type == BStoreContainer))
        {
            foreach (var fbse in RecordReader.Children(stm, store).Where(c => c.Type == Fbse))
            {
                var body = stm.AsSpan(fbse.Body, fbse.Length);
                if (body.Length < 36) { result.Add(null); continue; }
                var size = BinaryPrimitives.ReadInt32LittleEndian(body[20..]);
                var foDelay = BinaryPrimitives.ReadInt32LittleEndian(body[28..]);
                var cbName = body[33];
                var embeddedAt = fbse.Body + 36 + cbName;
                Blip? blip = null;
                if (embeddedAt + 8 <= fbse.End && RecordReader.TryRead(stm, embeddedAt, out var inner)) blip = Load(stm, false, inner);
                else if (size > 0 && RecordReader.TryRead(delay, foDelay, out var delayed)) blip = Load(delay, true, delayed);
                result.Add(blip);
            }
        }
        return result;
    }

    // Instances whose BLIP header carries a second 16-byte UID (MS-ODRAW 2.2.23-2.2.29).
    private static readonly HashSet<ushort> TwoUid = [0x3D5, 0x217, 0x543, 0x46B, 0x6E3, 0x6E1, 0x7A9, 0x6E5];

    /// <param name="budget">Bytes this picture may still produce (copied or inflated); beyond it the picture is null.</param>
    /// <param name="produced">Bytes actually produced, including those of an inflation given up part-way.</param>
    private static Blip? ReadBlip(byte[] data, Record r, long budget, out long produced)
    {
        produced = 0;
        var (ext, metafile) = r.Type switch
        {
            0xF01A => ("emf", true), 0xF01B => ("wmf", true), 0xF01C => ("pict", true),
            0xF01D => ("jpg", false), 0xF02A => ("jpg", false), 0xF01E => ("png", false),
            0xF01F => ("dib", false), 0xF029 => ("tiff", false),
            _ => ((string?)null, false),
        };
        if (ext is null) return null;
        var skip = TwoUid.Contains(r.Instance) ? 32 : 16;
        if (metafile)
        {
            // OfficeArtMetafileHeader (34 bytes): compression at +32 (0 = DEFLATE, 0xFE = none).
            var header = r.Body + skip;
            if (header + 34 > r.End) return null;
            var compressed = data[header + 32] == 0;
            var (from, length) = (header + 34, r.End - header - 34);
            if (!compressed)
            {
                if (length > budget) return null;
                produced = length;
                return new Blip(ext, data.AsSpan(from, length).ToArray());
            }
            var inflated = Inflate(data, from, length, (int)Math.Max(0, Math.Min(MaxInflatedPicture, budget)), out produced);
            return inflated is null ? null : new Blip(ext, inflated);
        }
        var start = r.Body + skip + 1;   // + 1-byte tag
        if (start > r.End || r.End - start > budget) return null;
        produced = r.End - start;
        return new Blip(ext, data.AsSpan(start, r.End - start).ToArray());
    }

    /// <summary>Largest picture a compressed metafile may inflate to; beyond it the data is treated as damaged.</summary>
    public const int MaxInflatedPicture = 64 << 20;

    /// <summary>
    /// zlib-inflates at most <paramref name="max"/> bytes; null when the data is corrupt or inflates past it.
    /// <paramref name="produced"/> is what was inflated either way.
    /// </summary>
    private static byte[]? Inflate(byte[] data, int offset, int count, int max, out long produced)
    {
        produced = 0;
        try
        {
            using var input = new MemoryStream(data, offset, count, writable: false);
            using var z = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int n;
            while ((n = z.Read(buffer)) > 0)
            {
                produced += n;
                if (output.Length + n > max) return null;
                output.Write(buffer, 0, n);
            }
            return output.ToArray();
        }
        catch (InvalidDataException) { return null; }
    }
}
