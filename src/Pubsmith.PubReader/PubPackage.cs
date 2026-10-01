using OpenMcdf;

namespace Pubsmith.PubReader;

/// <summary>Thrown when a file is not a readable Publisher publication. The message names the file.</summary>
public sealed class PubFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The streams of a Publisher 2003–2021 publication (an OLE compound file, MS-CFB), read fully into memory.
/// Nothing stays open after <see cref="Open"/> returns.
/// </summary>
public sealed class PubPackage
{
    private PubPackage(string name, byte[] contents, byte[] escherStm, byte[] escherDelayStm, byte[] quill)
    {
        Name = name; Contents = contents; EscherStm = escherStm; EscherDelayStm = escherDelayStm; Quill = quill;
    }

    public string Name { get; }

    /// <summary>"Contents": Publisher's own chunk structure (pages, shapes, document). Starts E8 AC xx 00.</summary>
    public byte[] Contents { get; }

    /// <summary>"Escher/EscherStm": OfficeArt drawing records (MS-ODRAW): shapes, properties, picture store.</summary>
    public byte[] EscherStm { get; }

    /// <summary>"Escher/EscherDelayStm": picture data referenced from the picture store. Empty if absent.</summary>
    public byte[] EscherDelayStm { get; }

    /// <summary>"Quill/QuillSub/CONTENTS": text stories and their formatting.</summary>
    public byte[] Quill { get; }

    /// <summary>Largest stream read into memory. Real publications with many large pictures stay well below it.</summary>
    public const long MaxStreamBytes = 1L << 30;

    /// <summary>A package over streams already in memory (no compound file, no header check): for tests and tools.</summary>
    internal static PubPackage FromStreams(string name, byte[] contents, byte[] escherStm, byte[] escherDelayStm, byte[] quill) =>
        new(name, contents, escherStm, escherDelayStm, quill);

    public static PubPackage Open(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        if (!File.Exists(path)) throw new PubFormatException($"'{name}' was not found.");
        try
        {
            // A stream cannot hold more than the file it is in: a directory entry claiming more is damage, and is
            // refused before anything that size is allocated.
            var limit = Math.Min(MaxStreamBytes, new FileInfo(path).Length);
            using var root = RootStorage.OpenRead(path);
            var contents = ReadStream(root, "Contents", name, limit) ?? throw new PubFormatException($"'{name}' has no Contents stream; it is not a Publisher file.");
            if (contents.Length < 4 || contents[0] != 0xE8 || contents[1] != 0xAC || contents[3] != 0x00)
                throw new PubFormatException($"'{name}' has an unrecognised Contents header; it is not a Publisher 2003–2021 file.");

            byte[]? escher = null, delay = null, quill = null;
            if (root.TryOpenStorage("Escher", out var esc))
            {
                escher = ReadStream(esc, "EscherStm", name, limit);
                delay = ReadStream(esc, "EscherDelayStm", name, limit);
            }
            if (root.TryOpenStorage("Quill", out var q) && q.TryOpenStorage("QuillSub", out var qs)) quill = ReadStream(qs, "CONTENTS", name, limit);
            if (escher is null) throw new PubFormatException($"'{name}' has no Escher drawing stream (older Publisher versions are not supported yet).");
            return new PubPackage(name, contents, escher, delay ?? [], quill ?? []);
        }
        catch (PubFormatException) { throw; }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new PubFormatException($"'{name}' is not a readable Publisher file: {ex.Message}", ex);
        }
    }

    internal static byte[]? ReadStream(Storage storage, string streamName, string fileName, long maxBytes = MaxStreamBytes)
    {
        if (!storage.TryOpenStream(streamName, out var s)) return null;
        using (s)
        {
            if (s.Length > maxBytes)
                throw new PubFormatException($"'{fileName}' declares a {streamName} stream of {s.Length:N0} bytes, more than the file holds or Pubsmith reads ({maxBytes:N0}).");
            var bytes = new byte[s.Length];
            s.ReadExactly(bytes);
            return bytes;
        }
    }
}
