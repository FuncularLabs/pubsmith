using System.Buffers.Binary;

namespace Pubsmith.PubReader.Contents;

/// <summary>
/// One Publisher "block" (see docs/format/pub-format-notes.md §1.2): u8 id, u8 type; the type decides the
/// payload size. Variable-length payloads begin with a u32 length that counts itself.
/// </summary>
public readonly record struct Block(int Offset, byte Id, byte Type, int DataOffset, int DataLength, uint Value)
{
    /// <summary>Total bytes the block occupies, header included.</summary>
    public int Size => 2 + DataLength;
    public int End => Offset + Size;
    public bool IsContainer => Type is 0x80 or 0x82 or 0x88 or 0x8A or 0x90 or 0x98 or 0xA0;
    public bool IsString => Type == 0xC0;
}

public static class Blocks
{
    /// <summary>Reads one block. Unknown type bytes throw: guessing a size would silently desynchronise the stream.</summary>
    public static Block ReadAt(byte[] data, int offset)
    {
        if (offset < 0 || (long)offset + 2 > data.Length) throw new PubFormatException($"Block at {offset} runs past the end of its stream.");
        var id = data[offset];
        var type = data[offset + 1];
        var dataOffset = offset + 2;
        int len;
        uint value = 0;
        switch (type)
        {
            // 0x00 observed as a flag in Publisher 365 files ("39 00" followed directly by the next block);
            // 0x02 in 2012 Quill records ("03 02 0c 22 ..." = italic flag, then the size block).
            case 0x78 or 0x00 or 0x02 or 0x05 or 0x08 or 0x0A: len = 0; break;
            case 0x07 or 0x10 or 0x12 or 0x18 or 0x1A: len = 2; break;
            case 0x20 or 0x22 or 0x58 or 0x68 or 0x70 or 0xB8: len = 4; break;
            case 0x28: len = 8; break;
            case 0x38: len = 16; break;
            case 0x48: len = 24; break;
            case 0x80 or 0x82 or 0x88 or 0x8A or 0x90 or 0x98 or 0xA0 or 0xC0:
                if ((long)dataOffset + 4 > data.Length) throw new PubFormatException($"Block at {offset} has a truncated length.");
                var raw = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(dataOffset));
                if (raw < 4 || raw > int.MaxValue) throw new PubFormatException($"Block at {offset} has an impossible length {raw}.");
                len = (int)raw;
                break;
            default:
                throw new PubFormatException($"Unknown block type 0x{type:X2} (id 0x{id:X2}) at {offset}.");
        }
        if ((long)dataOffset + len > data.Length) throw new PubFormatException($"Block at {offset} (type 0x{type:X2}) runs past the end of its stream.");
        if (len == 2) value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(dataOffset));
        else if (len == 4 && !(type is 0x80 or 0x82 or 0x88 or 0x8A or 0x90 or 0x98 or 0xA0 or 0xC0)) value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(dataOffset));
        return new Block(offset, id, type, dataOffset, len, value);
    }

    /// <summary>The blocks from <paramref name="start"/> up to <paramref name="end"/>.</summary>
    public static IEnumerable<Block> Sequence(byte[] data, int start, int end)
    {
        var pos = start;
        while (pos + 2 <= end)
        {
            var b = ReadAt(data, pos);
            if (b.End > end) throw new PubFormatException($"Block at {pos} overruns its container (ends {b.End}, container ends {end}).");
            yield return b;
            pos = b.End;
        }
    }

    /// <summary>Children of a container block (payload after its u32 length).</summary>
    public static IEnumerable<Block> Children(byte[] data, Block container) =>
        container.IsContainer ? Sequence(data, container.DataOffset + 4, container.DataOffset + container.DataLength) : [];
}
