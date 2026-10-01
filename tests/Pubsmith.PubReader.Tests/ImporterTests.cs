using Pubsmith.Core;

namespace Pubsmith.PubReader.Tests;

// Layer 5: .pub -> Pubsmith document (AC2-AC8 of WI-004), on diff-corpus fixtures with known values.
public class ImporterTests
{
    private static PubImportResult Import(string group, string variant) => PubImporter.Import(Fixtures.Pub(group, variant));

    private static void Near(Box expected, Box actual, double tol = 0.1)
    {
        Assert.InRange(actual.X, expected.X - tol, expected.X + tol);
        Assert.InRange(actual.Y, expected.Y - tol, expected.Y + tol);
        Assert.InRange(actual.Width, expected.Width - tol, expected.Width + tol);
        Assert.InRange(actual.Height, expected.Height - tol, expected.Height + tol);
    }

    [Fact]
    public void Rectangle_PositionSizeFill()
    {
        var r = Import("geometry", "base");
        var page = Assert.Single(r.Document.Pages);
        Assert.Equal((612.0, 792.0), (page.Width, page.Height));
        var rect = Assert.IsType<ShapeElement>(Assert.Single(page.Elements));
        Assert.Equal(ShapeKind.Rectangle, rect.Kind);
        Near(new Box(72, 72, 144, 72), rect.Bounds);
        Assert.Equal(new Rgba(0, 0, 255), rect.Fill);
        Assert.NotNull(rect.Stroke);
    }

    [Fact]
    public void Rotation30_KeepsTheFrame()
    {
        var rect = Assert.IsType<ShapeElement>(Assert.Single(Import("geometry", "rot-30").Document.Pages[0].Elements));
        Assert.Equal(30, rect.Rotation, 2);
        Near(new Box(72, 72, 144, 72), rect.Bounds);
    }

    [Fact]
    public void Rotation90_UnswapsTheStoredBox()
    {
        // Between 45 and 135 degrees Publisher stores the box with width and height exchanged about its centre.
        var rect = Assert.IsType<ShapeElement>(Assert.Single(Import("geometry", "rot-90").Document.Pages[0].Elements));
        Assert.Equal(90, rect.Rotation, 2);
        Near(new Box(72, 72, 144, 72), rect.Bounds);
    }

    [Fact]
    public void ZOrder_FollowsTheDrawingStream()
    {
        var els = Import("geometry", "two-rects-swapped").Document.Pages[0].Elements.Cast<ShapeElement>().ToList();
        Assert.Equal(2, els.Count);
        Assert.Equal(new Rgba(255, 0, 0), els[0].Fill);   // sent to back
        Assert.Equal(new Rgba(0, 0, 255), els[1].Fill);
    }

    [Fact]
    public void Oval_IsAnEllipse()
    {
        var s = Assert.IsType<ShapeElement>(Assert.Single(Import("shapes", "oval").Document.Pages[0].Elements));
        Assert.Equal(ShapeKind.Ellipse, s.Kind);
    }

    [Fact]
    public void NoFill_AndNoLine_AreRespected()
    {
        Assert.Null(Assert.IsType<ShapeElement>(Assert.Single(Import("fill", "no-fill").Document.Pages[0].Elements)).Fill);
        Assert.Null(Assert.IsType<ShapeElement>(Assert.Single(Import("line", "no-line").Document.Pages[0].Elements)).Stroke);
    }

    [Fact]
    public void LineWeightAndColour()
    {
        var w = Assert.IsType<ShapeElement>(Assert.Single(Import("line", "weight-4").Document.Pages[0].Elements));
        Assert.Equal(4, w.Stroke!.Width, 2);
        var c = Assert.IsType<ShapeElement>(Assert.Single(Import("line", "color-red").Document.Pages[0].Elements));
        Assert.Equal(new Rgba(255, 0, 0), c.Stroke!.Color);
    }

    [Fact]
    public void Transparency_BecomesAlpha()
    {
        var s = Assert.IsType<ShapeElement>(Assert.Single(Import("fill", "transparency-50").Document.Pages[0].Elements));
        Assert.InRange(s.Fill!.Value.A, 120, 135);
    }

    [Fact]
    public void TextBox_TextFontInsets()
    {
        var t = Assert.IsType<TextElement>(Assert.Single(Import("text", "base").Document.Pages[0].Elements));
        Near(new Box(72, 72, 288, 144), t.Bounds);
        Assert.Equal(2.88, t.Insets.Left, 2);
        var run = Assert.Single(Assert.Single(t.Paragraphs).Runs);
        Assert.Equal("Hello Example Text world", run.Text);
        Assert.Equal("Calibri", run.Style.Family);
        Assert.Equal(10, run.Style.Size, 2);
    }

    [Fact]
    public void ExactLineSpacing_IsCarried()
    {
        var t = Assert.IsType<TextElement>(Assert.Single(Import("text", "ls-exact-30").Document.Pages[0].Elements));
        Assert.Equal(30, Assert.Single(t.Paragraphs).ExactLineSpacing!.Value, 2);
    }

    [Fact]
    public void VerticalAnchorMiddle_IsCarried()
    {
        var t = Assert.IsType<TextElement>(Assert.Single(Import("text", "vanchor-middle").Document.Pages[0].Elements));
        Assert.Equal(TextVerticalAlign.Middle, t.VerticalAlign);
    }

    [Fact]
    public void RotatedTextBox_270()
    {
        var t = Assert.IsType<TextElement>(Assert.Single(Import("text", "rotated-270").Document.Pages[0].Elements));
        Assert.Equal(270, ((t.Rotation % 360) + 360) % 360, 2);
        Near(new Box(72, 72, 288, 144), t.Bounds);
    }

    [Fact]
    public void Picture_HasItsOwnImageAndFrame()
    {
        var r = Import("picture", "base");
        var img = Assert.IsType<ImageElement>(Assert.Single(r.Document.Pages[0].Elements));
        Near(new Box(72, 72, 216, 216), img.Bounds);
        Assert.True(r.Assets.TryGetValue(img.Source, out var bytes));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes![..4]);
    }

    [Fact]
    public void PictureCrop_IsASourceFraction()
    {
        var img = Assert.IsType<ImageElement>(Assert.Single(Import("picture", "crop-left").Document.Pages[0].Elements));
        Assert.NotNull(img.Crop);
        Assert.InRange(img.Crop!.Left, 0.068, 0.073);   // 54 pt of a 768 pt picture
        Assert.Equal(0, img.Crop.Right, 3);
    }

    [Fact]
    public void PictureBorder_BecomesStroke()
    {
        var img = Assert.IsType<ImageElement>(Assert.Single(Import("picture", "border").Document.Pages[0].Elements));
        Assert.Equal(5, img.Stroke!.Width, 2);
        Assert.Equal(new Rgba(255, 255, 255), img.Stroke.Color);
    }

    [Fact]
    public void SecondPage_AndMaster()
    {
        var two = Import("pages", "two-rect-p2").Document;
        Assert.Equal(2, two.Pages.Count);
        Assert.Equal(new Rgba(255, 0, 0), Assert.IsType<ShapeElement>(Assert.Single(two.Pages[1].Elements)).Fill);

        var master = Import("pages", "master-rect").Document.Pages[0].Elements.Cast<ShapeElement>().ToList();
        Assert.Equal(new Rgba(0, 200, 0), master[0].Fill);   // master layer first (behind)
        Assert.Equal(new Rgba(0, 0, 255), master[1].Fill);
    }

    [Fact]
    public void WordArt_BecomesStretchedStyledText()
    {
        var r = Import("wordart", "base");
        var t = Assert.IsType<TextElement>(Assert.Single(r.Document.Pages[0].Elements));
        Assert.Equal(TextFit.Stretch, t.Fit);
        var run = Assert.Single(Assert.Single(t.Paragraphs).Runs);
        Assert.Equal("Sample Text!", run.Text);
        Assert.Equal("Arial Black", run.Style.Family);
    }

    [Fact]
    public void WordArtOutline_BecomesTheTextOutline()
    {
        var t = Assert.IsType<TextElement>(Assert.Single(Import("wordart", "line-white").Document.Pages[0].Elements));
        Assert.Equal(new Rgba(255, 255, 255), t.Outline!.Color);
        Assert.Equal(1.5, t.Outline.Width, 2);
    }

    [Fact]
    public void Shadow_DefaultAndOffset()
    {
        var on = Assert.IsType<ShapeElement>(Assert.Single(Import("shadow", "on").Document.Pages[0].Elements));
        Assert.NotNull(on.Shadow);
        Assert.Equal((2.0, 2.0), (on.Shadow!.OffsetX, on.Shadow.OffsetY));
        var off = Assert.IsType<ShapeElement>(Assert.Single(Import("shadow", "offset").Document.Pages[0].Elements));
        Assert.Equal((6.0, 6.0), (off.Shadow!.OffsetX, off.Shadow.OffsetY));
        Assert.Null(Assert.IsType<ShapeElement>(Assert.Single(Import("geometry", "base").Document.Pages[0].Elements)).Shadow);
    }

    [Theory]
    [InlineData("arch", WarpKind.ArchUp)]
    [InlineData("circle", WarpKind.Circle)]
    public void WordArtWarp_IsMapped(string variant, WarpKind expected)
    {
        var r = Import("wordart", variant);
        Assert.Equal(expected, Assert.IsType<TextElement>(Assert.Single(r.Document.Pages[0].Elements)).Warp!.Kind);
        Assert.DoesNotContain(r.Issues, i => i.Detail.Contains("warp"));
    }

    [Fact]
    public void WordArtShadow_IsGrey()
    {
        var t = Assert.IsType<TextElement>(Assert.Single(Import("wordart", "shadow").Document.Pages[0].Elements));
        Assert.Equal(new Rgba(0x86, 0x86, 0x86), t.Shadow!.Color);
    }

    [Theory]
    [InlineData((ushort)137)]
    [InlineData((ushort)142)]
    [InlineData((ushort)168)]
    public void UnmappedWarps_AreNull(ushort spt) => Assert.Null(PubImporter.WarpFor(spt));

    [Fact]
    public void OvalMask_IsApplied()
    {
        var r = Import("picture", "oval-mask");
        Assert.Equal(ShapeKind.Ellipse, Assert.IsType<ImageElement>(Assert.Single(r.Document.Pages[0].Elements)).Mask);
        Assert.DoesNotContain(r.Issues, i => i.Detail.Contains("crop-to-shape"));
    }

    [Fact]
    public void Line_IsApproximated_AndReported()
    {
        var r = Import("shapes", "line");
        Assert.Single(r.Document.Pages[0].Elements);
        Assert.Contains(r.Issues, i => i.Kind == ImportIssueKind.Approximated && i.Detail.Contains("line"));
    }

    [Theory]
    [InlineData("shapes", "star5")]
    [InlineData("table", "2x2")]
    public void Unsupported_IsPlaceheldAndReported(string group, string variant)
    {
        var r = Import(group, variant);
        Assert.NotEmpty(r.Document.Pages[0].Elements);            // something visible stands in its place
        Assert.Contains(r.Issues, i => i.Kind == ImportIssueKind.Placeholder);
    }

    [Theory]
    [InlineData("fill", "gradient", "gradient")]
    [InlineData("line", "dash-4", "dash")]
    public void Approximations_AreReported(string group, string variant, string word)
    {
        var r = Import(group, variant);
        Assert.Contains(r.Issues, i => i.Kind == ImportIssueKind.Approximated && i.Detail.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Group_MembersArePlaced()
    {
        var els = Import("group", "two").Document.Pages[0].Elements.Cast<ShapeElement>().ToList();
        Assert.Equal(2, els.Count);
        Near(new Box(72, 72, 144, 72), els[0].Bounds, 0.5);
        Near(new Box(250, 72, 72, 72), els[1].Bounds, 0.5);
    }

    [Fact]
    public void EveryShape_IsAccountedFor()
    {
        // AC8: the number of top-level drawing elements on content pages equals what the report + document account for.
        foreach (var (g, v) in new[] { ("shapes", "star5"), ("picture", "two"), ("wordart", "base"), ("table", "2x2") })
        {
            var r = Import(g, v);
            Assert.True(r.ShapesRead > 0);
            Assert.Equal(r.ShapesRead, r.ShapesConverted + r.ShapesPlaceheld);
            Assert.Equal(r.ShapesPlaceheld > 0, r.Issues.Any(i => i.Kind == ImportIssueKind.Placeholder));
        }
    }

    [Fact]
    public void ShapesAccountedFor_EqualTheFilesPageLists()
    {
        // AC8, counted independently of the importer's own bookkeeping: every shape reference in the file's page
        // lists (a content page's own, plus its master's, per placement) is either read as a drawing or names a
        // shape with no drawing; each such missing reference is reported (once per page list that names it).
        foreach (var file in Directory.GetFiles(Fixtures.Dir, "*.pub"))
        {
            var pkg = PubPackage.Open(file);
            var contents = Contents.ContentsReader.Read(pkg.Contents);
            var drawn = OfficeArt.EscherReader.ReadShapes(pkg.EscherStm).Where(s => s.Seqnum is not null).Select(s => s.Seqnum!.Value).ToHashSet();
            var pages = contents.Pages.ToDictionary(p => p.Seqnum);
            var lists = contents.ContentPages.SelectMany(p => p.MasterSeqnum is { } m && pages.TryGetValue(m, out var mp) && mp.IsMaster ? new[] { p, mp } : new[] { p }).ToList();
            var listed = lists.Sum(l => l.ShapeSeqnums.Distinct().Count());
            var missing = lists.Sum(l => l.ShapeSeqnums.Distinct().Count(q => !drawn.Contains(q)));
            var missingPairs = lists.SelectMany(l => l.ShapeSeqnums.Distinct().Where(q => !drawn.Contains(q)).Select(q => (l.Seqnum, q))).Distinct().Count();
            var r = PubImporter.Import(file);
            Assert.True(listed == r.ShapesRead + missing, $"{Path.GetFileName(file)}: {listed} listed, {r.ShapesRead} read, {missing} missing");
            Assert.Equal(missingPairs, r.Issues.Count(i => i.Kind == ImportIssueKind.Dropped && i.Detail.Contains("has no drawing")));
        }
    }

    [Fact]
    public void WriteTo_Again_RemovesOnlyPicturesTheNewImportDoesNotUse()
    {
        var dir = Directory.CreateTempSubdirectory("pubsmith-rewrite-").FullName;
        try
        {
            var pub = Path.Combine(dir, "flyer.pub");
            File.Copy(Fixtures.Pub("picture", "base"), pub);
            var result = PubImporter.Import(pub);
            PubImporter.WriteTo(result, dir, "flyer");
            // As if an earlier import of a different flyer.pub had written a seventh picture, and the user added notes.
            File.Copy(Path.Combine(dir, result.Assets.Keys.Single()), Path.Combine(dir, "flyer.assets", "img7.png"));
            File.WriteAllText(Path.Combine(dir, "flyer.assets", "notes.txt"), "the user's own file");

            PubImporter.WriteTo(result, dir, "flyer");

            var left = Directory.GetFiles(Path.Combine(dir, "flyer.assets")).Select(Path.GetFileName).Order().ToList();
            Assert.Equal([.. result.Assets.Keys.Select(Path.GetFileName).Order(), "notes.txt"], left);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void WriteTo_LeftoverPictureThatCannotBeDeleted_DoesNotFailTheImport()
    {
        var dir = Directory.CreateTempSubdirectory("pubsmith-locked-").FullName;
        var leftover = Path.Combine(dir, "flyer.assets", "img7.png");
        try
        {
            var pub = Path.Combine(dir, "flyer.pub");
            File.Copy(Fixtures.Pub("picture", "base"), pub);
            var result = PubImporter.Import(pub);
            PubImporter.WriteTo(result, dir, "flyer");
            File.Copy(Path.Combine(dir, result.Assets.Keys.Single()), leftover);
            File.SetAttributes(leftover, FileAttributes.ReadOnly);   // cannot be deleted

            var json = PubImporter.WriteTo(result, dir, "flyer");

            Assert.True(File.Exists(json));
            Assert.True(File.Exists(leftover));   // left for a later run
        }
        finally
        {
            if (File.Exists(leftover)) File.SetAttributes(leftover, FileAttributes.Normal);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void WriteTo_ThatFailsOnAPicture_WritesNoDocument()
    {
        // The document is staged first and moved into place last: when a picture cannot be written (its folder name
        // is taken by a file), no document refers to missing pictures, and the staged one is removed.
        var dir = Directory.CreateTempSubdirectory("pubsmith-fail-").FullName;
        try
        {
            var pub = Path.Combine(dir, "flyer.pub");
            File.Copy(Fixtures.Pub("picture", "base"), pub);
            File.WriteAllText(Path.Combine(dir, "flyer.assets"), "a file where the picture folder would go");
            Assert.ThrowsAny<IOException>(() => PubImporter.WriteTo(PubImporter.Import(pub), dir, "flyer"));
            Assert.False(File.Exists(Path.Combine(dir, "flyer.json")));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void WriteTo_ThatFailsOnTheReport_KeepsTheEarlierDocument()
    {
        // A re-import whose report cannot be written (its name is taken by a folder) must not have replaced the
        // earlier document, nor leave the staged one behind.
        var dir = Directory.CreateTempSubdirectory("pubsmith-fail-").FullName;
        try
        {
            var json = PubImporter.WriteTo(Import("picture", "base"), dir, "flyer");
            var earlier = File.ReadAllBytes(json);
            File.Delete(Path.Combine(dir, "flyer.import.json"));
            Directory.CreateDirectory(Path.Combine(dir, "flyer.import.json"));

            var ex = Record.Exception(() => PubImporter.WriteTo(Import("text", "base"), dir, "flyer"));

            Assert.True(ex is IOException or UnauthorizedAccessException, $"unexpected {ex}");
            Assert.Equal(earlier, File.ReadAllBytes(json));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void WriteTo_ProducesALoadableDocumentWithItsPictures()
    {
        var dir = Directory.CreateTempSubdirectory("pubsmith-import-").FullName;
        try
        {
            var r = Import("picture", "two");
            var json = PubImporter.WriteTo(r, dir, "two");
            var doc = DocumentJson.Load(json);
            foreach (var img in doc.Pages.SelectMany(p => p.Elements).OfType<ImageElement>())
                Assert.True(File.Exists(Path.Combine(dir, img.Source)), img.Source);
            Assert.True(File.Exists(Path.Combine(dir, "two.import.json")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
