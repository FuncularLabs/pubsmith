using Pubsmith.Core;

namespace Pubsmith.Core.Tests;

public class DocumentJsonTests
{
    internal static PubsmithDocument SampleWithEveryElementKind() => new()
    {
        Name = "sample",
        Pages =
        [
            new Page(414, 342)
            {
                Elements =
                [
                    new ShapeElement
                    {
                        Bounds = new Box(18, 18, 378, 306), Rotation = 15, Kind = ShapeKind.Ellipse,
                        Shadow = new Shadow(new Rgba(0, 0, 0, 128), 2, -1.5),
                        Fill = new Rgba(200, 0, 0), Stroke = new Stroke(new Rgba(0, 0, 0, 128), 2),
                    },
                    new ImageElement
                    {
                        Bounds = new Box(54, 54, 216, 216), Source = "assets/sample-picture.png",
                        Crop = new Crop(0.1, 0.2, 0.05, 0), Stroke = new Stroke(new Rgba(255, 255, 255), 4),
                    },
                    new TextElement
                    {
                        Bounds = new Box(107, 150, 200, 40), Rotation = 270,
                        Insets = new Insets(1, 2, 3, 4), VerticalAlign = TextVerticalAlign.Bottom,
                        Fit = TextFit.Stretch, Warp = new TextWarp(WarpKind.CanDown, 0.2),
                        Paragraphs =
                        [
                            new Paragraph
                            {
                                Alignment = TextAlignment.Center, LineSpacing = 1.5, ExactLineSpacing = 14, SpaceBefore = 3, SpaceAfter = 9,
                                Runs =
                                [
                                    new TextRun("Plain ", new TextStyle()),
                                    new TextRun("Bold red", new TextStyle { Family = "Arial", Size = 14, Bold = true, Italic = true, AllCaps = true, Color = new Rgba(170, 20, 20) }),
                                ],
                            },
                        ],
                    },
                ],
            },
            new Page(612, 792),
        ],
    };

    [Fact]
    public void RoundTrip_AllElementKinds_IsLossless()
    {
        var original = SampleWithEveryElementKind();

        var json = DocumentJson.Serialize(original);
        var loaded = DocumentJson.Deserialize(json);

        // Re-serialising must reproduce the exact text: any property the reader drops or alters shows up here.
        Assert.Equal(json, DocumentJson.Serialize(loaded));
        Assert.Equal(PubsmithDocument.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(2, loaded.Pages.Count);
        var elements = loaded.Pages[0].Elements;
        Assert.IsType<ShapeElement>(elements[0]);
        var image = Assert.IsType<ImageElement>(elements[1]);
        Assert.Equal(new Crop(0.1, 0.2, 0.05, 0), image.Crop);
        var text = Assert.IsType<TextElement>(elements[2]);
        Assert.Equal(270, text.Rotation);
        Assert.Equal(TextVerticalAlign.Bottom, text.VerticalAlign);
        Assert.Equal(new TextWarp(WarpKind.CanDown, 0.2), text.Warp);
        Assert.Equal(new Shadow(new Rgba(0, 0, 0, 128), 2, -1.5), elements[0].Shadow);
        Assert.Equal(14, text.Paragraphs[0].ExactLineSpacing);
        Assert.Equal("Bold red", text.Paragraphs[0].Runs[1].Text);
        Assert.True(text.Paragraphs[0].Runs[1].Style.Bold);
        Assert.True(text.Paragraphs[0].Runs[1].Style.AllCaps);
    }

    [Fact]
    public void Serialize_UsesTypeDiscriminatorAndHexColours()
    {
        var json = DocumentJson.Serialize(SampleWithEveryElementKind());

        Assert.Contains("\"type\": \"shape\"", json);
        Assert.Contains("\"type\": \"image\"", json);
        Assert.Contains("\"type\": \"text\"", json);
        Assert.Contains("\"#C80000\"", json);
        Assert.Contains("\"#00000080\"", json);
        Assert.Contains("\"ellipse\"", json);
    }

    [Fact]
    public void Load_UnsupportedSchemaVersion_ThrowsNamingVersion()
    {
        var json = DocumentJson.Serialize(SampleWithEveryElementKind()).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 7");

        var ex = Assert.Throws<DocumentFormatException>(() => DocumentJson.Deserialize(json));
        Assert.Contains("7", ex.Message);
    }

    [Fact]
    public void Load_MissingSchemaVersion_IsRejected()
    {
        var ex = Assert.Throws<DocumentFormatException>(() => DocumentJson.Deserialize("{\"name\":\"x\",\"pages\":[]}"));
        Assert.Contains("schemaVersion", ex.Message);
    }

    [Theory]
    [InlineData("{\"text\":\"hi\"}", "style")]                                           // a run without its style
    [InlineData("{\"style\":{\"family\":\"Arial\",\"size\":10}}", "text")]            // a run without its text
    public void MissingRequiredValue_IsRejected_NamingIt(string run, string missing)
    {
        var json = "{\"schemaVersion\":1,\"pages\":[{\"width\":100,\"height\":100,\"elements\":[{\"type\":\"text\"," +
                   "\"bounds\":{\"x\":0,\"y\":0,\"width\":10,\"height\":10},\"paragraphs\":[{\"runs\":[" + run + "]}]}]}]}";
        var ex = Assert.Throws<DocumentFormatException>(() => DocumentJson.Deserialize(json));
        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public void PageWithoutASize_IsRejected() =>
        Assert.Throws<DocumentFormatException>(() => DocumentJson.Deserialize("{\"schemaVersion\":1,\"pages\":[{\"elements\":[]}]}"));

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\":1,\"pages\":[{\"width\":10,\"height\":10,\"elements\":[{\"type\":\"blob\"}]}]}")]
    public void Load_Malformed_ThrowsDocumentFormatException(string json)
    {
        Assert.Throws<DocumentFormatException>(() => DocumentJson.Deserialize(json));
    }

    [Theory]
    [InlineData(-0.1, 0, 0, 0)]
    [InlineData(0, 1.2, 0, 0)]
    [InlineData(0.6, 0, 0.5, 0)]   // left + right removes the whole image
    [InlineData(0, 0.5, 0, 0.5)]   // top + bottom removes the whole image
    public void Crop_OutOfRange_IsRejected(double l, double t, double r, double b)
    {
        Assert.ThrowsAny<ArgumentException>(() => new Crop(l, t, r, b));
        var json = "{\"schemaVersion\":1,\"pages\":[{\"width\":10,\"height\":10,\"elements\":[{\"type\":\"image\",\"source\":\"a.png\",\"crop\":{" +
                   FormattableString.Invariant($"\"left\":{l},\"top\":{t},\"right\":{r},\"bottom\":{b}") + "}}]}]}";
        Assert.Throws<DocumentFormatException>(() => DocumentJson.Deserialize(json));
    }

    [Fact]
    public void Color_SerializesAsHex_AndParsesWithAndWithoutAlpha()
    {
        Assert.Equal("#5B9BD5", new Rgba(0x5B, 0x9B, 0xD5).ToString());
        Assert.Equal("#5B9BD580", new Rgba(0x5B, 0x9B, 0xD5, 0x80).ToString());
        Assert.Equal(new Rgba(0x5B, 0x9B, 0xD5), Rgba.Parse("#5b9bd5"));
        Assert.Equal(new Rgba(0x5B, 0x9B, 0xD5, 0x80), Rgba.Parse("#5B9BD580"));
        Assert.Throws<FormatException>(() => Rgba.Parse("5B9BD5"));
        Assert.Throws<FormatException>(() => Rgba.Parse("#12345"));
        Assert.Throws<FormatException>(() => Rgba.Parse("#GGGGGG"));
    }

    [Fact]
    public void SaveAndLoad_File_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pubsmith-{Guid.NewGuid():N}.json");
        try
        {
            var doc = SampleWithEveryElementKind();
            DocumentJson.Save(doc, path);
            Assert.Equal(DocumentJson.Serialize(doc), DocumentJson.Serialize(DocumentJson.Load(path)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Save_ThatFails_LeavesNoTemporaryFile()
    {
        // The target is an existing directory, so the final rename fails after the temporary file was written.
        var dir = Directory.CreateTempSubdirectory("pubsmith-save-").FullName;
        try
        {
            var target = Directory.CreateDirectory(Path.Combine(dir, "doc.json")).FullName;
            var ex = Record.Exception(() => DocumentJson.Save(SampleWithEveryElementKind(), target));
            Assert.True(ex is IOException or UnauthorizedAccessException, $"unexpected {ex}");
            Assert.Equal([target], Directory.GetFileSystemEntries(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Save_DoesNotClobberAnUnrelatedTmpFile()
    {
        var dir = Directory.CreateTempSubdirectory("pubsmith-save-").FullName;
        try
        {
            var path = Path.Combine(dir, "doc.json");
            File.WriteAllText(path + ".tmp", "someone else's file");
            DocumentJson.Save(SampleWithEveryElementKind(), path);
            Assert.Equal("someone else's file", File.ReadAllText(path + ".tmp"));
            Assert.Equal(2, Directory.GetFiles(dir).Length);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Save_WritesExactlyWhatSerializeReturns()
    {
        var dir = Directory.CreateTempSubdirectory("pubsmith-save-").FullName;
        try
        {
            var doc = SampleWithEveryElementKind();
            var path = Path.Combine(dir, "doc.json");
            DocumentJson.Save(doc, path);
            Assert.Equal(new System.Text.UTF8Encoding(false).GetBytes(DocumentJson.Serialize(doc)), File.ReadAllBytes(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Save_StreamsTheDocument()
    {
        // About 25 MB of JSON: saved as a stream, not built as one string first, so saving allocates a small fraction.
        var runs = Enumerable.Range(0, 100_000).Select(_ => new TextRun("x", new TextStyle())).ToList();
        var doc = new PubsmithDocument
        {
            Name = "big",
            Pages = [new Page(612, 792) { Elements = [new TextElement { Bounds = new Box(0, 0, 612, 792), Paragraphs = [new Paragraph { Runs = runs }] }] }],
        };
        var dir = Directory.CreateTempSubdirectory("pubsmith-save-").FullName;
        try
        {
            var path = Path.Combine(dir, "doc.json");
            var before = GC.GetAllocatedBytesForCurrentThread();
            DocumentJson.Save(doc, path);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            var written = new FileInfo(path).Length;
            Assert.True(written > 20L << 20, $"only {written >> 20} MB written");
            Assert.True(allocated < written / 4, $"allocated {allocated >> 20} MB to write {written >> 20} MB");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Save_LargerThanItsLimit_IsRefused_AndKeepsTheEarlierDocument()
    {
        var dir = Directory.CreateTempSubdirectory("pubsmith-save-").FullName;
        try
        {
            var path = Path.Combine(dir, "doc.json");
            DocumentJson.Save(new PubsmithDocument { Name = "earlier", Pages = [new Page(612, 792)] }, path);
            var earlier = File.ReadAllBytes(path);

            var ex = Assert.Throws<DocumentTooLargeException>(() => DocumentJson.Save(SampleWithEveryElementKind(), path, 1000));

            Assert.Equal(1000, ex.Limit);
            Assert.Equal(earlier, File.ReadAllBytes(path));
            Assert.Equal([path], Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Save_AtItsLimit_IsSaved()
    {
        var dir = Directory.CreateTempSubdirectory("pubsmith-save-").FullName;
        try
        {
            var doc = SampleWithEveryElementKind();
            var bytes = new System.Text.UTF8Encoding(false).GetBytes(DocumentJson.Serialize(doc));
            var path = Path.Combine(dir, "doc.json");
            DocumentJson.Save(doc, path, bytes.Length);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Throws<DocumentTooLargeException>(() => DocumentJson.Save(doc, path, bytes.Length - 1));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Save_WithANegativeLimit_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pubsmith-{Guid.NewGuid():N}.json");
        Assert.Throws<ArgumentOutOfRangeException>(() => DocumentJson.Save(SampleWithEveryElementKind(), path, -1));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Stage_WritesBesideThePath_AndOnlyCommitReplacesIt()
    {
        var dir = Directory.CreateTempSubdirectory("pubsmith-save-").FullName;
        try
        {
            var path = Path.Combine(dir, "doc.json");
            File.WriteAllText(path, "earlier");
            var doc = SampleWithEveryElementKind();

            using (var staged = DocumentJson.Stage(doc, path, long.MaxValue))
            {
                Assert.Equal("earlier", File.ReadAllText(path));   // staged, not in place
                Assert.Equal(2, Directory.GetFiles(dir).Length);
                staged.Commit();
                Assert.Equal(DocumentJson.Serialize(doc), File.ReadAllText(path));
                Assert.Equal([path], Directory.GetFiles(dir));
                Assert.Throws<ObjectDisposedException>(() => staged.Commit());   // once only
            }
            Assert.Equal(DocumentJson.Serialize(doc), File.ReadAllText(path));   // disposing after the commit changes nothing
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Stage_DisposedUncommitted_LeavesThePathAsItWas()
    {
        var dir = Directory.CreateTempSubdirectory("pubsmith-save-").FullName;
        try
        {
            var path = Path.Combine(dir, "doc.json");
            File.WriteAllText(path, "earlier");
            var staged = DocumentJson.Stage(SampleWithEveryElementKind(), path, long.MaxValue);
            staged.Dispose();
            Assert.Equal("earlier", File.ReadAllText(path));
            Assert.Equal([path], Directory.GetFiles(dir));
            Assert.Throws<ObjectDisposedException>(() => staged.Commit());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Stage_ACommitWhoseMoveFails_KeepsTheEarlierDocument()
    {
        // The staged file is gone (as if removed under us), so the move fails: the earlier document must not have
        // been removed first. The replace is one move, never a delete followed by a move.
        var dir = Directory.CreateTempSubdirectory("pubsmith-save-").FullName;
        try
        {
            var path = Path.Combine(dir, "doc.json");
            File.WriteAllText(path, "earlier");
            using var staged = DocumentJson.Stage(SampleWithEveryElementKind(), path, long.MaxValue);
            File.Delete(Assert.Single(Directory.GetFiles(dir, "*.tmp")));

            Assert.ThrowsAny<IOException>(() => staged.Commit());

            Assert.Equal("earlier", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LimitedStream_IsWriteOnly_AndRefusesAWritePastItsLimit()
    {
        var inner = new MemoryStream();
        var s = new LimitedStream(inner, 5);
        Assert.False(s.CanRead);
        Assert.False(s.CanSeek);
        Assert.True(s.CanWrite);
        Assert.Throws<NotSupportedException>(() => s.Length);
        Assert.Throws<NotSupportedException>(() => s.Position);
        Assert.Throws<NotSupportedException>(() => s.Position = 0);
        Assert.Throws<NotSupportedException>(() => s.Read(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => s.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => s.SetLength(0));

        s.Write([1, 2, 3], 0, 3);
        s.Write([4, 5]);
        s.Flush();
        Assert.Throws<DocumentTooLargeException>(() => s.Write([6], 0, 1));   // the limit is reached: nothing more is written
        Assert.Equal([1, 2, 3, 4, 5], inner.ToArray());

        s.Dispose();
        Assert.False(inner.CanWrite);   // disposing it closes the file under it
    }

    [Fact]
    public void Save_ThatFailsPartWay_KeepsTheEarlierDocument()
    {
        // About 5 MB of pages, then one the serializer refuses (NaN) only when it reaches it: the earlier document must
        // survive a failure part-way through writing, as it would a full disk.
        var dir = Directory.CreateTempSubdirectory("pubsmith-save-").FullName;
        try
        {
            var path = Path.Combine(dir, "doc.json");
            DocumentJson.Save(new PubsmithDocument { Name = "earlier", Pages = [new Page(612, 792)] }, path);
            var earlier = File.ReadAllBytes(path);
            var pages = Enumerable.Range(0, 20_000).Select(_ => new Page(612, 792) { Elements = [new ShapeElement { Bounds = new Box(1, 2, 3, 4) }] }).ToList();
            pages.Add(new Page(double.NaN, 792));

            Assert.NotNull(Record.Exception(() => DocumentJson.Save(new PubsmithDocument { Name = "bad", Pages = pages }, path)));

            Assert.Equal(earlier, File.ReadAllBytes(path));
            Assert.Equal([path], Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Load_MissingFile_ThrowsDocumentFormatExceptionNamingPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pubsmith-missing-{Guid.NewGuid():N}.json");
        var ex = Assert.Throws<DocumentFormatException>(() => DocumentJson.Load(path));
        Assert.Contains(path, ex.Message);
    }

    [Fact]
    public void Defaults_MatchPublisherTextBoxDefaults()
    {
        var p = new Paragraph();
        Assert.Equal(1.19, p.LineSpacing);
        Assert.Equal(6, p.SpaceAfter);
        Assert.Equal(new Insets(2.88, 2.88, 2.88, 2.88), new TextElement().Insets);
        Assert.Equal("Calibri", new TextStyle().Family);
        Assert.Equal(10, new TextStyle().Size);
        Assert.Equal(new Rgba(0, 0, 0), new TextStyle().Color);
    }
}
