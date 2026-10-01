using System.Buffers.Binary;

namespace Pubsmith.PubReader.OfficeArt;

/// <summary>An OfficeArt record header (MS-ODRAW 2.2.1): 4-bit version, 12-bit instance, 16-bit type, 32-bit length.</summary>
public readonly record struct Record(int Offset, ushort Version, ushort Instance, ushort Type, int Length)
{
    public int Body => Offset + 8;
    public int End => Body + Length;
    public bool IsContainer => Version == 0xF;
}

public static class RecordReader
{
    /// <summary>Reads the record header at <paramref name="offset"/>; throws if it runs past the data.</summary>
    public static Record Read(byte[] data, int offset)
    {
        if (!TryRead(data, offset, out var r, requireOfficeArtType: false))
            throw new PubFormatException($"OfficeArt record at {offset} runs past the end of its stream.");
        return r;
    }

    /// <summary>
    /// Reads a header if one fits at <paramref name="offset"/>. With <paramref name="requireOfficeArtType"/>, only
    /// types 0xF000–0xF1FF count, which is how the reader resynchronises across Publisher's framing bytes.
    /// </summary>
    public static bool TryRead(byte[] data, int offset, out Record record, bool requireOfficeArtType = true)
    {
        record = default;
        if (offset < 0 || (long)offset + 8 > data.Length) return false;
        var verInst = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
        var type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2));
        var length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
        if (length > int.MaxValue || offset + 8 + (long)length > data.Length) return false;
        if (requireOfficeArtType && type is < 0xF000 or > 0xF1FF) return false;
        record = new Record(offset, (ushort)(verInst & 0xF), (ushort)(verInst >> 4), type, (int)length);
        return true;
    }

    /// <summary>The records directly inside a container.</summary>
    public static IEnumerable<Record> Children(byte[] data, Record parent)
    {
        var pos = parent.Body;
        while (pos + 8 <= parent.End)
        {
            var r = Read(data, pos);
            if (r.End > parent.End) throw new PubFormatException($"OfficeArt record at {pos} overruns its container at {parent.Offset}.");
            yield return r;
            pos = r.End;
        }
    }
}
