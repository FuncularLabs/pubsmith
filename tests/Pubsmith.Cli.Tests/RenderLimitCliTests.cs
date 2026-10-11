using Pubsmith.Cli;
using Pubsmith.Core;
using Pubsmith.Rendering;

namespace Pubsmith.Cli.Tests;

// WI-006 AC1, AC4, AC7: the CLI checks the whole command before writing; writes every output to a temporary file;
// then commits, putting every output back if any move fails; a refused, stopped or failed command leaves every earlier
// output byte for byte as it was, and no temporary or backup of its own.
public sealed class RenderLimitCliTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pubsmith-cli-limits-").FullName;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly RenderLimits Open = RenderLimits.Default with
    {
        MaxPages = int.MaxValue, MaxPngPixels = long.MaxValue, MaxPdfTypefaces = int.MaxValue, MaxFontNames = int.MaxValue,
        MaxTextBoxCharacters = int.MaxValue, MaxTextBoxRuns = int.MaxValue, MaxWordArtGlyphs = int.MaxValue,
        MaxDrawTime = TimeSpan.MaxValue, MaxPdfBytes = long.MaxValue, MaxPngBytes = long.MaxValue,
    };

    /// <summary>The real file operations, failing the moves or deletes the test names, and recording every call.</summary>
    private sealed class Files(Func<string, string, bool>? failMove = null, Func<string, bool>? failDelete = null, bool accessDenied = false, Func<string, string, bool>? denyMove = null, Func<string, bool>? denyDelete = null) : ICliFiles
    {
        public List<string> Calls { get; } = [];
        public void Move(string from, string to)
        {
            Calls.Add($"move {Path.GetFileName(from)} -> {Path.GetFileName(to)}");
            if (failMove?.Invoke(from, to) == true)
                throw accessDenied ? new UnauthorizedAccessException($"access to {Path.GetFileName(to)} is denied") : new IOException($"cannot move {Path.GetFileName(from)}");
            if (denyMove?.Invoke(from, to) == true) throw new UnauthorizedAccessException($"access to {Path.GetFileName(to)} is denied");
            File.Move(from, to);
        }
        public void Delete(string path)
        {
            Calls.Add($"delete {Path.GetFileName(path)}");
            if (failDelete?.Invoke(path) == true) throw new IOException($"cannot delete {Path.GetFileName(path)}");
            if (denyDelete?.Invoke(path) == true) throw new UnauthorizedAccessException($"access to {Path.GetFileName(path)} is denied");
            File.Delete(path);
        }
    }

    private sealed class StepClock : TimeProvider
    {
        private long _now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _now += TimeSpan.TicksPerSecond;
    }

    private int Run(RenderLimits limits, ICliFiles files, TimeProvider? clock = null, long maxLoadBytes = DocumentJson.MaxLoadBytes, params string[] args) =>
        CliApp.Run(args, _out, _err, limits, clock ?? TimeProvider.System, maxLoadBytes, files);

    private string Doc(int pages, int shapes = 1)
    {
        var path = Path.Combine(_dir, "doc.json");
        DocumentJson.Save(new PubsmithDocument
        {
            Pages = Enumerable.Range(0, pages).Select(_ => new Page(72, 72) { Elements = Enumerable.Range(0, shapes).Select(i => (Element)new ShapeElement { Bounds = new Box(i % 7 * 10, 0, 9, 9), Fill = new Rgba(0, 0, 200) }).ToList() }).ToList(),
        }, path);
        return path;
    }

    private string Out(string name) => Path.Combine(_dir, name);

    // Three earlier outputs (a PDF and two PNG pages), each with its own bytes.
    private Dictionary<string, byte[]> Earlier()
    {
        var earlier = new Dictionary<string, byte[]> { [Out("out.pdf")] = [1, 2, 3], [Out("out-p1.png")] = [4, 5], [Out("out-p2.png")] = [6] };
        foreach (var (path, bytes) in earlier) File.WriteAllBytes(path, bytes);
        return earlier;
    }

    private void AssertUnchanged(Dictionary<string, byte[]> earlier)
    {
        foreach (var (path, bytes) in earlier) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.Empty(Directory.GetFiles(_dir, "*.backup"));
    }

    private string[] Render(string doc) => ["render", doc, "--pdf", Out("out.pdf"), "--png", Out("out.png"), "--dpi", "72"];

    [Fact]
    public void ARefusedCommand_WritesNothing()
    {
        var earlier = Earlier();
        var files = new Files();
        // Two pages, PDF and PNG: 4 pages written; 3 allowed.
        Assert.Equal(CliApp.Failed, Run(Open with { MaxPages = 3 }, files, args: Render(Doc(2))));
        Assert.Contains(RenderLimitException.Pages, _err.ToString());
        AssertUnchanged(earlier);
        Assert.Empty(files.Calls);
    }

    [Fact]
    public void AnOverLimitCommand_IsRefusedBeforeItsOutputsArePlanned()
    {
        // The counted check comes before the outputs are named and their folders checked, work that grows with the pages
        // (round 9: planning a million tiny pages to PNG took 9 minutes with the folder given in 8.3 short form). So a
        // document over the page limit, to a folder that does not exist, is refused by the limit, not by the folder.
        var files = new Files();
        var missing = Path.Combine(_dir, "no-such-folder");
        Assert.Equal(CliApp.Failed, Run(Open with { MaxPages = 2 }, files, args: ["render", Doc(3), "--png", Path.Combine(missing, "out.png"), "--dpi", "72"]));
        Assert.Contains(RenderLimitException.Pages, _err.ToString());
        Assert.DoesNotContain("does not exist", _err.ToString());
        Assert.Empty(files.Calls);
    }

    [Theory]
    [InlineData("--png", ".png")]
    [InlineData("--pdf", ".pdf")]
    public void AnOverLimitCommand_IsRefusedBeforeItsOutputsAreNamed(string option, string extension)
    {
        // Naming an output by its full path is the planning's first step, and a path holding a NUL character fails it.
        // So a refusal by the limit, with no word of the path, shows that no output was named before the counted check
        // (round 10: naming every output first, then checking, then comparing the paths, passed the test above).
        var output = Out("a\0b" + extension);
        var naming = Assert.Throws<ArgumentException>(() => Path.GetFullPath(output));
        var files = new Files();
        Assert.Equal(CliApp.Failed, Run(Open with { MaxPages = 2 }, files, args: ["render", Doc(3), option, output]));
        Assert.Contains(RenderLimitException.Pages, _err.ToString());
        Assert.DoesNotContain(naming.Message, _err.ToString());
        Assert.Empty(files.Calls);
    }

    [Theory]
    [InlineData("--png", ".png")]
    [InlineData("--pdf", ".pdf")]
    public void AnOutputWhoseNamingFails_IsReported_WhenTheCommandIsWithinTheLimits(string option, string extension)
    {
        // What the test above must not see: the same path, in a command the check lets through, fails as it is named.
        var output = Out("a\0b" + extension);
        var naming = Assert.Throws<ArgumentException>(() => Path.GetFullPath(output));
        var files = new Files();
        Assert.Equal(CliApp.Failed, Run(Open, files, args: ["render", Doc(1), option, output]));
        Assert.Contains(naming.Message, _err.ToString());
        Assert.Empty(files.Calls);
    }

    [Fact]
    public void AStoppedCommand_WritesNothing()
    {
        // The drawing time passes its limit while the second PNG page is drawn, after the PDF and the first page were staged.
        var earlier = Earlier();
        var files = new Files();
        // Each page reads the clock at its start, at 4 checkpoints and at its stop: 5 s a page. The PDF and PNG page 1 take
        // 15 s; PNG page 2's third checkpoint passes 17 s.
        Assert.Equal(CliApp.Failed, Run(Open with { MaxDrawTime = TimeSpan.FromSeconds(17) }, files, new StepClock(), args: Render(Doc(2, shapes: 4))));
        Assert.Contains(RenderLimitException.DrawTime, _err.ToString());
        AssertUnchanged(earlier);
        Assert.All(files.Calls, c => Assert.StartsWith("delete ", c));   // its temporaries, and nothing moved
    }

    [Theory]
    [InlineData("out.pdf", "out.pdf", 1)]
    [InlineData("out.pdf", "OUT.PDF", 1)]
    [InlineData("out-p1.png", "out.png", 2)]   // the PDF named as the first of two PNG pages
    public void TwoOutputsWithOnePath_IsAUsageError(string pdf, string png, int pages)
    {
        if (pdf != png && !OperatingSystem.IsWindows() && pdf.ToUpperInvariant() == png.ToUpperInvariant()) return;   // case matters elsewhere
        var files = new Files();
        Assert.Equal(CliApp.Usage, Run(Open, files, args: ["render", Doc(pages), "--pdf", Out(pdf), "--png", Out(png), "--dpi", "72"]));
        Assert.Contains("same path", _err.ToString());
        Assert.Empty(Directory.GetFiles(_dir, "*.p*"));
    }

    [Fact]
    public void AFailedReplacement_PutsEveryOutputBack()
    {
        var earlier = Earlier();
        // The PDF and the first PNG page are moved into place; moving the second PNG page into place fails.
        var files = new Files((from, to) => from.EndsWith(".tmp") && to == Out("out-p2.png"));
        Assert.Equal(CliApp.Failed, Run(Open, files, args: Render(Doc(2))));
        AssertUnchanged(earlier);
        // The undo, in reverse order: what was moved in is deleted, each output put back from its backup.
        var moves = files.Calls.Where(c => c.StartsWith("move ")).ToList();
        var undo = files.Calls.SkipWhile(c => !(c.EndsWith(".tmp -> out-p2.png"))).Skip(1).ToList();
        Assert.Equal(
        [
            "move out-p2.png.backup? -> out-p2.png",
            "delete out-p1.png", "move out-p1.png.backup? -> out-p1.png",
            "delete out.pdf", "move out.pdf.backup? -> out.pdf",
        ], undo.Where(c => !c.EndsWith(".tmp")).Select(c => System.Text.RegularExpressions.Regex.Replace(c, @"\.[0-9a-f]{32}\.backup", ".backup?")).ToList());
        Assert.Contains("cannot move", _err.ToString());
        Assert.NotEmpty(moves);
    }

    [Fact]
    public void AFailedReplacement_WithAccessDenied_PutsEveryOutputBack()
    {
        var earlier = Earlier();
        var files = new Files((from, to) => from.EndsWith(".tmp") && to == Out("out-p2.png"), accessDenied: true);
        Assert.Equal(CliApp.Failed, Run(Open, files, args: Render(Doc(2))));
        AssertUnchanged(earlier);
        Assert.Contains("denied", _err.ToString());
    }

    [Fact]
    public void ANewOutputThatCannotBeRemoved_IsNamed()
    {
        // No earlier outputs: the PDF is moved into place, the second PNG page cannot be, and the PDF cannot be removed.
        var files = new Files((from, to) => from.EndsWith(".tmp") && to == Out("out-p2.png"), failDelete: path => path == Out("out.pdf"));
        Assert.Equal(CliApp.Failed, Run(Open, files, args: Render(Doc(2))));
        Assert.True(File.Exists(Out("out.pdf")));
        Assert.Contains($"could not be removed: {Out("out.pdf")}", _err.ToString());
        Assert.DoesNotContain("every earlier output was put back", _err.ToString());
        Assert.False(File.Exists(Out("out-p1.png")));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void ANewOutputThatCannotBeRemoved_OverAnEarlierOne_KeepsAndNamesTheEarliersBackup()
    {
        // The new PDF is in place and cannot be removed, so the earlier PDF cannot go back over it: its backup is kept.
        Earlier();
        var files = new Files((from, to) => from.EndsWith(".tmp") && to == Out("out-p2.png"), failDelete: path => path == Out("out.pdf"));
        Assert.Equal(CliApp.Failed, Run(Open, files, args: Render(Doc(2))));
        var kept = Assert.Single(Directory.GetFiles(_dir, "out.pdf.*.backup"));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(kept));
        Assert.Contains($"could not be removed: {Out("out.pdf")}", _err.ToString());
        Assert.Contains(kept, _err.ToString());
        Assert.Equal([4, 5], File.ReadAllBytes(Out("out-p1.png")));
        Assert.Equal([6], File.ReadAllBytes(Out("out-p2.png")));
    }

    [Fact]
    public void AnUndoDeniedAccess_NamesWhatItLeft()
    {
        // The undo's own moves and deletes can be denied too: each is named, and the rest is still put back.
        Earlier();
        var files = new Files((from, to) => from.EndsWith(".tmp") && to == Out("out-p2.png"),
            denyMove: (from, to) => from.EndsWith(".backup") && to == Out("out-p1.png"), denyDelete: path => path == Out("out.pdf"));
        Assert.Equal(CliApp.Failed, Run(Open, files, args: Render(Doc(2))));
        Assert.Contains($"could not be removed: {Out("out.pdf")}", _err.ToString());
        var keptP1 = Assert.Single(Directory.GetFiles(_dir, "out-p1.png.*.backup"));
        Assert.Contains(keptP1, _err.ToString());
        Assert.Equal([6], File.ReadAllBytes(Out("out-p2.png")));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void ABackupThatCannotBeRestored_IsKeptAndNamed()
    {
        Earlier();
        // Moving the second PNG page into place fails, and so does putting the PDF back.
        var files = new Files((from, to) => (from.EndsWith(".tmp") && to == Out("out-p2.png")) || (from.EndsWith(".backup") && to == Out("out.pdf")));
        Assert.Equal(CliApp.Failed, Run(Open, files, args: Render(Doc(2))));
        var kept = Assert.Single(Directory.GetFiles(_dir, "out.pdf.*.backup"));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(kept));
        Assert.Contains(kept, _err.ToString());
        Assert.Equal([4, 5], File.ReadAllBytes(Out("out-p1.png")));
        Assert.Equal([6], File.ReadAllBytes(Out("out-p2.png")));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void ASuccessfulCommand_LeavesNoBackupOrTemporary()
    {
        Earlier();
        var files = new Files();
        Assert.Equal(CliApp.Ok, Run(Open, files, args: Render(Doc(2))));
        Assert.StartsWith("%PDF", File.ReadAllText(Out("out.pdf"))[..4]);
        Assert.True(new FileInfo(Out("out-p2.png")).Length > 1);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.Empty(Directory.GetFiles(_dir, "*.backup"));
        Assert.Contains($"wrote {Out("out.pdf")}", _out.ToString());
    }

    [Fact]
    public void ABackupThatCannotBeDeleted_IsNamedInAWarning()
    {
        Earlier();
        var files = new FailingDelete(".backup");
        Assert.Equal(CliApp.Ok, Run(Open, files, args: Render(Doc(2))));
        Assert.Contains("warning:", _err.ToString());
        Assert.Contains(".backup", _err.ToString());
    }

    private sealed class FailingDelete(string suffix) : ICliFiles
    {
        public void Move(string from, string to) => File.Move(from, to);
        public void Delete(string path)
        {
            if (path.EndsWith(suffix)) throw new IOException("in use");
            File.Delete(path);
        }
    }

    [Fact]
    public void ALeftoverBackup_SurvivesTheNextRun()
    {
        // A backup and a temporary left by an earlier run that was killed are never touched.
        var backup = Out($"out.pdf.{Guid.NewGuid():N}.backup");
        var temp = Out($"out.pdf.{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(backup, [9]);
        File.WriteAllBytes(temp, [8]);
        Assert.Equal(CliApp.Ok, Run(Open, new Files(), args: Render(Doc(1))));
        Assert.Equal([9], File.ReadAllBytes(backup));
        Assert.Equal([8], File.ReadAllBytes(temp));
    }

    [Fact]
    public void ADocJsonTooLarge_IsNamed()
    {
        var doc = Doc(1);
        Assert.Equal(CliApp.Failed, Run(Open, new Files(), maxLoadBytes: new FileInfo(doc).Length - 1, args: ["render", doc, "--pdf", Out("out.pdf")]));
        Assert.Contains(doc, _err.ToString());
        Assert.Contains("more than", _err.ToString());
        Assert.False(File.Exists(Out("out.pdf")));
    }
}
