using System.Globalization;
using Pubsmith.PubReader.Contents;
using Xunit.Abstractions;

namespace Pubsmith.PubReader.Tests;

/// <summary>Skips unless PUBSMITH_CORPUS points at a harvest folder (real files and Publisher's renders, kept outside the repo).</summary>
public sealed class CorpusFactAttribute : FactAttribute
{
    public CorpusFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PUBSMITH_CORPUS")))
            Skip = "Set PUBSMITH_CORPUS to a harvest folder (see README, Reference material and the corpus suite) to run the corpus suite.";
    }
}

// Opt-in corpus suite (WI-004): every real file, checked against Publisher's own manifest.
public class CorpusTests(ITestOutputHelper output)
{
    internal static string Root => Environment.GetEnvironmentVariable("PUBSMITH_CORPUS")!;

    /// <summary>The harvest's mirror of a source path: "D:\a\b.pub" -> "D\a\b.pub", "\\server\share\b.pub" -> "server\share\b.pub" (always relative).</summary>
    internal static string Mirror(string full) => (full[..1] + full[2..]).TrimStart('\\', '/');

    /// <summary>Text boxes in the maintainers' corpus whose text does not match Publisher's export yet.</summary>
    private const int KnownTextMisses = 3;

    /// <summary>Rows of manifest.csv for the real (non test-corpus) files: source, pages, width in, height in.</summary>
    internal static IEnumerable<(string Source, int Pages, double WidthIn, double HeightIn)> RealFiles()
    {
        var lines = File.ReadAllLines(Path.Combine(Root, "manifest.csv"));
        var head = Csv(lines[0]);
        int Col(string n) => Array.IndexOf(head, n);
        foreach (var line in lines.Skip(1))
        {
            var f = Csv(line);
            var src = f[Col("Source")];
            if (src.Contains(@"\test-corpus\") || src.Contains(@"\diff-corpus\") || f[Col("Status")] != "OK") continue;
            yield return (src, int.Parse(f[Col("Pages")], CultureInfo.InvariantCulture),
                double.Parse(f[Col("WidthIn")], CultureInfo.InvariantCulture), double.Parse(f[Col("HeightIn")], CultureInfo.InvariantCulture));
        }
    }

    private static string[] Csv(string line) =>
        System.Text.RegularExpressions.Regex.Matches(line, "\"((?:[^\"]|\"\")*)\"|([^,]*)").Cast<System.Text.RegularExpressions.Match>()
            .Where((m, i) => m.Length > 0 || i % 2 == 0).Select(m => m.Groups[1].Success ? m.Groups[1].Value.Replace("\"\"", "\"") : m.Groups[2].Value).ToArray();

    [CorpusFact]
    public void EveryRealFile_OpensWithPublishersPageCountAndSize()
    {
        var failures = new List<string>();
        var olderFormat = new List<string>();   // pre-2003 files: explicitly unsupported for now, listed, not hidden
        var n = 0;
        foreach (var (src, pages, w, h) in RealFiles())
        {
            n++;
            try
            {
                var pkg = PubPackage.Open(src);
                var c = ContentsReader.Read(pkg.Contents);
                var wIn = c.PageWidthEmu / 914400.0; var hIn = c.PageHeightEmu / 914400.0;
                if (c.ContentPages.Count != pages || Math.Abs(wIn - w) > 0.001 || Math.Abs(hIn - h) > 0.001)
                    failures.Add($"{Path.GetFileName(src)}: pages {c.ContentPages.Count} vs {pages}, size {wIn:F3}x{hIn:F3} vs {w}x{h}");
            }
            catch (PubFormatException ex) when (ex.Message.Contains("older Publisher versions")) { olderFormat.Add(Path.GetFileName(src)); }
            catch (PubFormatException ex) { failures.Add($"{Path.GetFileName(src)}: {ex.Message}"); }
        }
        foreach (var f in failures) output.WriteLine(f);
        foreach (var f in olderFormat) output.WriteLine($"{f}: older format, not supported yet");
        output.WriteLine($"{n - failures.Count - olderFormat.Count}/{n} files match; {olderFormat.Count} older-format");
        Assert.True(n >= 36, $"only {n} real files in the manifest");
        Assert.Empty(failures);
        Assert.True(olderFormat.Count <= 1, "more pre-2003 files than the one known in the author's corpus");
    }

    /// <summary>
    /// AC9 gate: import every real file natively, render each page at 300 dpi, score it against Publisher's own
    /// render. Writes native\&lt;mirror&gt;\ (document, pictures, import report, page PNGs) and native-report.csv.
    /// The gate is a decision, so this test asserts only that every supported file imported.
    /// </summary>
    [CorpusFact]
    public void Gate_FidelityReport()
    {
        var outRoot = Path.Combine(Root, "native");
        var rows = new List<string> { "Source,Pages,ShapesRead,Converted,Placeheld,Approximations,FontsSubstituted,MaxScore,PageScores,Error" };
        var scores = new List<double>();
        var failed = new List<string>();
        foreach (var (src, _, _, _) in RealFiles())
        {
            var full = Path.GetFullPath(src);
            var dir = Path.Combine(outRoot, Path.GetDirectoryName(Mirror(full))!);
            var name = Path.GetFileNameWithoutExtension(src);
            try
            {
                var result = PubImporter.Import(src);
                var json = PubImporter.WriteTo(result, dir, name);
                var doc = Pubsmith.Core.DocumentJson.Load(json);
                var ctx = new Pubsmith.Rendering.RenderContext(baseDirectory: dir);
                var pageScores = new List<string>();
                for (var i = 0; i < doc.Pages.Count; i++)
                {
                    using var bmp = Pubsmith.Rendering.Exporter.RenderBitmap(doc.Pages[i], 300, ctx);
                    var golden = Path.Combine(Root, "png", Path.GetDirectoryName(Mirror(full))!, $"{name}-p{i + 1}.png");
                    using (var fs = File.Create(Path.Combine(dir, $"{name}.native-p{i + 1}.png"))) bmp.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90).SaveTo(fs);
                    if (!File.Exists(golden)) { pageScores.Add("n/a"); continue; }
                    using var reference = SkiaSharp.SKBitmap.Decode(golden);
                    var score = Pubsmith.Rendering.ImageComparer.Score(reference, bmp);
                    scores.Add(score);
                    pageScores.Add(score.ToString("F2", CultureInfo.InvariantCulture));
                }
                var subs = string.Join("; ", ctx.Warnings.Where(w => w.Code == Pubsmith.Rendering.RenderWarning.FontSubstituted)
                    .Select(w => System.Text.RegularExpressions.Regex.Match(w.Message, "'([^']+)'").Groups[1].Value).Distinct());
                var max = pageScores.Where(p => p != "n/a").Select(p => double.Parse(p, CultureInfo.InvariantCulture)).DefaultIfEmpty(double.NaN).Max();
                rows.Add(string.Join(",", Q(src), doc.Pages.Count, result.ShapesRead, result.ShapesConverted, result.ShapesPlaceheld,
                    result.Issues.Count(x => x.Kind == ImportIssueKind.Approximated), Q(subs), max.ToString("F2", CultureInfo.InvariantCulture), Q(string.Join(" ", pageScores)), ""));
            }
            catch (PubFormatException ex)
            {
                rows.Add(string.Join(",", Q(src), 0, 0, 0, 0, 0, "", "", "", Q(ex.Message)));
                if (!ex.Message.Contains("older Publisher versions")) failed.Add($"{Path.GetFileName(src)}: {ex.Message}");
            }
        }
        File.WriteAllLines(Path.Combine(Root, "native-report.csv"), rows);
        var within3 = scores.Count(s => s <= 3.0);
        var within6 = scores.Count(s => s <= 6.0);
        var sorted = scores.Order().ToList();
        output.WriteLine($"GATE: {within3}/{scores.Count} pages <= 3.0 ({100.0 * within3 / scores.Count:F0}%), {within6} <= 6.0 ({100.0 * within6 / scores.Count:F0}%), median {sorted[sorted.Count / 2]:F2}");
        foreach (var f in failed) output.WriteLine("FAILED " + f);
        Assert.Empty(failed);

        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>Publisher-assisted export (WI-002) of a real file: the ground truth for its text boxes.</summary>
    internal static string TruthJson(string source)
    {
        var full = Path.GetFullPath(source);
        var rel = Mirror(full);
        return Path.Combine(Root, "funcular", Path.ChangeExtension(rel, ".json"));
    }

    private static string Norm(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();

    [CorpusFact]
    public void EveryTextBox_TextMatchesPublishersExport()
    {
        var misses = new List<string>();
        int files = 0, boxes = 0;
        foreach (var (src, _, _, _) in RealFiles())
        {
            PubPackage pkg;
            try { pkg = PubPackage.Open(src); } catch (PubFormatException ex) when (ex.Message.Contains("older Publisher versions")) { continue; }
            files++;
            var contents = ContentsReader.Read(pkg.Contents);
            var stories = Quill.QuillReader.Read(pkg.Quill, new ColorResolver(contents.Palette));
            var ours = stories.Values.Select(s => Norm(string.Join(" ", s.Paragraphs.Select(p => string.Concat(p.Runs.Select(r => r.Text)))))).ToHashSet();

            using var truth = System.Text.Json.JsonDocument.Parse(File.ReadAllText(TruthJson(src)));
            foreach (var page in truth.RootElement.GetProperty("pages").EnumerateArray())
                foreach (var el in page.GetProperty("elements").EnumerateArray().Where(e => e.GetProperty("type").GetString() == "text"))
                {
                    var t = Norm(string.Join(" ", el.GetProperty("paragraphs").EnumerateArray()
                        .Select(p => string.Concat(p.GetProperty("runs").EnumerateArray().Select(r => r.GetProperty("text").GetString())))));
                    if (t.Length == 0) continue;
                    boxes++;
                    if (!ours.Contains(t)) misses.Add($"{Path.GetFileName(src)}: \"{(t.Length > 60 ? t[..60] + "…" : t)}\"");
                }
        }
        foreach (var m in misses) output.WriteLine(m);
        output.WriteLine($"{boxes - misses.Count}/{boxes} text boxes matched across {files} files");
        // A ratchet, not a pass/fail on perfection: the maintainers' corpus has KnownTextMisses boxes whose text is
        // not matched yet (two share a story across linked boxes; one is unexplained; WI-004). More is a regression.
        Assert.True(misses.Count <= KnownTextMisses, $"{misses.Count} text boxes do not match (known: {KnownTextMisses})");
    }
}
