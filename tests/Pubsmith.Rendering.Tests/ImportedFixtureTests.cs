using Pubsmith.Core;

namespace Pubsmith.Rendering.Tests;

// WI-002: documents produced by tools/oracle/Export-PubLayout.ps1 (Publisher-assisted export) from Publisher-built
// feature files load and render, and every picture they reference is present and decodable (a flattened element
// that fails to load would silently drop artwork from the page).
public class ImportedFixtureTests
{
    private static readonly string Dir = Path.Combine(Fixtures.Dir, "imported");

    [Theory]
    [InlineData("F04-images-crop-alpha", 1, 1, 4)]    // background rectangle + four pictures
    [InlineData("F14-multipage-master", 3, 4, 0)]     // master header bar, master text, page number, body text
    [InlineData("F18-F19-wordart-curved", 1, 0, 5)]   // WordArt and effect text: all flattened
    public void ExportedDocument_LoadsAndRendersEveryPicture(string name, int pages, int minNative, int minPictures)
    {
        var doc = DocumentJson.Load(Path.Combine(Dir, name + ".json"));
        Assert.Equal(pages, doc.Pages.Count);

        var first = doc.Pages[0].Elements;
        Assert.True(first.Count(e => e is TextElement or ShapeElement) >= minNative, "too few editable elements on page 1");
        Assert.True(first.OfType<ImageElement>().Count() >= minPictures, "too few pictures on page 1");

        var ctx = new RenderContext(baseDirectory: Dir);
        foreach (var page in doc.Pages)
        {
            using var bmp = Exporter.RenderBitmap(page, 72, ctx);
            Assert.True(bmp.Width > 0);
        }
        Assert.DoesNotContain(ctx.Warnings, w => w.Code is RenderWarning.ImageMissing or RenderWarning.ImageUnreadable);
    }

    [Fact]
    public void MasterElements_AppearOnEveryPage()
    {
        // F14 puts a header bar and a page number on the master page; each exported page must carry them, behind
        // the page's own text (master layer first).
        var doc = DocumentJson.Load(Path.Combine(Dir, "F14-multipage-master.json"));
        Assert.Equal(3, doc.Pages.Count);
        Assert.All(doc.Pages, p =>
        {
            Assert.True(p.Elements.Count >= 4, $"page has only {p.Elements.Count} elements");
            var bar = Assert.IsType<ShapeElement>(p.Elements[0]);
            Assert.Equal(new Box(0, 0, 612, 40), bar.Bounds);
        });
    }
}
