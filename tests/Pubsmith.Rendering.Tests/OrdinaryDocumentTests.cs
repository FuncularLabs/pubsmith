using System.Text;
using Pubsmith.Core;

namespace Pubsmith.Rendering.Tests;

// WI-006 AC5: ordinary publications never meet a limit. Every counted value of WI-004's large publications, the book
// and the catalogue, in PDF and in PNG at 300 dpi, is at most half its default (typefaces at most a quarter). Their
// drawing times are the opt-in CalibrationTests' (measured, not counted).
public sealed class OrdinaryDocumentTests
{
    public static TheoryData<string> Publications() => [.. OrdinaryPublications.All().Select(p => p.Name)];

    [Theory]
    [MemberData(nameof(Publications))]
    public void CountedValues_AreFarBelowTheirLimits(string name)
    {
        var doc = OrdinaryPublications.All().Single(p => p.Name == name).Build();
        var d = RenderLimits.Default;
        var fraction = d with
        {
            MaxPages = d.MaxPages / 2, MaxPngPixels = d.MaxPngPixels / 2, MaxPdfTypefaces = d.MaxPdfTypefaces / 4, MaxFontNames = d.MaxFontNames / 2,
            MaxTextBoxCharacters = d.MaxTextBoxCharacters / 2, MaxTextBoxRuns = d.MaxTextBoxRuns / 2, MaxWordArtGlyphs = d.MaxWordArtGlyphs / 2,
        };
        using var ctx = new RenderContext(null, fraction);

        // The whole command, PDF and PNG at 300 dpi, as the CLI checks it: pages, PNG pixels, typefaces, font names.
        RenderLimits.Check(doc, new RenderOutputs(Pdf: true, PngDpi: 300), ctx);
        // Each text element, as PageRenderer checks it before drawing: characters, runs, WordArt glyphs, font names.
        for (var p = 0; p < doc.Pages.Count; p++)
            for (var e = 0; e < doc.Pages[p].Elements.Count; e++)
                if (doc.Pages[p].Elements[e] is TextElement t) ctx.CheckText(t, p + 1, e + 1);
        // Its document and its largest page.
        Assert.True(Encoding.UTF8.GetByteCount(DocumentJson.Serialize(doc)) <= DocumentJson.MaxLoadBytes / 2);
        Assert.All(doc.Pages, page => Assert.True(Exporter.PixelSize(page, 300) is var (w, h) && (long)w * h <= Exporter.MaxPixels / 2));
        // The catalogue's photos, within half the cap for a picture that counts as decoded in a PDF.
        Assert.True((long)OrdinaryPublications.PhotoWidth * OrdinaryPublications.PhotoHeight <= d.MaxPdfPicturePixels / 2);
    }
}
