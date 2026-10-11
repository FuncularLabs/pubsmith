using Pubsmith.Core;
using Pubsmith.PubReader;
using Pubsmith.PubReader.Contents;
using Pubsmith.PubReader.OfficeArt;
using Pubsmith.PubReader.Quill;
using SkiaSharp;

namespace Pubsmith.Rendering.Tests;

/// <summary>
/// The ordinary publications WI-006's defaults are sized against (AC5): WI-004's three large publications, imported as
/// PublicationScaleTests builds them; Task 0 round 1's book of 6,000 visible characters a page; and the catalogue of
/// 100 pages, each a distinct 14.4 MP photo.
/// </summary>
internal static class OrdinaryPublications
{
    private const double Pt = 12700;
    private static readonly CharFormat Plain = new("Arial", 10, false, false, 0, RgbColor.Black, false, false, 0);
    private static readonly ParaFormat Para = new(ParagraphAlign.Left, new LineSpacing(LineSpacingKind.Multiple, 1), 0, 0, 0, 0, 0);

    private static Story StoryOf(uint id, int paragraphs, int chars = 150) =>
        new(id, Enumerable.Range(0, paragraphs).Select(_ => new StoryParagraph(Para, [new StoryRun(new string('t', chars), Plain)])).ToList());

    private static EscherShape Shape(uint seq, ushort spt = 1) => new()
    {
        Offset = 0, ShapeId = seq, ShapeType = spt, Flags = 0, Seqnum = seq,
        Anchor = new AnchorEmu(0, 0, (int)(100 * Pt), (int)(100 * Pt)),
        Props = spt == 202 ? new Dictionary<ushort, uint> { [EscherShape.PropFillFlags] = 0x100000, [EscherShape.PropLineFlags] = 0x80000 } : new Dictionary<ushort, uint>(),
        Complex = new Dictionary<ushort, byte[]>(), Children = [],
    };

    private static PubsmithDocument Compose(IReadOnlyList<PageInfo> all, IReadOnlyList<PageInfo> content, IReadOnlyList<EscherShape> top,
        Dictionary<uint, ShapeInfo>? shapes = null, Dictionary<uint, Story>? stories = null) =>
        PubImporter.Compose("t", new ContentsModel(612 * 12700, 792 * 12700, all, content, shapes ?? [], []), new ColorResolver([]), top, stories ?? [], [], []).Document;

    /// <summary>WI-004: one linked text box per page (300 boxes), all sharing one 3,000-paragraph story.</summary>
    public static PubsmithDocument LinkedStory()
    {
        var boxes = Enumerable.Range(1, 300).Select(i => Shape((uint)i, 202)).ToArray();
        var pages = boxes.Select(b => new PageInfo(1000 + b.Seqnum!.Value, false, null, null, [b.Seqnum!.Value])).ToList();
        var info = boxes.ToDictionary(b => b.Seqnum!.Value, b => new ShapeInfo(b.Seqnum!.Value, 0x01, 9, null, null));
        return Compose(pages, pages, boxes, info, new() { [9] = StoryOf(9, 3000) });
    }

    /// <summary>WI-004: a master with a header bar and a 3-paragraph footer on 1,000 pages, each with its own 20-paragraph story.</summary>
    public static PubsmithDocument BookWithMaster()
    {
        var header = Shape(1);
        var footer = Shape(2, 202);
        var master = new PageInfo(500, true, null, null, [1, 2]);
        var bodies = Enumerable.Range(0, 1000).Select(i => Shape((uint)(10 + i), 202)).ToArray();
        var pages = bodies.Select(b => new PageInfo(2000 + b.Seqnum!.Value, false, 500, null, [b.Seqnum!.Value])).ToList();
        var info = bodies.ToDictionary(b => b.Seqnum!.Value, b => new ShapeInfo(b.Seqnum!.Value, 0x01, 100 + b.Seqnum!.Value, null, null));
        info[2] = new ShapeInfo(2, 0x01, 5, null, null);
        var stories = bodies.ToDictionary(b => 100 + b.Seqnum!.Value, b => StoryOf(100 + b.Seqnum!.Value, 20));
        stories[5] = StoryOf(5, 3, 40);
        return Compose([master, .. pages], pages, [header, footer, .. bodies], info, stories);
    }

    /// <summary>WI-004: 100 pages of 200 shapes.</summary>
    public static PubsmithDocument PagesOfShapes()
    {
        var shapes = Enumerable.Range(1, 20_000).Select(i => Shape((uint)i)).ToArray();
        var pages = Enumerable.Range(0, 100).Select(p => new PageInfo((uint)(30_000 + p), false, null, null, shapes.Skip(p * 200).Take(200).Select(s => s.Seqnum!.Value).ToList())).ToList();
        return Compose(pages, pages, shapes);
    }

    /// <summary>Task 0 round 1's book: 1,000 pages of 6,000 visible characters (10 paragraphs of 4 runs, one italic), a header bar and a footer.</summary>
    public static PubsmithDocument Book()
    {
        var words = "the quick brown fox jumps over a lazy dog while reading publisher files ";
        string Chars(int n) => string.Concat(Enumerable.Repeat(words, n / words.Length + 1))[..n];
        var body = new TextStyle { Family = "Times New Roman", Size = 10 };
        return new PubsmithDocument
        {
            Pages = Enumerable.Range(0, 1000).Select(_ => new Page(612, 792)
            {
                Elements =
                [
                    new ShapeElement { Bounds = new Box(72, 36, 468, 12), Fill = new Rgba(40, 40, 40) },
                    new TextElement { Bounds = new Box(72, 72, 468, 648), Paragraphs = Enumerable.Range(0, 10).Select(_ => new Paragraph { Runs = Enumerable.Range(0, 4).Select(r => new TextRun(Chars(150), body with { Italic = r == 2 })).ToList() }).ToList() },
                    new TextElement { Bounds = new Box(72, 740, 468, 30), Paragraphs = Enumerable.Range(0, 3).Select(_ => new Paragraph { Runs = [new TextRun(Chars(40), body)] }).ToList() },
                ],
            }).ToList(),
        };
    }

    public const int PhotoWidth = 4800, PhotoHeight = 3000;   // 14.4 MP

    /// <summary>The catalogue: 100 pages, each a distinct photo (cat000.jpg to cat099.jpg, made by <see cref="WritePhotos"/>) on 540 x 360 pt and 860 characters.</summary>
    public static PubsmithDocument Catalogue()
    {
        var text = string.Concat(Enumerable.Repeat("Item description with a few words of text. ", 20));
        return new PubsmithDocument
        {
            Pages = Enumerable.Range(0, 100).Select(k => new Page(612, 792)
            {
                Elements =
                [
                    new ImageElement { Bounds = new Box(36, 36, 540, 360), Source = $"cat{k:D3}.jpg" },
                    new TextElement { Bounds = new Box(36, 420, 540, 300), Paragraphs = [new Paragraph { Runs = [new TextRun(text, new TextStyle { Family = "Arial", Size = 10 })] }] },
                ],
            }).ToList(),
        };
    }

    /// <summary>The catalogue's 100 distinct 14.4 MP photos: noise on a gradient, JPEG quality 85.</summary>
    public static void WritePhotos(string dir) =>
        Parallel.For(0, 100, new ParallelOptions { MaxDegreeOfParallelism = 8 }, k =>
        {
            var path = Path.Combine(dir, $"cat{k:D3}.jpg");
            if (File.Exists(path)) return;
            var rng = new Random(100 + k);
            using var bmp = new SKBitmap(new SKImageInfo(PhotoWidth, PhotoHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(bmp))
            {
                using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(PhotoWidth, PhotoHeight), [new SKColor((byte)rng.Next(256), 80, 40), new SKColor(20, (byte)rng.Next(256), 220)], SKShaderTileMode.Clamp);
                using var paint = new SKPaint { Shader = shader };
                canvas.DrawRect(0, 0, PhotoWidth, PhotoHeight, paint);
                using var dot = new SKPaint { IsAntialias = true };
                for (var i = 0; i < PhotoWidth * PhotoHeight / 400; i++) { dot.Color = new SKColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), 160); canvas.DrawCircle(rng.Next(PhotoWidth), rng.Next(PhotoHeight), 1 + rng.Next(6), dot); }
            }
            using var data = bmp.Encode(SKEncodedImageFormat.Jpeg, 85);
            File.WriteAllBytes(path, data.ToArray());
        });

    public static IEnumerable<(string Name, Func<PubsmithDocument> Build)> All() =>
    [
        ("WI-004's 300 pages of one linked story", LinkedStory),
        ("WI-004's 1,000 pages with a master", BookWithMaster),
        ("WI-004's 100 pages of 200 shapes", PagesOfShapes),
        ("Round 1's book of 1,000 pages", Book),
        ("The catalogue of 100 photos", Catalogue),
    ];
}
