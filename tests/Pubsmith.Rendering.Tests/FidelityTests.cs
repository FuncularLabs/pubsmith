using Pubsmith.Core;
using Xunit.Abstractions;

namespace Pubsmith.Rendering.Tests;

// AC4: rendered pages match Publisher's own 300 dpi output within the plan's thresholds, and a deliberately
// wrong model does NOT (proof that each threshold can fail).
public class FidelityTests(ITestOutputHelper output)
{
    public const double F01Threshold = 2.0;   // LibreOffice: 4.1
    public const double F04Threshold = 2.0;   // LibreOffice: 4.5
    public const double F12Threshold = 1.3;   // LibreOffice: 1.3

    [Fact] public void F01_PageCustomSize_WithinThreshold() => AssertWithin("F01-page-custom-size", CorpusModels.F01(), F01Threshold);
    [Fact] public void F01_Mutated_ExceedsThreshold() => AssertAbove("F01-page-custom-size", CorpusModels.F01(mutate: true), F01Threshold);

    [Fact] public void F04_ImagesCropAlpha_WithinThreshold() => AssertWithin("F04-images-crop-alpha", CorpusModels.F04(), F04Threshold);
    [Fact] public void F04_Mutated_ExceedsThreshold() => AssertAbove("F04-images-crop-alpha", CorpusModels.F04(mutate: true), F04Threshold);

    [Fact] public void F12_Bleed_WithinThreshold() => AssertWithin("F12-bleed", CorpusModels.F12(), F12Threshold);
    [Fact] public void F12_Mutated_ExceedsThreshold() => AssertAbove("F12-bleed", CorpusModels.F12(mutate: true), F12Threshold);

    private double ScoreOf(string golden, Page page)
    {
        var ctx = Fixtures.Context();
        using var rendered = Exporter.RenderBitmap(page, 300, ctx);
        using var reference = SkiaSharp.SKBitmap.Decode(Fixtures.Golden(golden));
        var score = ImageComparer.Score(reference, rendered);
        output.WriteLine($"{golden}: score {score:F2}; warnings: {string.Join(" | ", ctx.Warnings.Select(w => w.Message))}");
        Assert.Empty(ctx.Warnings);   // a substituted font or missing asset would make the score meaningless
        return score;
    }

    private void AssertWithin(string golden, Page page, double threshold) => Assert.InRange(ScoreOf(golden, page), 0, threshold);
    private void AssertAbove(string golden, Page page, double threshold) => Assert.True(ScoreOf(golden, page) > threshold);
}
