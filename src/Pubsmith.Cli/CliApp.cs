using System.Globalization;
using Pubsmith.Core;
using Pubsmith.PubReader;
using Pubsmith.Rendering;

namespace Pubsmith.Cli;

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

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length < 2) return UsageError(stderr, null);
        try
        {
            return args[0] switch
            {
                "render" => Render(args, stdout, stderr),
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

    private static int Render(string[] args, TextWriter stdout, TextWriter stderr)
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
                try { doc = DocumentJson.Load(input); }
                catch (DocumentFormatException ex) { stderr.WriteLine($"error: {ex.Message}"); return Failed; }
                baseDirectory = Path.GetDirectoryName(Path.GetFullPath(input))!;
            }
            if (doc.Pages.Count == 0) { stderr.WriteLine("error: the document has no pages."); return Failed; }

            using var ctx = new RenderContext(baseDirectory);   // owns the pictures it opens
            try
            {
                if (pdf is not null)
                    WriteAtomically(pdf, s => Exporter.ToPdf(doc, ctx, s), stdout);
                if (png is not null)
                {
                    for (var p = 0; p < doc.Pages.Count; p++)
                    {
                        var target = doc.Pages.Count == 1 ? png : PagePath(png, p + 1);
                        var page = doc.Pages[p];
                        WriteAtomically(target, s => Exporter.ToPng(page, dpi, ctx, s), stdout);
                    }
                }
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

    /// <summary>Writes to a uniquely named temporary sibling and renames, so a failed render never leaves a truncated file behind.</summary>
    private static void WriteAtomically(string path, Action<Stream> write, TextWriter stdout)
    {
        var full = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(full)!;
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"The output folder '{folder}' does not exist.");
        var temp = $"{full}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var fs = File.Create(temp)) write(fs);
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        stdout.WriteLine($"wrote {full}");
    }

    private static int UsageError(TextWriter stderr, string? detail)
    {
        if (detail is not null) stderr.WriteLine($"error: {detail}");
        stderr.WriteLine(UsageText);
        return Usage;
    }
}
