using Pubsmith.Cli;
using SkiaSharp;

namespace Pubsmith.Cli.Tests;

/// <summary>Tests that point the process's temporary folder elsewhere: nothing else in this process runs meanwhile.</summary>
[CollectionDefinition(nameof(TempPathCollection), DisableParallelization = true)]
public sealed class TempPathCollection;

// `render file.pub` imports into a temporary folder and must leave nothing there. To see everything it leaves, the
// test gives the process a temporary folder of its own (TMP, TEMP and TMPDIR) for the length of the command, and then
// expects that folder to be empty. Other processes keep their own temporary folders, so nothing can race with it.
[Collection(nameof(TempPathCollection))]
public sealed class RenderScratchTests : IDisposable
{
    private static readonly string[] TempVariables = ["TMP", "TEMP", "TMPDIR"];

    private readonly string _dir = Directory.CreateTempSubdirectory("pubsmith-cli-scratch-").FullName;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Runs the command with the process's temporary folder set to <paramref name="temp"/>.</summary>
    private int RunWithTemp(string temp, params string[] args)
    {
        var saved = TempVariables.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var v in TempVariables) Environment.SetEnvironmentVariable(v, temp);
            // The redirection must hold, or the check below would look at the wrong folder and pass for nothing.
            Assert.Equal(Path.TrimEndingDirectorySeparator(temp), Path.TrimEndingDirectorySeparator(Path.GetTempPath()));
            return CliApp.Run(args, _out, _err);
        }
        finally
        {
            foreach (var (v, value) in saved) Environment.SetEnvironmentVariable(v, value);
        }
    }

    [Fact]
    public void RenderPub_Directly_WritesPdfAndPng_AndLeavesNoTemporaryFile()
    {
        var pub = Path.Combine(_dir, "flyer.pub");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "pub", "picture__base.pub"), pub);
        var pdf = Path.Combine(_dir, "flyer.pdf");
        var png = Path.Combine(_dir, "flyer.png");
        var temp = Directory.CreateDirectory(Path.Combine(_dir, "temp")).FullName;

        Assert.Equal(0, RunWithTemp(temp, "render", pub, "--pdf", pdf, "--png", png, "--dpi", "36"));

        Assert.Empty(Directory.GetFileSystemEntries(temp));   // the temporary import folder, and everything in it, is gone
        Assert.StartsWith("%PDF", File.ReadAllText(pdf)[..4]);
        using (var bmp = SKBitmap.Decode(png)) Assert.Equal(306, bmp.Width);
        Assert.DoesNotContain("not found", _err.ToString());   // the picture came along
        Assert.Equal(["flyer.pdf", "flyer.png", "flyer.pub", "temp"], Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName).Order());
    }
}
