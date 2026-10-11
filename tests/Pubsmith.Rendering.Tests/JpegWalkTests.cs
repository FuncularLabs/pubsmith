namespace Pubsmith.Rendering.Tests;

// WI-006 AC4: the walk that decides whether a JPEG is in the one shape a PDF passes through, on crafted bytes,
// including bytes no decoder would read (truncated and overlong segments, two SOFs, no SOF).
public sealed class JpegWalkTests
{
    internal static byte[] Seg(byte code, params byte[] data)
    {
        var len = data.Length + 2;
        return [0xFF, code, (byte)(len >> 8), (byte)len, .. data];
    }

    internal static byte[] Sof(byte code = 0xC0, byte precision = 8, params byte[] ids)
    {
        if (ids.Length == 0) ids = [1, 2, 3];
        var data = new List<byte> { precision, 0, 16, 0, 16, (byte)ids.Length };
        foreach (var id in ids) data.AddRange([id, 0x11, 0]);
        return Seg(code, [.. data]);
    }

    internal static readonly byte[] Soi = [0xFF, 0xD8];
    internal static readonly byte[] Dqt = Seg(0xDB, [0, .. Enumerable.Repeat((byte)1, 64)]);
    internal static readonly byte[] Dht = Seg(0xC4, [0, 0, 1, .. new byte[15], 0]);
    internal static readonly byte[] Sos = [.. Seg(0xDA, 3, 1, 0, 2, 0x11, 3, 0x11, 0, 63, 0), 0x12, 0x34, 0xFF, 0xD9];
    internal static readonly byte[] Jfif = Seg(0xE0, [.. "JFIF"u8, 0, 1, 1, 0, 0, 1, 0, 1, 0, 0]);   // 14 data bytes
    internal static byte[] Adobe(byte transform) => Seg(0xEE, [.. "Adobe"u8, 0, 100, 0, 0, 0, 0, transform]);   // 12 data bytes

    internal static byte[] Jpeg(params byte[][] parts) => [.. Soi, .. parts.SelectMany(p => p)];

    public static TheoryData<string, byte[], bool> Rows() => new()
    {
        { "baseline, IDs 1 2 3", Jpeg(Dqt, Sof(), Dht, Sos), true },
        { "extended (SOF1)", Jpeg(Dqt, Sof(0xC1), Dht, Sos), true },
        { "progressive (SOF2)", Jpeg(Dqt, Sof(0xC2), Dht, Sos), true },
        { "grayscale", Jpeg(Dqt, Sof(ids: [1]), Dht, Sos), true },
        { "restart interval, comment, APP15", Jpeg(Seg(0xDD, 0, 4), Seg(0xFE, 0x61), Seg(0xEF), Dqt, Sof(), Dht, Sos), true },
        { "not a JPEG", [0x89, 0x50, 0x4E, 0x47, .. Jpeg(Dqt, Sof(), Dht, Sos)[2..]], false },
        { "SOI only", Soi, false },
        { "truncated inside a segment", Jpeg(Dqt, Sof(), Dht, Sos)[..30], false },
        { "truncated inside a length", Jpeg(Dqt)[..^(Dqt.Length - 3)], false },
        { "a segment longer than the file", Jpeg(Dqt, Seg(0xC0, 8, 0, 16, 0, 16, 3, 1, 0x11, 0, 2, 0x11, 0, 3, 0x11, 0)[..12]), false },
        { "an overlong segment after the SOF", [.. Jpeg(Dqt, Sof()), 0xFF, 0xC4, 0x00, 0x40, 1, 2, 3], false },
        { "a length below 2", Jpeg(Dqt, [0xFF, 0xE1, 0, 1], Sof(), Dht, Sos), false },
        { "no SOS", Jpeg(Dqt, Sof(), Dht), false },
        { "no SOF", Jpeg(Dqt, Dht, Sos), false },
        { "two SOFs", Jpeg(Dqt, Sof(), Sof(), Dht, Sos), false },
        { "SOF0 then SOF2", Jpeg(Dqt, Sof(), Sof(0xC2), Dht, Sos), false },
        { "a SOF too short for its components", Jpeg(Dqt, Seg(0xC0, 8, 0, 16, 0, 16, 3, 1, 0x11, 0), Dht, Sos), false },
        { "two components", Jpeg(Dqt, Sof(ids: [1, 2]), Dht, Sos), false },
        { "four components", Jpeg(Dqt, Sof(ids: [1, 2, 3, 4]), Dht, Sos), false },
        { "12-bit", Jpeg(Dqt, Sof(0xC1, 12), Dht, Sos), false },
        { "lossless (SOF3)", Jpeg(Dqt, Sof(0xC3), Dht, Sos), false },
        { "arithmetic (SOF9)", Jpeg(Dqt, Sof(0xC9), Dht, Sos), false },
        { "a stuffed 00 where a marker is expected", Jpeg(Dqt, [0xFF, 0x00], Sof(), Dht, Sos), false },
        { "a fill byte", Jpeg(Dqt, [0xFF], Sof(), Dht, Sos), false },
        { "TEM", Jpeg(Dqt, [0xFF, 0x01], Sof(), Dht, Sos), false },
        { "RST0", Jpeg(Dqt, [0xFF, 0xD0], Sof(), Dht, Sos), false },
        { "DAC", Jpeg(Dqt, Sof(), Seg(0xCC, 0, 0x11), Dht, Sos), false },
        { "DNL", Jpeg(Dqt, Sof(), Seg(0xDC, 0, 16), Dht, Sos), false },
        { "JFIF, IDs R G B", Jpeg(Jfif, Dqt, Sof(ids: [0x52, 0x47, 0x42]), Dht, Sos), true },
        { "Adobe 0", Jpeg(Adobe(0), Dqt, Sof(), Dht, Sos), false },
        { "Adobe 1", Jpeg(Adobe(1), Dqt, Sof(), Dht, Sos), true },
        { "JFIF and Adobe 0", Jpeg(Jfif, Adobe(0), Dqt, Sof(), Dht, Sos), true },
        { "Adobe 0 after the SOF", Jpeg(Dqt, Sof(), Adobe(0), Dht, Sos), false },
        { "Adobe 1 then Adobe 0", Jpeg(Adobe(1), Adobe(0), Dqt, Sof(), Dht, Sos), false },
        { "Adobe 0 then Adobe 1", Jpeg(Adobe(0), Adobe(1), Dqt, Sof(), Dht, Sos), true },
        { "IDs R G B", Jpeg(Dqt, Sof(ids: [0x52, 0x47, 0x42]), Dht, Sos), false },
        { "IDs B G R", Jpeg(Dqt, Sof(ids: [0x42, 0x47, 0x52]), Dht, Sos), true },
        { "Adobe 1, IDs R G B", Jpeg(Adobe(1), Dqt, Sof(ids: [0x52, 0x47, 0x42]), Dht, Sos), true },
        { "an APP0 JFIFX, IDs R G B", Jpeg(Seg(0xE0, [.. "JFIFX"u8, 1, 1, 0, 0, 1, 0, 1, 0, 0]), Dqt, Sof(ids: [0x52, 0x47, 0x42]), Dht, Sos), false },
        { "a JFIF of 13 data bytes, IDs R G B", Jpeg(Seg(0xE0, [.. "JFIF"u8, 0, 1, 1, 0, 0, 1, 0, 1, 0]), Dqt, Sof(ids: [0x52, 0x47, 0x42]), Dht, Sos), false },
        { "an Adobe of 11 data bytes, IDs R G B", Jpeg(Seg(0xEE, [.. "Adobe"u8, 0, 100, 0, 0, 0, 0]), Dqt, Sof(ids: [0x52, 0x47, 0x42]), Dht, Sos), false },
        { "an Adobe of 11 data bytes, IDs 1 2 3", Jpeg(Seg(0xEE, [.. "Adobe"u8, 0, 100, 0, 0, 0, 0]), Dqt, Sof(), Dht, Sos), true },
        { "a 1-component Adobe 0", Jpeg(Adobe(0), Dqt, Sof(ids: [1]), Dht, Sos), true },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void TheWalk_PassesOnlyTheOneShape(string row, byte[] file, bool passed)
    {
        Assert.True(JpegWalk.IsPassedThroughShape(file) == passed, row);
    }
}
