using Xunit.Abstractions;
using Pubsmith.Core;
using SkiaSharp;
using static Pubsmith.Rendering.Tests.JpegBytes;

namespace Pubsmith.Rendering.Tests;

[CollectionDefinition(nameof(JpegPassThroughSoundnessTests), DisableParallelization = true)]
public sealed class JpegSoundnessCollection;

// WI-006 AC4: whatever the walk passes through, SkPDF passes through too, over seeded variants of small real JPEGs.
// The oracle is SkPDF itself: the file's whole bytes appear in the PDF, and Skia's resource cache (purged first) does
// not grow while it is drawn. The class runs alone, so no other render touches the resource cache meanwhile.
[Collection(nameof(JpegPassThroughSoundnessTests))]
public sealed class JpegPassThroughSoundnessTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pubsmith-soundness-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly byte[][] Markers =
    [
        Seg(0xFE, 0x61, 0x62), Seg(0xE5, 1, 2, 3), Seg(0xED, [.. "Photoshop 3.0\0"u8]), Seg(0xDD, 0, 8), Exif(1), Exif(6),
        JpegBytes.Jfif, Adobe(0), Adobe(1), Adobe(2), Seg(0xCC, 0, 0x11), Seg(0xDC, 0, 0x20), [0xFF, 0x01], [0xFF, 0xD3], [0xFF, 0x00], [0xFF],
    ];

    /// <summary>One variant: a few edits of markers, bytes, the frame header, and JFIF and Adobe markers and their lengths.</summary>
    internal static byte[] Variant(byte[] b, Random rng)
    {
        for (var edits = rng.Next(1, 4); edits > 0; edits--)
        {
            var segs = Segments(b);
            if (segs.Count == 0) return b;
            var at = rng.Next(2) == 0 ? segs[rng.Next(segs.Count)].At : segs[^1].At + 2 + segs[^1].Length;   // a boundary, or the SOS
            var pick = segs[rng.Next(segs.Count)];
            var pickBytes = b[pick.At..(pick.At + 2 + pick.Length)];
            byte[] Insert(byte[] x) => [.. b[..at], .. x, .. b[at..]];
            byte[] Be(int v) => [(byte)(v >> 8), (byte)v];
            var target = rng.Next(2) == 0 ? Adobe(0) : JpegBytes.Jfif;
            switch (rng.Next(12))
            {
                case 0: b = Insert(Markers[rng.Next(Markers.Length)]); break;
                case 1: b = [.. b[..pick.At], .. b[(pick.At + 2 + pick.Length)..]]; break;          // remove a segment
                case 2: b = Insert(pickBytes); break;                                                // duplicate one
                case 3: b = [.. b[..pick.At], .. b[(pick.At + 2 + pick.Length)..]]; b = InsertAt(b, pickBytes, rng); break;   // move one
                case 4: b = Insert([0xFF, 0x00, .. Be(target.Length + 2), .. target]); break;        // a stuffed byte whose "length" skips the marker after it
                case 5: b = Insert([0xFF, .. target]); break;                                        // a fill byte before a marker
                case 6:
                {
                    var c = (byte[])b.Clone();
                    var sof = segs.FirstOrDefault(s => s.Code is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC);
                    if (sof.At > 0) c[sof.At + 1] = (byte)(0xC0 + new[] { 0, 1, 2, 3, 5, 6, 7, 9, 10, 11, 13, 14, 15 }[rng.Next(13)]);
                    b = c;
                    break;
                }
                case 7:
                {
                    var c = (byte[])b.Clone();
                    var sof = segs.FirstOrDefault(s => s.Code is 0xC0 or 0xC1 or 0xC2);
                    if (sof.At > 0) c[sof.At + 4] = new byte[] { 8, 12, 16 }[rng.Next(3)];
                    b = c;
                    break;
                }
                case 8:
                {
                    var c = (byte[])b.Clone();
                    var sof = segs.FirstOrDefault(s => s.Code is 0xC0 or 0xC1 or 0xC2);
                    if (sof.At > 0) c[sof.At + 9] = (byte)rng.Next(1, 5);   // component count, the length left as it was
                    b = c;
                    break;
                }
                case 9:
                {
                    var c = (byte[])b.Clone();
                    var sof = segs.FirstOrDefault(s => s.Code is 0xC0 or 0xC1 or 0xC2);
                    if (sof.At > 0 && c[sof.At + 9] == 3)
                    {
                        byte[][] choices = [[1, 2, 3], [0x52, 0x47, 0x42], [0x42, 0x47, 0x52], [0, 1, 2], [0x52, 0x47, 0x47]];
                        var ids = choices[rng.Next(choices.Length)];
                        for (var k = 0; k < 3; k++) c[sof.At + 10 + 3 * k] = ids[k];
                    }
                    b = c;
                    break;
                }
                case 10:
                {
                    // A JFIF or Adobe marker cut short or padded.
                    var m = target[4..].ToList();
                    var cut = rng.Next(-3, 3);
                    if (cut < 0) m.RemoveRange(m.Count + cut, -cut); else m.AddRange(new byte[cut]);
                    b = Insert(Seg(target[1], [.. m]));
                    break;
                }
                default: b = Insert(target); break;
            }
        }
        return b;
    }

    private static byte[] InsertAt(byte[] b, byte[] x, Random rng)
    {
        var segs = Segments(b);
        var at = segs.Count == 0 ? 2 : rng.Next(2) == 0 ? segs[rng.Next(segs.Count)].At : segs[^1].At + 2 + segs[^1].Length;
        return [.. b[..at], .. x, .. b[at..]];
    }

    [Fact]
    public void GeneratedVariants_AreNeverPassedWhenSkPdfDecodes()
    {
        byte[][] bases =
        [
            Encode(11),                                                        // a small JFIF JPEG
            InsertBefore(Without(Encode(12), 0xE0), 0xDB, Exif(1)),            // camera-style: EXIF, no JFIF
            Encode(13, gray: true),                                            // grayscale
            Fixture("progressive.jpg"),                                        // progressive
            InsertBefore(Without(Encode(14), 0xE0), 0xDB, Adobe(1)),           // Photoshop-style: Adobe 1
        ];
        var rng = new Random(20261002);
        int passed = 0, decoded = 0, unreadable = 0;
        var unsound = new List<string>();
        for (var n = 0; n < 5_000; n++)
        {
            var file = Variant(bases[n % bases.Length], rng);
            File.WriteAllBytes(Path.Combine(_dir, "v.jpg"), file);
            bool walkPasses;
            using (var codec = SKCodec.Create(new MemoryStream(file)))
            {
                if (codec is null) { unreadable++; continue; }
                walkPasses = codec.EncodedFormat == SKEncodedImageFormat.Jpeg && codec.EncodedOrigin == SKEncodedOrigin.TopLeft && JpegWalk.IsPassedThroughShape(file);
            }
            if (!walkPasses) { decoded++; continue; }
            passed++;

            SKGraphics.PurgeResourceCache();
            var before = SKGraphics.GetResourceCacheTotalBytesUsed();
            using var ctx = new RenderContext(_dir);
            using var s = new MemoryStream();
            Exporter.ToPdf(new PubsmithDocument { Pages = [new Page(100, 100) { Elements = [new ImageElement { Bounds = new Box(10, 10, 48, 32), Source = "v.jpg" }] }] }, ctx, s);
            var grew = SKGraphics.GetResourceCacheTotalBytesUsed() > before;
            if (s.ToArray().AsSpan().IndexOf(file) < 0 || grew)
                unsound.Add($"variant {n}{(grew ? " (the resource cache grew)" : "")}: {Convert.ToHexString(file.AsSpan(0, Math.Min(file.Length, 160)))}");
        }
        output.WriteLine($"passed {passed}, decoded {decoded}, unreadable {unreadable}, unsound {unsound.Count}");
        Assert.True(unsound.Count == 0, $"{unsound.Count} unsound of {passed} passed:\n{string.Join("\n", unsound.Take(5))}");
        // The generator reaches both verdicts often enough for the check above to mean something.
        Assert.True(passed >= 1_000 && decoded >= 1_000, $"passed {passed}, decoded {decoded}, unreadable {unreadable}");
    }
}
