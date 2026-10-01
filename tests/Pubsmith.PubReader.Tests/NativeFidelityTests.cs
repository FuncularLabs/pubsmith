using Pubsmith.Core;
using Pubsmith.Rendering;
using SkiaSharp;
using Xunit.Abstractions;

namespace Pubsmith.PubReader.Tests;

// End to end: Publisher-built variant -> native import -> render -> score against Publisher's own 300 dpi render.
// Each threshold has a twin with the feature removed from the imported document, which must score worse
// (proof that the score measures the feature).
public class NativeFidelityTests(ITestOutputHelper output)
{
    private double Score(string variant, Func<PubsmithDocument, PubsmithDocument>? mutate = null)
    {
        var (group, name) = (variant.Split("__")[0], variant.Split("__")[1]);
        var r = PubImporter.Import(Fixtures.Pub(group, name));
        var doc = mutate is null ? r.Document : mutate(r.Document);
        var dir = Directory.CreateTempSubdirectory("pubsmith-fid-").FullName;
        try
        {
            foreach (var (rel, bytes) in r.Assets) { var p = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllBytes(p, bytes); }
            using var bmp = Exporter.RenderBitmap(doc.Pages[0], 300, new RenderContext(baseDirectory: dir));
            using var golden = SKBitmap.Decode(Path.Combine(Fixtures.Dir, "golden", variant + ".png"));
            var s = ImageComparer.Score(golden, bmp);
            output.WriteLine($"{variant}{(mutate is null ? "" : " (mutated)")}: {s:F2}");
            return s;
        }
        finally { Directory.Delete(dir, true); }
    }

    private static PubsmithDocument Map(PubsmithDocument d, Func<Element, Element> f) =>
        d with { Pages = d.Pages.Select(p => p with { Elements = p.Elements.Select(f).ToList() }).ToList() };

    private static PubsmithDocument NoShadows(PubsmithDocument d) => Map(d, e => e with { Shadow = null });
    private static PubsmithDocument NoWarps(PubsmithDocument d) => Map(d, e => e is TextElement t ? t with { Warp = null } : e);
    private static PubsmithDocument InCalibri(PubsmithDocument d) => Map(d, e => e is TextElement t
        ? t with { Paragraphs = t.Paragraphs.Select(p => p with { Runs = p.Runs.Select(r => r with { Style = r.Style with { Family = "Calibri" } }).ToList() }).ToList() } : e);

    /// <summary>Every element turned the other way (by twice the group's 30 degrees) about the elements' common centre.</summary>
    private static PubsmithDocument TurnedTheOtherWay(PubsmithDocument d)
    {
        var els = d.Pages[0].Elements;
        double cx = els.Average(e => e.Bounds.CenterX), cy = els.Average(e => e.Bounds.CenterY), rad = -60 * Math.PI / 180;
        return Map(d, e =>
        {
            double dx = e.Bounds.CenterX - cx, dy = e.Bounds.CenterY - cy;
            double x = cx + dx * Math.Cos(rad) - dy * Math.Sin(rad), y = cy + dx * Math.Sin(rad) + dy * Math.Cos(rad);
            return e with { Bounds = e.Bounds with { X = x - e.Bounds.Width / 2, Y = y - e.Bounds.Height / 2 }, Rotation = e.Rotation - 60 };
        });
    }

    [Fact]
    public void RotatedGroup_MatchesPublisher()
    {
        // Publisher's render of a group turned 30 degrees; turning it the other way must score clearly worse.
        var score = Score("group__rotated");
        output.WriteLine($"rotated group {score:F2}");
        Assert.InRange(score, 0, RotatedGroupThreshold);
        Assert.True(Score("group__rotated", TurnedTheOtherWay) > RotatedGroupThreshold);
    }

    private const double RotatedGroupThreshold = 1.0;

    [Fact]
    public void WordArtFont_IsTheNamedFace()
    {
        // "Arial Black" must resolve to Arial at weight 900, not fall back: drawn in Calibri the page scores worse.
        Assert.InRange(Score("wordart__fill-red"), 0, 1.5);
        Assert.True(Score("wordart__fill-red", InCalibri) > 1.5);
    }

    [Theory]
    [InlineData("shadow__on", 0.8)]
    [InlineData("shadow__offset", 0.8)]
    [InlineData("wordart__shadow", 1.2)]
    public void Shadows_MatchPublisher(string variant, double threshold)
    {
        Assert.InRange(Score(variant), 0, threshold);
        Assert.True(Score(variant, NoShadows) > threshold, "removing the shadow must score worse than the threshold");
    }

    [Fact]
    public void ArchWarp_MatchesPublisher()
    {
        Assert.InRange(Score("wordart__arch"), 0, 2.2);
        Assert.True(Score("wordart__arch", NoWarps) > 2.2, "drawing the text straight must score worse than the threshold");
    }

    /// <summary>
    /// Regression bound only: on this page the circle text is thin outlines on white, so the page metric cannot
    /// tell circle from straight text (2.93 vs 2.87 on 2026-09-30). The circle geometry itself is pinned by
    /// WarpAndShadowTests.Circle_WrapsAroundTheFrameEllipse.
    /// </summary>
    [Fact]
    public void CircleWarp_DoesNotRegress() => Assert.InRange(Score("wordart__circle"), 0, 3.2);

    [Theory]
    [InlineData("wordart__base", 1.0)]
    [InlineData("wordart__fill-red", 1.5)]
    [InlineData("wordart__line-white", 0.5)]
    public void PlainWordArt_MatchesPublisher(string variant, double threshold) => Assert.InRange(Score(variant), 0, threshold);
}
