using Pubsmith.Cli;
using Pubsmith.Core;
using SkiaSharp;

namespace Pubsmith.Cli.Tests;

// AC9: pubsmith render <doc.json> [--pdf <path>] [--png <path>] [--dpi <n>]
public sealed class CliTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pubsmith-cli-").FullName;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private int Run(params string[] args) => CliApp.Run(args, _out, _err);

    private string WriteDoc(PubsmithDocument doc, string name = "doc.json")
    {
        var path = Path.Combine(_dir, name);
        DocumentJson.Save(doc, path);
        return path;
    }

    private static PubsmithDocument OnePage(params Element[] elements) =>
        new() { Pages = [new Page(144, 72) { Elements = elements }] };

    [Fact]
    public void Render_WritesPdfAndPng_ExitZero()
    {
        var doc = WriteDoc(OnePage(new ShapeElement { Bounds = new Box(0, 0, 144, 72), Fill = new Rgba(0, 0, 255) }));
        var pdf = Path.Combine(_dir, "out.pdf");
        var png = Path.Combine(_dir, "out.png");

        var code = Run("render", doc, "--pdf", pdf, "--png", png, "--dpi", "150");

        Assert.Equal(0, code);
        Assert.StartsWith("%PDF", File.ReadAllText(pdf)[..4]);
        using var bmp = SKBitmap.Decode(png);
        Assert.Equal(300, bmp.Width);
        Assert.Equal(150, bmp.Height);
        Assert.Contains(pdf, _out.ToString());
        Assert.Contains(png, _out.ToString());
        Assert.Equal("", _err.ToString());
    }

    [Fact]
    public void MultiPagePng_WritesOneFilePerPage()
    {
        var doc = WriteDoc(new PubsmithDocument { Pages = [new Page(72, 72), new Page(72, 72)] });
        var png = Path.Combine(_dir, "sheet.png");

        Assert.Equal(0, Run("render", doc, "--png", png));

        Assert.True(File.Exists(Path.Combine(_dir, "sheet-p1.png")));
        Assert.True(File.Exists(Path.Combine(_dir, "sheet-p2.png")));
        Assert.False(File.Exists(png));
    }

    [Fact]
    public void RelativeImagePaths_ResolveAgainstDocumentFolder()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "art"));
        using (var bmp = new SKBitmap(10, 10))
        {
            bmp.Erase(SKColors.Red);
            using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(_dir, "art", "red.png"), data.ToArray());
        }
        var doc = WriteDoc(OnePage(new ImageElement { Bounds = new Box(0, 0, 144, 72), Source = "art/red.png" }));
        var png = Path.Combine(_dir, "img.png");

        Assert.Equal(0, Run("render", doc, "--png", png));

        Assert.Equal("", _err.ToString());   // no image-missing warning
        using var outBmp = SKBitmap.Decode(png);
        Assert.Equal(SKColors.Red, outBmp.GetPixel(72, 36));
    }

    [Fact]
    public void Warnings_GoToStderr_ExitStillZero()
    {
        var doc = WriteDoc(OnePage(new ImageElement { Bounds = new Box(0, 0, 10, 10), Source = "missing.png" }));

        var code = Run("render", doc, "--png", Path.Combine(_dir, "w.png"));

        Assert.Equal(0, code);
        Assert.Contains("warning", _err.ToString());
        Assert.Contains("missing.png", _err.ToString());
    }

    [Theory]
    [InlineData]
    [InlineData("render")]
    [InlineData("print", "x.json", "--pdf", "x.pdf")]
    public void MissingArgsOrUnknownCommand_ExitTwo(params string[] args)
    {
        Assert.Equal(2, Run(args));
        Assert.Contains("usage", _err.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoOutputRequested_ExitTwo()
    {
        var doc = WriteDoc(OnePage());
        Assert.Equal(2, Run("render", doc));
        Assert.Contains("--pdf", _err.ToString());
    }

    [Theory]
    [InlineData("--dpi", "abc")]
    [InlineData("--dpi", "0")]
    [InlineData("--dpi", "99999")]
    [InlineData("--frobnicate", "1")]
    [InlineData("--pdf")]
    public void BadOption_ExitTwo(params string[] option)
    {
        var doc = WriteDoc(OnePage());
        var args = new[] { "render", doc, "--png", Path.Combine(_dir, "o.png") }.Concat(option).ToArray();
        Assert.Equal(2, Run(args));
    }

    [Fact]
    public void DecimalDpi_IsCultureInvariant()
    {
        var doc = WriteDoc(OnePage());
        var png = Path.Combine(_dir, "d.png");
        var saved = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try { Assert.Equal(0, Run("render", doc, "--png", png, "--dpi", "144.0")); }
        finally { Thread.CurrentThread.CurrentCulture = saved; }
        using var bmp = SKBitmap.Decode(png);
        Assert.Equal(288, bmp.Width);
    }

    [Fact]
    public void InvalidJson_ExitOne()
    {
        var path = Path.Combine(_dir, "bad.json");
        File.WriteAllText(path, "{ not json");
        Assert.Equal(1, Run("render", path, "--pdf", Path.Combine(_dir, "x.pdf")));
        Assert.Contains("not valid JSON", _err.ToString());
        Assert.False(File.Exists(Path.Combine(_dir, "x.pdf")));
    }

    [Fact]
    public void MissingFile_ExitOne()
    {
        var path = Path.Combine(_dir, "nope.json");
        Assert.Equal(1, Run("render", path, "--pdf", Path.Combine(_dir, "x.pdf")));
        Assert.Contains("nope.json", _err.ToString());
    }

    [Fact]
    public void RealExecutable_WiresArgsAndExitCode()
    {
        // Runs the built pubsmith entry point (Program.cs), not CliApp directly.
        var dll = Path.Combine(AppContext.BaseDirectory, "pubsmith.dll");
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"\"{dll}\" render")
        {
            RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        Assert.Equal(2, p.ExitCode);
        Assert.Contains("usage", err);
    }

    [Fact]
    public void EmptyDocument_ExitOne()
    {
        var doc = WriteDoc(new PubsmithDocument());
        Assert.Equal(1, Run("render", doc, "--pdf", Path.Combine(_dir, "x.pdf")));
        Assert.Contains("no pages", _err.ToString());
    }

    [Theory]
    [InlineData(1e7, 1e7, "--png")]   // too large to rasterise
    [InlineData(0, 0, "--pdf")]       // no size at all
    public void RenderFailure_ExitsOne_AndLeavesNoFile(double w, double h, string format)
    {
        var doc = WriteDoc(new PubsmithDocument { Pages = [new Page(w, h)] });
        Assert.Equal(1, Run("render", doc, format, Path.Combine(_dir, "out")));
        Assert.Contains("error:", _err.ToString());
        Assert.Equal([doc], Directory.GetFiles(_dir));
    }

    [Fact]
    public void MissingOutputFolder_ExitOne_NamingTheFolder()
    {
        var doc = WriteDoc(OnePage(new ShapeElement { Bounds = new Box(0, 0, 10, 10), Fill = new Rgba(0, 0, 0) }));
        var folder = Path.Combine(_dir, "no-such-folder");
        Assert.Equal(1, Run("render", doc, "--pdf", Path.Combine(folder, "x.pdf")));
        Assert.Contains($"The output folder '{folder}' does not exist.", _err.ToString());
        Assert.DoesNotContain(".tmp", _err.ToString());
    }

    [Fact]
    public void RunWithoutStyle_IsAnInvalidDocument_ExitOne()
    {
        // A hand-written document missing a required value must be rejected, not crash the renderer.
        var path = Path.Combine(_dir, "nostyle.json");
        File.WriteAllText(path, """
            { "schemaVersion": 1, "pages": [ { "width": 100, "height": 100, "elements": [
              { "type": "text", "bounds": { "x": 0, "y": 0, "width": 100, "height": 100 },
                "paragraphs": [ { "runs": [ { "text": "hi" } ] } ] } ] } ] }
            """);
        Assert.Equal(1, Run("render", path, "--png", Path.Combine(_dir, "x.png")));
        Assert.Contains("error: The document is invalid", _err.ToString());   // rejected on load, not a crash while drawing
        Assert.Contains("style", _err.ToString());
    }

    // ---------- .pub input ----------

    private string CopyPub(string fixture, string name)
    {
        var path = Path.Combine(_dir, name);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "pub", fixture), path);
        return path;
    }

    [Fact]
    public void Import_WritesDocumentPicturesAndReport()
    {
        var pub = CopyPub("picture__base.pub", "flyer.pub");
        var outDir = Path.Combine(_dir, "rescued");

        Assert.Equal(0, Run("import", pub, "--out", outDir));

        var doc = DocumentJson.Load(Path.Combine(outDir, "flyer.json"));
        var img = Assert.Single(doc.Pages[0].Elements.OfType<ImageElement>());
        Assert.True(File.Exists(Path.Combine(outDir, img.Source)));
        Assert.True(File.Exists(Path.Combine(outDir, "flyer.import.json")));
        Assert.Contains("wrote", _out.ToString());
        Assert.Contains("1 page", _out.ToString());
    }

    [Fact]
    public void Import_IntoAPathThatIsAFile_IsNotCalledADamagedFile()
    {
        // The output folder cannot be made (a file has its name): a fault of the machine, not of the .pub.
        var pub = CopyPub("text__base.pub", "note.pub");
        var taken = Path.Combine(_dir, "taken");
        File.WriteAllText(taken, "someone else's file");

        Assert.Equal(1, Run("import", pub, "--out", taken));

        Assert.StartsWith("error: ", _err.ToString());
        Assert.DoesNotContain("is damaged", _err.ToString());
        Assert.Equal("someone else's file", File.ReadAllText(taken));
    }

    [Fact]
    public void Import_WithoutOut_WritesToANewFolderBesideThePub()
    {
        var pub = CopyPub("text__base.pub", "note.pub");
        Assert.Equal(0, Run("import", pub));
        Assert.True(File.Exists(Path.Combine(_dir, "note-pubsmith", "note.json")));
    }

    // `render file.pub` directly (and that it leaves no temporary file): RenderScratchTests.

    [Fact]
    public void NotAPub_ExitOne()
    {
        var bad = Path.Combine(_dir, "bad.pub");
        File.WriteAllText(bad, "not a compound file");
        Assert.Equal(1, Run("import", bad, "--out", Path.Combine(_dir, "o")));
        Assert.Equal(1, Run("render", bad, "--pdf", Path.Combine(_dir, "o.pdf")));
        Assert.Contains("bad.pub", _err.ToString());
        Assert.Equal([bad], Directory.GetFileSystemEntries(_dir));
    }

    [Theory]
    [InlineData("import")]
    [InlineData("import", "a.pub", "--out")]
    [InlineData("import", "a.pub", "--pdf", "x.pdf")]
    public void ImportUsage_ExitTwo(params string[] args) => Assert.Equal(2, Run(args));
}
