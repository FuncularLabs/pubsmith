using System.Globalization;
using Pubsmith.Core;
using Pubsmith.PubReader;
using Pubsmith.Rendering;

namespace Pubsmith.Cli;

/// <summary>The file moves and deletes of the CLI's commit (a seam for its tests).</summary>
internal interface ICliFiles
{
    void Move(string from, string to);
    void Delete(string path);
}

internal sealed class SystemFiles : ICliFiles
{
    public static SystemFiles Instance { get; } = new();
    public void Move(string from, string to) => File.Move(from, to);
    public void Delete(string path) => File.Delete(path);
}

/// <summary>
/// pubsmith render &lt;doc.json | file.pub&gt; [--pdf &lt;path&gt;] [--png &lt;path&gt;] [--dpi &lt;n&gt;]
/// pubsmith import &lt;file.pub&gt; [--out &lt;dir&gt;]
/// Exit codes: 0 success (warnings may be printed to stderr), 1 the input could not be read or rendered,
/// 2 usage error. Any failure while reading or rendering is reported on stderr and exits 1.
/// </summary>
public static class CliApp
{
    public const int Ok = 0, Failed = 1, Usage = 2;

    private const string UsageText =
        "usage: pubsmith render <doc.json | file.pub> [--pdf <path>] [--png <path>] [--dpi <n>]\n" +
        "       pubsmith import <file.pub> [--out <dir>]\n" +
        "  render: --png on a multi-page document writes <name>-p1.png, <name>-p2.png, ...\n" +
        "          --dpi applies to PNG output (default 300)\n" +
        "  import: writes <name>.json, its pictures and <name>.import.json (what was approximated or left out)\n" +
        "          into --out (default: a folder <name>-pubsmith beside the .pub; importing again updates it)";

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr) =>
        Run(args, stdout, stderr, RenderLimits.Default, TimeProvider.System, DocumentJson.MaxLoadBytes, SystemFiles.Instance);

    /// <summary>The CLI with its limits, clock, load limit and file operations given (for its tests).</summary>
    internal static int Run(string[] args, TextWriter stdout, TextWriter stderr, RenderLimits limits, TimeProvider time, long maxLoadBytes, ICliFiles files)
    {
        if (args.Length < 2) return UsageError(stderr, null);
        try
        {
            return args[0] switch
            {
                "render" => Render(args, stdout, stderr, limits, time, maxLoadBytes, files),
                "import" => Import(args, stdout, stderr),
                _ => UsageError(stderr, null),
            };
        }
        catch (Exception ex)
        {
            // Whatever goes wrong while reading or rendering, the contract is a message and exit code 1, never a crash.
            stderr.WriteLine($"error: {ex.Message}");
            return Failed;
        }
    }

    private static int Render(string[] args, TextWriter stdout, TextWriter stderr, RenderLimits limits, TimeProvider time, long maxLoadBytes, ICliFiles files)
    {
        var input = args[1];
        string? pdf = null, png = null;
        double dpi = 300;
        for (var i = 2; i < args.Length; i++)
        {
            var opt = args[i];
            if (i + 1 >= args.Length) return UsageError(stderr, $"option {opt} needs a value");
            var value = args[++i];
            switch (opt)
            {
                case "--pdf": pdf = value; break;
                case "--png": png = value; break;
                case "--dpi":
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out dpi) || !(dpi > 0 && dpi <= Exporter.MaxDpi))
                        return UsageError(stderr, $"--dpi must be a number in (0, {Exporter.MaxDpi.ToString(CultureInfo.InvariantCulture)}]");
                    break;
                default: return UsageError(stderr, $"unknown option {opt}");
            }
        }
        if (pdf is null && png is null) return UsageError(stderr, "nothing to do: give --pdf and/or --png");

        string? scratch = null;   // a .pub is imported into a temporary folder, which is removed afterwards
        try
        {
            PubsmithDocument doc;
            string baseDirectory;
            if (IsPub(input))
            {
                if (ImportOrReport(input, stderr) is not { } result) return Failed;
                scratch = Directory.CreateTempSubdirectory("pubsmith-render-").FullName;
                if (WriteOrReport(result, scratch, Path.GetFileNameWithoutExtension(input), input, stderr) is null) return Failed;
                ReportLosses(result, stderr);
                (doc, baseDirectory) = (result.Document, scratch);
            }
            else
            {
                try { doc = DocumentJson.Load(input, maxLoadBytes); }
                catch (DocumentFormatException ex) { stderr.WriteLine($"error: {ex.Message}"); return Failed; }
                baseDirectory = Path.GetDirectoryName(Path.GetFullPath(input))!;
            }
            if (doc.Pages.Count == 0) { stderr.WriteLine("error: the document has no pages."); return Failed; }

            using var ctx = new RenderContext(baseDirectory, limits, time);   // owns the pictures it opens
            try
            {
                // The whole command, before anything is written, and before the outputs are planned: planning grows with
                // the pages, and the count bounds them.
                RenderLimits.Check(doc, new RenderOutputs(pdf is not null, png is not null ? dpi : null), ctx);

                // Every output, by its full path, checked against the others and its folder before anything is written.
                var outputs = new List<(string Path, Action<Stream, RenderContext> Write)>();
                if (pdf is not null) outputs.Add((Path.GetFullPath(pdf), (s, c) => Exporter.ToPdf(doc, c, s)));
                if (png is not null)
                {
                    // Expanded once: a folder given in 8.3 short form costs a file-system lookup at each expansion.
                    var fullPng = Path.GetFullPath(png);
                    for (var p = 0; p < doc.Pages.Count; p++)
                    {
                        var page = doc.Pages[p];
                        outputs.Add((Path.GetFullPath(doc.Pages.Count == 1 ? fullPng : PagePath(fullPng, p + 1)), (s, c) => Exporter.ToPng(page, dpi, c, s)));
                    }
                }
                var samePath = outputs.GroupBy(o => o.Path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
                if (samePath is not null) return UsageError(stderr, $"two outputs have the same path: {samePath.Key}");
                foreach (var folder in outputs.Select(o => Path.GetDirectoryName(o.Path)!).Distinct())
                    if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"The output folder '{folder}' does not exist.");

                var staged = Stage(outputs, ctx, files, stderr);
                Commit(staged, files, stdout, stderr);
            }
            finally
            {
                foreach (var w in ctx.Warnings) stderr.WriteLine($"warning: {w.Message}");
            }
            return Ok;
        }
        finally
        {
            if (scratch is not null)
            {
                try { Directory.Delete(scratch, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { stderr.WriteLine($"warning: could not remove the temporary folder {scratch}: {ex.Message}"); }
            }
        }
    }

    private static int Import(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var input = args[1];
        string? outDir = null;
        for (var i = 2; i < args.Length; i++)
        {
            var opt = args[i];
            if (opt != "--out") return UsageError(stderr, $"unknown option {opt}");
            if (i + 1 >= args.Length) return UsageError(stderr, $"option {opt} needs a value");
            outDir = args[++i];
        }
        if (ImportOrReport(input, stderr) is not { } result) return Failed;

        var name = Path.GetFileNameWithoutExtension(input);
        outDir ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!, name + "-pubsmith");
        if (WriteOrReport(result, outDir, name, input, stderr) is not { } json) return Failed;
        stdout.WriteLine($"wrote {json}");
        var pages = result.Document.Pages.Count;
        stdout.WriteLine($"{pages} page{(pages == 1 ? "" : "s")}, {result.ShapesRead} shapes: {result.ShapesConverted} converted, " +
            $"{result.ShapesPlaceheld} shown as placeholders or not drawn; {result.Issues.Count} notes in {name}.import.json");
        ReportLosses(result, stderr);
        return Ok;
    }

    private static bool IsPub(string path) => path.EndsWith(".pub", StringComparison.OrdinalIgnoreCase);

    private static PubImportResult? ImportOrReport(string path, TextWriter stderr)
    {
        try { return PubImporter.Import(path); }
        catch (PubFormatException ex) { stderr.WriteLine($"error: {ex.Message}"); return null; }
    }

    /// <summary>Writes an import, or says why not: a document past the JSON limit can only come from a damaged or hostile file.</summary>
    private static string? WriteOrReport(PubImportResult result, string directory, string name, string input, TextWriter stderr)
    {
        try { return PubImporter.WriteTo(result, directory, name); }
        catch (DocumentTooLargeException)
        {
            stderr.WriteLine($"error: '{Path.GetFileName(input)}' is damaged: its document would be more than {PubImporter.MaxJsonBytes >> 20} MiB of JSON, the most Pubsmith writes; no file was written.");
            return null;
        }
    }

    /// <summary>Placeholders and dropped items are worth a line on stderr; approximations stay in the report.</summary>
    private static void ReportLosses(PubImportResult result, TextWriter stderr)
    {
        var held = result.Issues.Count(i => i.Kind == ImportIssueKind.Placeholder);
        var dropped = result.Issues.Where(i => i.Kind == ImportIssueKind.Dropped).ToList();
        if (held > 0) stderr.WriteLine($"warning: {held} shape{(held == 1 ? " is" : "s are")} shown as placeholder outlines (the report says why).");
        foreach (var d in dropped) stderr.WriteLine($"warning: {(d.Page > 0 ? $"page {d.Page}: " : "")}{d.Detail}");
    }

    private static string PagePath(string path, int page) =>
        Path.Combine(Path.GetDirectoryName(path) ?? "", $"{Path.GetFileNameWithoutExtension(path)}-p{page}{Path.GetExtension(path)}");

    /// <summary>
    /// Writes every output to a uniquely named temporary sibling (<c>&lt;output&gt;.&lt;guid&gt;.tmp</c>). If any of them fails,
    /// the temporaries written so far are deleted and nothing else is touched.
    /// </summary>
    private static List<(string Path, string Temp)> Stage(List<(string Path, Action<Stream, RenderContext> Write)> outputs, RenderContext ctx, ICliFiles files, TextWriter stderr)
    {
        var staged = new List<(string Path, string Temp)>();
        try
        {
            foreach (var (path, write) in outputs)
            {
                var temp = $"{path}.{Guid.NewGuid():N}.tmp";
                staged.Add((path, temp));
                using var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write);
                write(file, ctx);
            }
            return staged;
        }
        catch
        {
            foreach (var (_, temp) in staged) DeleteOrWarn(temp, "temporary file", files, stderr);
            throw;
        }
    }

    /// <summary>
    /// Moves the staged outputs into place: each existing output first to a uniquely named backup
    /// (<c>&lt;output&gt;.&lt;guid&gt;.backup</c>), then its temporary into place. If a move fails, the moves are undone in
    /// reverse order (what was moved in is deleted, each output put back from its backup); a backup that cannot be put
    /// back is kept and named, and so is a new output that cannot be removed. When every move succeeded, the backups are
    /// deleted.
    /// </summary>
    private static void Commit(List<(string Path, string Temp)> staged, ICliFiles files, TextWriter stdout, TextWriter stderr)
    {
        var done = new List<(string Path, string? Backup, bool MovedIn)>();
        try
        {
            foreach (var (path, temp) in staged)
            {
                string? backup = null;
                if (File.Exists(path))
                {
                    backup = $"{path}.{Guid.NewGuid():N}.backup";
                    files.Move(path, backup);
                }
                done.Add((path, backup, false));
                files.Move(temp, path);
                done[^1] = (path, backup, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var kept = new List<string>();      // backups that could not be put back
            var leftNew = new List<string>();   // new outputs moved into place that could not be removed
            for (var k = done.Count - 1; k >= 0; k--)
            {
                var (path, backup, movedIn) = done[k];
                if (movedIn)
                {
                    try { files.Delete(path); }
                    catch (Exception undo) when (undo is IOException or UnauthorizedAccessException)
                    {
                        leftNew.Add(path);
                        if (backup is not null) kept.Add(backup);   // the earlier output cannot be put back over it
                        continue;
                    }
                }
                if (backup is null) continue;
                try { files.Move(backup, path); }
                catch (Exception undo) when (undo is IOException or UnauthorizedAccessException) { kept.Add(backup); }
            }
            foreach (var (_, temp) in staged)
                if (File.Exists(temp)) DeleteOrWarn(temp, "temporary file", files, stderr);
            var left = new List<string>();
            if (leftNew.Count > 0) left.Add($"these new outputs could not be removed: {string.Join(", ", leftNew)}");
            if (kept.Count > 0) left.Add($"these backups could not be put back and were kept: {string.Join(", ", kept)}");
            throw new IOException(left.Count == 0
                ? $"{ex.Message}; nothing was replaced: every earlier output was put back."
                : $"{ex.Message}; the replacement was undone except: {string.Join("; ", left)}.", ex);
        }
        foreach (var (_, backup, _) in done)
            if (backup is not null) DeleteOrWarn(backup, "backup", files, stderr);
        foreach (var (path, _) in staged) stdout.WriteLine($"wrote {path}");
    }

    private static void DeleteOrWarn(string path, string what, ICliFiles files, TextWriter stderr)
    {
        try { files.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { stderr.WriteLine($"warning: could not delete the {what} {path}: {ex.Message}"); }
    }

    private static int UsageError(TextWriter stderr, string? detail)
    {
        if (detail is not null) stderr.WriteLine($"error: {detail}");
        stderr.WriteLine(UsageText);
        return Usage;
    }
}
