using System.Text;
using Pubsmith.Core;
using SkiaSharp;

namespace Pubsmith.Rendering.Tests;

// WI-006 AC6 and AC7: a PDF is written through a counting stream, so a stream that cannot seek works; its bytes are
// checked at each checkpoint, after each page's end and before its close; past MaxPdfBytes, or on any stop or failure,
// the document is aborted, never closed.
public sealed class PdfLimitTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pubsmith-pdflimit-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly RenderLimits Open = RenderLimits.Default with
    {
        MaxPages = int.MaxValue, MaxPngPixels = long.MaxValue, MaxPdfTypefaces = int.MaxValue, MaxFontNames = int.MaxValue,
        MaxTextBoxCharacters = int.MaxValue, MaxTextBoxRuns = int.MaxValue, MaxWordArtGlyphs = int.MaxValue,
        MaxDrawTime = TimeSpan.MaxValue, MaxPdfBytes = long.MaxValue, MaxPngBytes = long.MaxValue,
    };

    private sealed class Unseekable : Stream
    {
        public MemoryStream Inner { get; } = new();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Inner.Write(buffer, offset, count);
    }

    private static bool Has(byte[] pdf, string text) => pdf.AsSpan().IndexOf(Encoding.ASCII.GetBytes(text)) >= 0;

    // A distinct picture per element: SkPDF writes each picture as it is drawn.
    private Element[] Pictures(int n)
    {
        var rng = new Random(5);
        return Enumerable.Range(0, n).Select(i =>
        {
            using var bmp = new SKBitmap(40, 40);
            for (var y = 0; y < 40; y++) for (var x = 0; x < 40; x++) bmp.SetPixel(x, y, new SKColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256)));
            using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(_dir, $"p{i}.png"), data.ToArray());
            return (Element)new ImageElement { Bounds = new Box(10 + i % 10 * 40, 10 + i / 10 * 40, 40, 40), Source = $"p{i}.png" };
        }).ToArray();
    }

    // Vector content that SkPDF keeps until the page ends.
    private static Element[] Shapes(int n) =>
        Enumerable.Range(0, n).Select(i => (Element)new ShapeElement { Kind = ShapeKind.Ellipse, Bounds = new Box(i % 50 * 8, i / 50 * 8, 7, 7), Fill = new Rgba((byte)i, 0, 0) }).ToArray();

    private static TextElement Text(string s) => new() { Bounds = new Box(0, 0, 400, 100), Paragraphs = [new Paragraph { Runs = [new TextRun(s, new TextStyle { Family = "Arial" })] }] };

    [Fact]
    public void TheCountingStream_CountsWhatItForwards_AndOnlyWrites()
    {
        var inner = new MemoryStream();
        var s = new CountingStream(inner);
        s.Write([1, 2, 3], 0, 3);
        s.Write(new byte[] { 4, 5 }.AsSpan());
        s.WriteByte(6);
        s.Flush();
        Assert.Equal(6, s.Count);
        Assert.Equal(6, s.Position);   // its own count: the caller's stream need not seek
        Assert.Equal(6, s.Length);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, inner.ToArray());
        Assert.True(s.CanWrite);
        Assert.False(s.CanRead);
        Assert.False(s.CanSeek);
        Assert.Throws<NotSupportedException>(() => s.Read(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => s.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => s.SetLength(0));
        Assert.Throws<NotSupportedException>(() => s.Position = 0);
    }

    [Fact]
    public void ToPdf_ToAStreamThatCannotSeek_Completes()
    {
        var s = new Unseekable();
        using var ctx = new RenderContext(_dir, Open);
        Exporter.ToPdf(new PubsmithDocument { Pages = [new Page(200, 200) { Elements = [Text("Hello"), .. Pictures(2)] }] }, ctx, s);
        var pdf = s.Inner.ToArray();
        Assert.True(Has(pdf, "%PDF") && Has(pdf, "%%EOF"));
    }

    [Fact]
    public void APdfOverItsBytes_BetweenElements_IsAbortedWithinTheBound()
    {
        var page = new Page(400, 100) { Elements = Pictures(20) };
        long full;
        using (var c = new RenderContext(_dir, Open)) { var m = new MemoryStream(); Exporter.ToPdf(new PubsmithDocument { Pages = [page] }, c, m); full = m.Length; }
        var largestPicture = Directory.GetFiles(_dir, "p*.png").Max(f => new FileInfo(f).Length);

        var limit = full / 3;
        using var ctx = new RenderContext(_dir, Open with { MaxPdfBytes = limit });
        var s = new MemoryStream();
        var ex = Assert.Throws<RenderLimitException>(() => Exporter.ToPdf(new PubsmithDocument { Pages = [page] }, ctx, s));
        Assert.Equal(RenderLimitException.PdfBytes, ex.Limit);
        Assert.Equal(1, ex.Page);
        Assert.InRange(ex.Element!.Value, 2, 19);   // stopped between elements, inside the page
        var pdf = s.ToArray();
        Assert.False(Has(pdf, "%%EOF"));
        // The bound: the limit plus one step's writes (one picture, as SkPDF writes it, uncompressed at most 4 bytes a pixel).
        Assert.True(pdf.Length <= limit + 40 * 40 * 4 + largestPicture + 2048, $"{pdf.Length} written, limit {limit}");
    }

    [Fact]
    public void APdfOverItsBytes_AtAPageEnd_IsAborted_AndAtTheLimit_ItIsClosed()
    {
        var doc = new PubsmithDocument { Pages = [new Page(400, 400) { Elements = Shapes(2_000) }, new Page(400, 400) { Elements = Shapes(10) }] };
        IReadOnlyList<long> ends;
        long full;
        using (var c = new RenderContext(_dir, Open)) { var m = new MemoryStream(); Exporter.ToPdf(doc, c, m); ends = [.. c.PdfBytesAtChecks]; full = m.Length; }
        // ends: the bytes after page 1's end, after page 2's end (= before the close).
        Assert.Equal(2, ends.Count);
        Assert.True(full > ends[1], "the close writes more");

        using (var ctx = new RenderContext(_dir, Open with { MaxPdfBytes = ends[0] - 1 }))
        {
            var s = new MemoryStream();
            var ex = Assert.Throws<RenderLimitException>(() => Exporter.ToPdf(doc, ctx, s));
            Assert.Equal(RenderLimitException.PdfBytes, ex.Limit);
            Assert.Equal(1, ex.Page);
            Assert.Null(ex.Element);
            Assert.False(Has(s.ToArray(), "%%EOF"));
        }
        using (var ctx = new RenderContext(_dir, Open with { MaxPdfBytes = ends[1] - 1 }))
        {
            var s = new MemoryStream();
            var ex = Assert.Throws<RenderLimitException>(() => Exporter.ToPdf(doc, ctx, s));   // checked before the close
            Assert.Equal(2, ex.Page);
            Assert.False(Has(s.ToArray(), "%%EOF"));
        }
        using (var ctx = new RenderContext(_dir, Open with { MaxPdfBytes = ends[0] }))
        {
            // Exactly at the limit after page 1's end, and at page 2's checkpoints (drawing shapes writes nothing): not
            // past it, so page 2 is drawn; its end passes it.
            var s = new MemoryStream();
            var ex = Assert.Throws<RenderLimitException>(() => Exporter.ToPdf(doc, ctx, s));
            Assert.Equal(2, ex.Page);
            Assert.Null(ex.Element);
        }
        using (var ctx = new RenderContext(_dir, Open with { MaxPdfBytes = ends[1] }))
        {
            // At the limit before the close, the close's own writes may pass it: they are part of the bound.
            var s = new MemoryStream();
            Exporter.ToPdf(doc, ctx, s);
            Assert.Equal(full, s.Length);
            Assert.True(s.Length > ends[1]);
            // The PDF's count ends with it: a later bitmap with the context is not judged by it.
            using (Exporter.RenderBitmap(new Page(100, 100) { Elements = Shapes(5) }, 72, ctx)) { }
        }
    }

    [Fact]
    public void AStoppedPdf_IsAborted_NotClosed()
    {
        // Text on page 1 (its font is written at the close), pictures on page 2, where the meter stops it.
        var doc = new PubsmithDocument { Pages = [new Page(400, 200) { Elements = [Text("Fonts are written when a PDF closes")] }, new Page(400, 200) { Elements = Pictures(10) }] };
        RenderContext? ctx = null;
        var clock = new PageTwoClock(() => ctx!.PicturesDrawn > 0);   // jumps an hour at each reading once page 2 draws its first picture
        ctx = new RenderContext(_dir, Open with { MaxDrawTime = TimeSpan.FromSeconds(4) }, clock);
        var s = new MemoryStream();
        var ex = Assert.Throws<RenderLimitException>(() => Exporter.ToPdf(doc, ctx, s));
        Assert.Equal(RenderLimitException.DrawTime, ex.Limit);
        Assert.Equal(2, ex.Page);
        Assert.Equal(2, ex.Element);
        var pdf = s.ToArray();
        Assert.True(Has(pdf, "%PDF"), "something was written before the stop");
        Assert.False(Has(pdf, "%%EOF"));
        Assert.False(Has(pdf, "/FontFile") || Has(pdf, "/FontDescriptor"));

        var next = new MemoryStream();
        Assert.Throws<RenderLimitException>(() => Exporter.ToPdf(new PubsmithDocument { Pages = [new Page(10, 10)] }, ctx!, next));
        Assert.Equal(0, next.Length);
    }

    [Fact]
    public void AFailingPdf_IsAborted_NotClosed()
    {
        // Any exception, not only a limit: a picture whose decoding throws is not one we can make, so the clock throws.
        var doc = new PubsmithDocument { Pages = [new Page(400, 200) { Elements = [Text("Fonts are written when a PDF closes"), .. Pictures(3)] }] };
        using var ctx = new RenderContext(_dir, Open, new StepClock(throwAt: 4));
        var s = new MemoryStream();
        Assert.Throws<InvalidOperationException>(() => Exporter.ToPdf(doc, ctx, s));
        Assert.False(Has(s.ToArray(), "%%EOF"));
    }

    private sealed class PageTwoClock(Func<bool> started) : TimeProvider
    {
        private long _now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => started() ? _now += TimeSpan.TicksPerHour : _now;
    }

    private sealed class StepClock(int throwAt = 0) : TimeProvider
    {
        private long _now;
        private int _readings;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp()
        {
            if (++_readings == throwAt) throw new InvalidOperationException("the clock failed");
            return _now += TimeSpan.TicksPerSecond;
        }
    }
}
