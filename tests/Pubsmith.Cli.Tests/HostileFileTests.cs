using System.Buffers.Binary;
using OpenMcdf;
using Pubsmith.Cli;
using Pubsmith.PubReader;

namespace Pubsmith.Cli.Tests;

// A .pub of about 100 KB that stays inside the import budgets (248,744 units, no text) but whose document would be
// more than PubImporter.MaxJsonBytes of JSON: the commands refuse it by name, and write no file.
public sealed class HostileFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pubsmith-cli-hostile-").FullName;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Refusal(string file) =>
        $"error: '{file}' is damaged: its document would be more than {PubImporter.MaxJsonBytes >> 20} MiB of JSON, the most Pubsmith writes; no file was written.";

    [Fact]
    public void Import_IsRefusedByName_AndWritesNoFile()
    {
        var pub = HostilePub();
        var outDir = Path.Combine(_dir, "out");

        var code = CliApp.Run(["import", pub, "--out", outDir], _out, _err);

        Assert.Equal(1, code);
        Assert.Contains(Refusal("hostile.pub"), _err.ToString());
        Assert.Empty(Directory.Exists(outDir) ? Directory.GetFiles(outDir, "*", SearchOption.AllDirectories) : []);
    }

    [Fact]
    public void Render_IsRefusedByName_AndWritesNoPdf()
    {
        var pub = HostilePub();
        var pdf = Path.Combine(_dir, "out.pdf");

        var code = CliApp.Run(["render", pub, "--pdf", pdf], _out, _err);

        Assert.Equal(1, code);
        Assert.Contains(Refusal("hostile.pub"), _err.ToString());
        Assert.False(File.Exists(pdf));
        Assert.Equal([pub], Directory.GetFiles(_dir));
    }

    // ---------- the file ----------

    private string HostilePub()
    {
        var path = Path.Combine(_dir, "hostile.pub");
        using var root = RootStorage.Create(path);
        using (var s = root.CreateStream("Contents")) s.Write(Contents(248));
        var escher = root.CreateStorage("Escher");
        using (var s = escher.CreateStream("EscherStm")) s.Write(GroupOfFramedTextBoxes(1000));
        return path;
    }

    private static byte[] U16(ushort v) => BitConverter.GetBytes(v);
    private static byte[] U32(uint v) => BitConverter.GetBytes(v);

    private static byte[] Rec(int ver, int inst, ushort type, byte[] body) =>
        [.. U16((ushort)(ver | (inst << 4))), .. U16(type), .. U32((uint)body.Length), .. body];

    private static byte[] Anchor(int x, int y, int w, int h) =>
        Rec(0, 0, 0xF010, [.. U32(28), .. U16(0x2001), .. U32((uint)x), .. U16(0x2002), .. U32((uint)y), .. U16(0x2003), .. U32((uint)w), .. U16(0x2004), .. U32((uint)h)]);

    /// <summary>EscherStm: a group (seqnum 6) of text boxes, each with a fill, a line, a shadow and a rotation.</summary>
    private static byte[] GroupOfFramedTextBoxes(int members)
    {
        // fill on, line on, shadow on, shadow colour, rotation 30.3 degrees
        (ushort Id, uint Value)[] framed = [(0x01BF, 0x100010), (0x01FF, 0x80008), (0x023F, 0x20002), (0x0201, 0x112233), (0x0004, (uint)(30.3 * 65536))];
        var leader = Rec(0xF, 0, 0xF004, [.. Rec(2, 0, 0xF00A, [.. U32(1), .. U32(0x1)]), .. Anchor(0, 0, 1_270_000, 1_270_000), .. Rec(0, 0, 0xF011, [.. U32(10), .. U16(0x6801), .. U32(6)])]);
        var member = Rec(0xF, 0, 0xF004, [.. Rec(2, 202, 0xF00A, [.. U32(2), .. U32(0)]),
            .. Rec(3, framed.Length, 0xF00B, framed.SelectMany(p => (byte[])[.. U16(p.Id), .. U32(p.Value)]).ToArray()), .. Anchor(12_345, 67_891, 1_234_567, 987_653)]);
        return Rec(0xF, 0, 0xF002, Rec(0xF, 0, 0xF003, [.. leader, .. Enumerable.Repeat(member, members).SelectMany(m => m)]));
    }

    /// <summary>Contents: document (seqnum 0), a page (1) listing shape 6 and placed <paramref name="times"/> times, internal pages 2-5, shape 6's record.</summary>
    private static byte[] Contents(int times)
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
        BinaryPrimitives.WriteUInt32LittleEndian(c0.AsSpan(0x1A), (uint)trailer);
        return c0;
    }
}
