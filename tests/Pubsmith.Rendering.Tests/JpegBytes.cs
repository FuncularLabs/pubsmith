using SkiaSharp;

namespace Pubsmith.Rendering.Tests;

/// <summary>Small real JPEGs and byte-level edits of their markers (WI-006's JPEG tests).</summary>
internal static class JpegBytes
{
    public static byte[] Seg(byte code, params byte[] data) => JpegWalkTests.Seg(code, data);

    /// <summary>Random pixels encoded by SkiaSharp: JFIF, baseline, 3 components (IDs 1, 2, 3), 4:2:0 unless asked.</summary>
    public static byte[] Encode(int seed, bool gray = false, bool full444 = false, SKColorSpace? colorSpace = null, int w = 48, int h = 32)
    {
        var rng = new Random(seed);
        var info = new SKImageInfo(w, h, gray ? SKColorType.Gray8 : SKColorType.Rgba8888, gray ? SKAlphaType.Opaque : SKAlphaType.Premul, colorSpace);
        using var bmp = new SKBitmap(info);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                bmp.SetPixel(x, y, gray ? new SKColor((byte)rng.Next(256), 0, 0) : new SKColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256)));
        using var pixmap = bmp.PeekPixels();
        using var data = pixmap.Encode(new SKJpegEncoderOptions(85, full444 ? SKJpegEncoderDownsample.Downsample444 : SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore))
            ?? throw new InvalidOperationException("the JPEG encoder returned nothing");
        return data.ToArray();
    }

    public static byte[] Fixture(string name) => File.ReadAllBytes(Fixtures.Asset(name));

    /// <summary>The markers from the SOI up to the first SOS: (code, offset of the 0xFF, length field).</summary>
    public static List<(byte Code, int At, int Length)> Segments(byte[] b)
    {
        var list = new List<(byte, int, int)>();
        var i = 2;
        while (i + 4 <= b.Length && b[i] == 0xFF && b[i + 1] != 0xDA)
        {
            var len = (b[i + 2] << 8) | b[i + 3];
            if (len < 2 || i + 2 + len > b.Length) break;   // a segment that does not fit ends the list
            list.Add((b[i + 1], i, len));
            i += 2 + len;
        }
        return list;
    }

    private static int SosAt(byte[] b) => Segments(b) is { Count: > 0 } s ? s[^1].At + 2 + s[^1].Length : 2;

    public static byte[] Without(byte[] b, byte code)
    {
        var s = Segments(b).Where(x => x.Code == code).ToList();
        var result = b.ToList();
        foreach (var seg in s.OrderByDescending(x => x.At)) result.RemoveRange(seg.At, 2 + seg.Length);
        return [.. result];
    }

    public static byte[] InsertBefore(byte[] b, byte code, byte[] bytes)
    {
        var at = code == 0xDA ? SosAt(b) : Segments(b).First(x => x.Code == code).At;
        return [.. b[..at], .. bytes, .. b[at..]];
    }

    public static byte[] InsertAfter(byte[] b, byte code, byte[] bytes)
    {
        var seg = Segments(b).First(x => x.Code == code);
        var at = seg.At + 2 + seg.Length;
        return [.. b[..at], .. bytes, .. b[at..]];
    }

    /// <summary>The first frame header (SOF0, SOF1 or SOF2).</summary>
    public static (byte Code, int At, int Length) Sof(byte[] b) => Segments(b).First(x => x.Code is 0xC0 or 0xC1 or 0xC2);

    /// <summary>Renames the components in the frame header and in the first scan's header, so the file still decodes.</summary>
    public static byte[] SetIds(byte[] b, params byte[] ids)
    {
        var c = (byte[])b.Clone();
        var sof = Sof(c);
        var old = new byte[ids.Length];
        for (var k = 0; k < ids.Length; k++)
        {
            old[k] = c[sof.At + 10 + 3 * k];
            c[sof.At + 10 + 3 * k] = ids[k];
        }
        var sos = SosAt(c);
        for (var k = 0; k < c[sos + 4]; k++)
        {
            var at = sos + 5 + 2 * k;
            var which = Array.IndexOf(old, c[at]);
            if (which >= 0) c[at] = ids[which];
        }
        return c;
    }

    public static byte[] SetSofCode(byte[] b, byte code)
    {
        var c = (byte[])b.Clone();
        c[Sof(c).At + 1] = code;
        return c;
    }

    public static byte[] SetPrecision(byte[] b, byte precision)
    {
        var c = (byte[])b.Clone();
        c[Sof(c).At + 4] = precision;
        return c;
    }

    public static readonly byte[] Jfif = JpegWalkTests.Jfif;

    public static byte[] Adobe(byte transform, int padding = 0, ushort version = 100) =>
        Seg(0xEE, [.. "Adobe"u8, (byte)(version >> 8), (byte)version, 0, 0, 0, 0, transform, .. new byte[padding]]);

    /// <summary>An APP1 Exif marker whose one IFD entry is the orientation (1 = top-left).</summary>
    public static byte[] Exif(ushort orientation) =>
        Seg(0xE1, [.. "Exif\0\0"u8, (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0, 0, 0, 0, 0]);
}
