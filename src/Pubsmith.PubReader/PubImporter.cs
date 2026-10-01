using System.Buffers.Binary;
using System.Text.Json;
using Pubsmith.Core;
using Pubsmith.PubReader.Contents;
using Pubsmith.PubReader.OfficeArt;
using Pubsmith.PubReader.Quill;

namespace Pubsmith.PubReader;

public enum ImportIssueKind
{
    /// <summary>Drawn, but not exactly as Publisher draws it (e.g. WordArt as plain styled text).</summary>
    Approximated,
    /// <summary>Could not be reproduced; a visible outline stands in its place.</summary>
    Placeholder,
    /// <summary>Could not be read or placed at all; nothing stands in its place.</summary>
    Dropped,
}

/// <param name="Page">1-based content page, or 0 for something that concerns the whole publication.</param>
public sealed record ImportIssue(int Page, uint? Seqnum, string Shape, ImportIssueKind Kind, string Detail);

/// <param name="Assets">Picture bytes keyed by the relative path used in <see cref="ImageElement.Source"/>.</param>
/// <param name="ShapesRead">Top-level drawing elements on content pages (master elements counted per page).</param>
/// <param name="ShapesConverted">Of those, how many produced at least one real element (exact or approximated).</param>
/// <param name="ShapesPlaceheld">Of those, how many produced only a placeholder (or nothing). Read = Converted + Placeheld.</param>
public sealed record PubImportResult(PubsmithDocument Document, IReadOnlyList<ImportIssue> Issues, IReadOnlyDictionary<string, byte[]> Assets, int ShapesRead, int ShapesConverted, int ShapesPlaceheld)
{
    /// <summary>Work placed, in <see cref="PubImporter.MaxElements"/> units (diagnostics and tests).</summary>
    internal int UnitsPlaced { get; init; }

    /// <summary>Characters of text placed, in <see cref="PubImporter.MaxTextPlaced"/> terms (diagnostics and tests).</summary>
    internal long TextPlaced { get; init; }
}

/// <summary>
/// Native .pub import (no Publisher): Contents (pages, masters) + OfficeArt (geometry, styling, pictures) +
/// Quill (text) mapped onto the Pubsmith model. Nothing is dropped silently: every shape becomes an element,
/// an approximated element, or a reported placeholder, and anything that cannot be placed at all is reported
/// as dropped.
/// </summary>
public static class PubImporter
{
    private const double Emu = 12700;

    /// <summary>
    /// Work placed before the file is treated as damaged, in units: one per page, per background, per entry in a
    /// page's shape list (looked up, found or not, repeated or not), per shape converted or skipped as deleted (group
    /// members, and master shapes on every page, included, whether or not they draw anything), per issue (found
    /// while placing, or while parsing: a few, each kind reported once), per paragraph and run of text placed, per 1,024 characters of text placed, and per WordArt
    /// shape plus its 1,024-character blocks. Whatever is repeated is charged every time it is placed, except a story
    /// shared by linked text boxes, which is placed once. Ordinary publications stay far below it
    /// (PublicationScaleTests: a 1,000-page book with a master, 300 pages of one linked story); the largest of 192
    /// files measured (real publications and test files) used 2,412 units, the median 5. The strings the placed
    /// things carry are bounded separately, by <see cref="MaxTextPlaced"/>.
    /// </summary>
    public const int MaxElements = 250_000;

    /// <summary>Longest font family name kept (real names are at most 31 characters); longer ones are cut and reported.</summary>
    public const int MaxFontName = 256;

    /// <summary>
    /// Characters of text placed before the file is treated as damaged: every character of the strings the document
    /// repeats per placement (text runs, WordArt text, a picture's file path, and a font name beyond its first 32
    /// characters), counted every time it is placed (except a linked story, placed once). Units
    /// bound how many things are placed; this bounds the strings they carry, so an import fails early instead of
    /// building a document mostly made of repeated text. A 1,000-page book of 6,000 characters a page, in 4 runs a
    /// paragraph, places 6 million (OutputCeilingTests); the largest of the 192 files measured placed 25,632, the
    /// median 24. What is written is limited separately, by <see cref="MaxJsonBytes"/>.
    /// </summary>
    public const int MaxTextPlaced = 8 * 1024 * 1024;

    /// <summary>A font name's characters that cost nothing against <see cref="MaxTextPlaced"/>: real names are at most 31.</summary>
    private const int FreeFontNameChars = 32;

    /// <summary>
    /// Largest document <see cref="WriteTo(PubImportResult, string, string)"/> writes: 256 MiB of JSON, enforced while
    /// it is streamed, whatever the shapes. A larger one (only hostile files come near: the novel above writes about a
    /// quarter of it) is refused with <see cref="DocumentTooLargeException"/> before anything in the folder changes.
    /// </summary>
    public const long MaxJsonBytes = 256L << 20;

    // MS-ODRAW geometry-text booleans (0x00FF): bit 4 italic, bit 5 bold, each with its use bit 16 higher.
    private const ushort PropGtextFlags = 0x00FF;

    private static readonly Stroke PlaceholderStroke = new(new Rgba(160, 160, 160), 0.75);

    public static PubImportResult Import(string path) => Import(PubPackage.Open(path));

    /// <summary>
    /// Imports an opened package. Only <see cref="PubFormatException"/> escapes. One the reader raised on purpose
    /// has no inner exception; anything else that fails inside the reader is wrapped, with the original as the
    /// inner exception, so a damaged file can never crash the caller.
    /// </summary>
    public static PubImportResult Import(PubPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var file = $"'{package.Name}'";
        try { return ImportCore(package); }
        catch (PubFormatException ex) when (ex.InnerException is null && !ex.Message.StartsWith(file, StringComparison.Ordinal))
        {
            throw new PubFormatException($"{file} is damaged: {ex.Message}");
        }
        catch (PubFormatException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new PubFormatException($"{file} could not be read ({ex.GetType().Name}: {ex.Message}). This is a Pubsmith bug; please report it.", ex);
        }
    }

    private static PubImportResult ImportCore(PubPackage package)
    {
        var name = Path.GetFileNameWithoutExtension(package.Name);
        var issues = new List<ImportIssue>();
        void Lost(string detail) => issues.Add(new ImportIssue(0, null, "publication", ImportIssueKind.Dropped, detail));

        var contents = ContentsReader.Read(package.Contents);
        var colors = new ColorResolver(contents.Palette);
        var top = EscherReader.ReadTopLevel(package.EscherStm, out var damaged);
        if (damaged > 0)
            Lost(damaged == 1 ? "1 damaged drawing record was skipped; some shapes may be missing or ungrouped"
                : $"{damaged} damaged drawing records were skipped; some shapes may be missing or ungrouped");

        // Text and pictures are read separately: damage in either loses that layer (reported), not the whole file.
        IReadOnlyDictionary<uint, Story> stories;
        var textProblems = new List<ImportIssue>();
        try { stories = QuillReader.Read(package.Quill, colors, textProblems); }
        catch (PubFormatException ex)
        {
            stories = new Dictionary<uint, Story>();
            textProblems.Clear();
            Lost($"text could not be read ({ex.Message}); text boxes are imported empty");
        }
        issues.AddRange(textProblems);

        IReadOnlyList<Blip?> blips;
        try { blips = EscherReader.ReadBlipStore(package.EscherStm, package.EscherDelayStm); }
        catch (PubFormatException ex)
        {
            blips = [];
            Lost($"the picture store could not be read ({ex.Message}); pictures are imported as placeholders");
        }

        return Compose(name, contents, colors, top, stories, blips, issues);
    }

    /// <summary>Lays the parsed layers out as pages. Separate from parsing so page composition can be tested alone.</summary>
    internal static PubImportResult Compose(string name, ContentsModel contents, ColorResolver colors, IReadOnlyList<EscherShape> top,
        IReadOnlyDictionary<uint, Story> stories, IReadOnlyList<Blip?> blips, List<ImportIssue> issues)
    {
        var ctx = new Ctx(contents, colors, stories, blips, name + ".assets", top, issues);
        ctx.Place(issues.Count);   // issues found while parsing (a few, each kind reported once) count like any other
        var pages = new List<Page>();
        for (var i = 0; i < contents.ContentPages.Count; i++)
        {
            var page = contents.ContentPages[i];
            ctx.PageNumber = i + 1;
            var master = page.MasterSeqnum is { } ms && ctx.PagesBySeqnum.TryGetValue(ms, out var m) && m.IsMaster ? m : null;
            // Backgrounds sit below every shape, the master's shapes included (docs/format/pub-format-notes.md §5.4).
            ctx.Place(1);
            var elements = new List<Element>();
            if (master is not null) AddBackground(ctx, master, elements);
            AddBackground(ctx, page, elements);
            if (master is not null) AddShapes(ctx, master, elements);
            AddShapes(ctx, page, elements);
            pages.Add(new Page(ctx.PageWidth, ctx.PageHeight) { Elements = elements });
        }
        var doc = new PubsmithDocument { Name = name, Pages = pages };
        return new PubImportResult(doc, ctx.Issues, ctx.Assets, ctx.Read, ctx.Converted, ctx.Placeheld) { UnitsPlaced = ctx.Placed, TextPlaced = ctx.TextPlaced };
    }

    /// <summary>Converts one drawing shape on an otherwise empty letter page: the mapping rules, without a file.</summary>
    internal static (List<Element> Elements, IReadOnlyList<ImportIssue> Issues) ConvertOne(EscherShape shape, ShapeInfo? info = null,
        IReadOnlyList<Blip?>? blips = null, IReadOnlyDictionary<uint, Story>? stories = null, IReadOnlyList<uint>? palette = null)
    {
        var shapes = new Dictionary<uint, ShapeInfo>();
        if (info is not null) shapes[info.Seqnum] = info;
        var contents = new ContentsModel(612 * 12700, 792 * 12700, [], [], shapes, palette ?? []);
        var ctx = new Ctx(contents, new ColorResolver(palette ?? []), stories ?? new Dictionary<uint, Story>(), blips ?? [], "test.assets", [shape], []) { PageNumber = 1 };
        var into = new List<Element>();
        Convert(ctx, shape, ToFrame(ctx, shape.Anchor), into);
        return (into, ctx.Issues);
    }

    /// <summary>
    /// Writes &lt;name&gt;.json, the pictures (at their Source paths) and &lt;name&gt;.import.json; returns the JSON path.
    /// The folder is created if it is missing. The document is written first, beside its name and within
    /// <see cref="MaxJsonBytes"/>, so a document that is too large writes no file and changes nothing already in the
    /// folder; it is moved into place last, so into an empty folder it exists only when its pictures do. Importing
    /// again into the same folder updates it in place, and once the new document is in place, pictures an earlier
    /// import left that it no longer uses are removed (best effort: a locked one stays). If such a re-import fails
    /// after its document is written (writing its pictures or report, or moving the document into place), the
    /// earlier document stays, beside whatever pictures (same-named ones replaced) and report the failed run had
    /// already written, and the new document is removed.
    /// </summary>
    public static string WriteTo(PubImportResult result, string directory, string name) => WriteTo(result, directory, name, MaxJsonBytes);

    internal static string WriteTo(PubImportResult result, string directory, string name, long maxJsonBytes)
    {
        Directory.CreateDirectory(directory);
        var json = Path.Combine(directory, name + ".json");
        using var staged = DocumentJson.Stage(result.Document, json, maxJsonBytes);   // refused here, before anything changes
        foreach (var (rel, bytes) in result.Assets)
        {
            var full = Path.Combine(directory, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }
        var report = Path.Combine(directory, name + ".import.json");
        using (var stream = File.Create(report))   // streamed: the report is never one string in memory
            JsonSerializer.Serialize(stream, result.Issues, new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        staged.Commit();   // in place last: the document exists only when its pictures do

        // Only now are an earlier import's extra pictures unreferenced. Only files this importer names are removed.
        var pictures = Path.Combine(directory, result.Document.Name + ".assets");
        if (Directory.Exists(pictures))
        {
            var kept = result.Assets.Keys.Select(k => Path.GetFullPath(Path.Combine(directory, k))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(pictures))
                if (System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file), @"^img\d+\.(png|jpg|bmp)$") && !kept.Contains(Path.GetFullPath(file)))
                {
                    try { File.Delete(file); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // the import itself succeeded
                }
        }
        return json;
    }

    private sealed class Ctx
    {
        public Ctx(ContentsModel contents, ColorResolver colors, IReadOnlyDictionary<uint, Story> stories, IReadOnlyList<Blip?> blips, string assetDir,
            IReadOnlyList<EscherShape> top, List<ImportIssue> issues)
        {
            (Contents, Colors, Stories, Blips, AssetDir, Top, Issues) = (contents, colors, stories, blips, assetDir, top, issues);
            for (var i = 0; i < top.Count; i++)
            {
                if (top[i].Seqnum is not { } q) continue;
                if (!TopBySeqnum.TryGetValue(q, out var at)) TopBySeqnum[q] = at = [];
                at.Add(i);
            }
            void Walk(EscherShape s) { if (s.Seqnum is { } q) AllSeqnums.Add(q); foreach (var c in s.Children) Walk(c); }
            foreach (var s in top) Walk(s);
            foreach (var p in contents.Pages) PagesBySeqnum.TryAdd(p.Seqnum, p);
        }

        public ContentsModel Contents { get; }
        public ColorResolver Colors { get; }
        public IReadOnlyDictionary<uint, Story> Stories { get; }
        public IReadOnlyList<Blip?> Blips { get; }
        public string AssetDir { get; }
        public IReadOnlyList<EscherShape> Top { get; }
        /// <summary>Top-level shape indices (stream order) by seqnum, and every seqnum in the drawing, groups included.</summary>
        public Dictionary<uint, List<int>> TopBySeqnum { get; } = [];
        public HashSet<uint> AllSeqnums { get; } = [];
        public Dictionary<uint, PageInfo> PagesBySeqnum { get; } = [];
        public double PageWidth => Contents.PageWidthEmu / Emu;
        public double PageHeight => Contents.PageHeightEmu / Emu;
        public int PageNumber { get; set; }
        public List<ImportIssue> Issues { get; }
        public Dictionary<string, byte[]> Assets { get; } = [];
        public int Read { get; set; }
        /// <summary>Pages and elements placed so far, against <see cref="MaxElements"/>.</summary>
        public int Placed { get; private set; }
        /// <summary>The asset path of each picture already written, so entries sharing a picture share one file.</summary>
        public Dictionary<Blip, string> AssetPaths { get; } = new(ReferenceEqualityComparer.Instance);

        public void Place(int count)
        {
            Placed += count;
            if (Placed > MaxElements) throw new PubFormatException($"The publication places more than {MaxElements:N0} pages and elements.");
        }

        public long TextPlaced { get; private set; }

        public void PlaceText(long chars)
        {
            TextPlaced += chars;
            if (TextPlaced > MaxTextPlaced) throw new PubFormatException($"The publication places more than {MaxTextPlaced:N0} characters of text.");
        }
        public int Converted { get; set; }
        public int Placeheld { get; set; }
        /// <summary>Elements added as placeholders (identity set), so accounting can tell them from real ones.</summary>
        public HashSet<Element> Placeholders { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// Records an issue found while placing pages. Each costs a unit, so issues cannot outgrow the budget. (The
        /// publication-level issues found while parsing, a few with each kind reported once, are charged when
        /// composing starts.)
        /// </summary>
        public void Note(EscherShape s, ImportIssueKind kind, string detail, string what) => Report(new ImportIssue(PageNumber, s.Seqnum, what, kind, detail));

        public void Report(ImportIssue issue)
        {
            Issues.Add(issue);
            Place(1);
        }

        /// <summary>(Listing page, shape) pairs already reported as having no drawing: reported once, not per placement.</summary>
        public HashSet<(uint Page, uint Shape)> ReportedMissing { get; } = [];

        /// <summary>The shape whose text box placed each story: linked boxes share one story, which is placed once.</summary>
        public Dictionary<uint, uint?> StoryPlacedBy { get; } = [];

        /// <summary>A WordArt shape's text and font name, decoded once however often the shape is placed.</summary>
        public Dictionary<EscherShape, (string Text, string Font, bool FontCut)> WordArtStrings { get; } = new(ReferenceEqualityComparer.Instance);
    }

    /// <summary>A page background: its fill covers the whole page.</summary>
    private static void AddBackground(Ctx ctx, PageInfo page, List<Element> into)
    {
        if (page.BackgroundSeqnum is not { } bg || !ctx.TopBySeqnum.TryGetValue(bg, out var at)) return;
        var s = ctx.Top[at[0]];
        if (s.Flag(EscherShape.PropFillFlags, 4, true))
        {
            into.Add(new ShapeElement { Bounds = new Box(0, 0, ctx.PageWidth, ctx.PageHeight), Fill = FillColor(ctx, s, "page background") });
            ctx.Place(1);
        }
    }

    private static void AddShapes(Ctx ctx, PageInfo page, List<Element> into)
    {
        // The page lists its shapes by seqnum; they are drawn in drawing-stream order, which is the z-order.
        var indices = new List<int>();
        var seen = new HashSet<uint>();
        foreach (var q in page.ShapeSeqnums)
        {
            ctx.Place(1);   // every entry the page lists costs its look-up: found or not, repeated or not
            if (!seen.Add(q)) continue;
            if (ctx.TopBySeqnum.TryGetValue(q, out var at)) indices.AddRange(at);
            else if (!ctx.AllSeqnums.Contains(q) && ctx.ReportedMissing.Add((page.Seqnum, q)))
                ctx.Report(new ImportIssue(ctx.PageNumber, q, "shape", ImportIssueKind.Dropped, $"shape {q} is listed on the page but has no drawing; nothing imported"));
        }
        indices.Sort();
        foreach (var i in indices)
        {
            var s = ctx.Top[i];
            if (s.IsDeleted) { ctx.Place(1); continue; }   // removed in Publisher: not read, but walked, so charged
            ctx.Read++;
            var before = into.Count;
            Convert(ctx, s, ToFrame(ctx, s.Anchor), into);
            if (into.Skip(before).Any(e => !ctx.Placeholders.Contains(e))) ctx.Converted++; else ctx.Placeheld++;
        }
    }

    // ---------- geometry ----------

    private readonly record struct Frame(Box Box, double Rotation);

    /// <summary>A Publisher anchor (EMU edges from the page centre) as a page box in points. Edges may be any int.</summary>
    internal static Box BoxFromAnchor(AnchorEmu a, double pageWidth, double pageHeight) => new(
        pageWidth / 2 + Math.Min(a.Left, a.Right) / Emu, pageHeight / 2 + Math.Min(a.Top, a.Bottom) / Emu,
        Math.Abs((double)a.Right - a.Left) / Emu, Math.Abs((double)a.Bottom - a.Top) / Emu);

    private static Frame? ToFrame(Ctx ctx, AnchorEmu? a) => a is { } r ? new Frame(BoxFromAnchor(r, ctx.PageWidth, ctx.PageHeight), 0) : null;

    /// <summary>A group member's box: its child anchor, in the group's own coordinate system, mapped onto the group's frame.</summary>
    internal static Box ChildBox(AnchorEmu child, AnchorEmu groupCoordinates, Box outer)
    {
        var cs = groupCoordinates;
        double csW = Math.Max(1, (double)cs.Right - cs.Left), csH = Math.Max(1, (double)cs.Bottom - cs.Top);
        double X(int v) => outer.X + ((double)v - cs.Left) * (outer.Width / csW);
        double Y(int v) => outer.Y + ((double)v - cs.Top) * (outer.Height / csH);
        return new Box(X(Math.Min(child.Left, child.Right)), Y(Math.Min(child.Top, child.Bottom)),
            Math.Abs(X(child.Right) - X(child.Left)), Math.Abs(Y(child.Bottom) - Y(child.Top)));
    }

    private static double Normalize(double degrees) => ((degrees % 360) + 360) % 360;

    /// <summary>Normalised rotation, and the real frame: between 45-135 and 225-315 degrees the stored box is swapped.</summary>
    private static Frame Resolve(EscherShape s, Box stored)
    {
        var rot = Normalize(s.RotationDegrees);
        if (rot is >= 45 and < 135 or >= 225 and < 315)
            stored = new Box(stored.CenterX - stored.Height / 2, stored.CenterY - stored.Width / 2, stored.Height, stored.Width);
        return new Frame(stored, rot);
    }

    // ---------- conversion ----------

    private static void Convert(Ctx ctx, EscherShape s, Frame? stored, List<Element> into)
    {
        ctx.Place(1);              // every conversion counts: deleted shapes and those that draw nothing (no position, empty group) too
        if (s.IsDeleted) return;   // fDeleted: removed in Publisher, kept in the stream
        var info = s.Seqnum is { } q && ctx.Contents.Shapes.TryGetValue(q, out var si) ? si : null;
        var what = Describe(s, info);
        if (s.Truncated) ctx.Note(s, ImportIssueKind.Approximated, $"{what}: shape data truncated; some formatting may be missing", what);
        if (s.IsGroup) { ConvertGroup(ctx, s, stored, into); return; }
        if (stored is not { } st)
        {
            ctx.Note(s, ImportIssueKind.Dropped, $"{what} has no position; skipped", what);
            return;
        }
        var f = Resolve(s, st.Box);

        if (info?.ChunkType == 0x10) { Placeholder(ctx, s, f, into, "table (cells not supported yet)", what); return; }
        if (s.ShapeType is >= 136 and <= 175) { WordArt(ctx, s, f, into); return; }
        if (s.PictureIndex is not null && s.ShapeType is not (202)) { Picture(ctx, s, f, info, into); return; }
        if (s.ShapeType is 20 or 32) { Line(ctx, s, f, into); return; }

        // Rectangles and ovals; text boxes (202, or any of these carrying text); shape type 0 only as a text box.
        var isTextBox = s.ShapeType == 202 || info?.TextId is not null;
        if (s.ShapeType is 1 or 3 || isTextBox && s.ShapeType is 0 or 202)
        {
            Styled(ctx, s, f, into, s.ShapeType == 3 ? ShapeKind.Ellipse : ShapeKind.Rectangle, isTextBox);
            if (isTextBox) Text(ctx, s, f, info, into);
            return;
        }
        Placeholder(ctx, s, f, into, $"{what} (shape type {s.ShapeType} not supported yet)", what);
    }

    private static string Describe(EscherShape s, ShapeInfo? info) => s switch
    {
        { IsGroup: true } => "group",
        { ShapeType: >= 136 and <= 175 } => "WordArt",
        { PictureIndex: not null } => "picture",
        { ShapeType: 202 } => "text box",
        { ShapeType: 20 or 32 } => "line",
        _ when info?.ChunkType == 0x10 => "table",
        _ => $"shape {s.ShapeType}",
    };

    private static void ConvertGroup(Ctx ctx, EscherShape g, Frame? stored, List<Element> into)
    {
        if (g.Children.Count == 0) { ctx.Note(g, ImportIssueKind.Dropped, "group with no members; nothing to draw", "group"); return; }
        // Members are laid out in the group's real (unrotated) frame, then turned and mirrored with the group.
        var frame = stored is { } st ? Resolve(g, st.Box) : (Frame?)null;
        var members = new List<Element>();
        foreach (var child in g.Children)
        {
            Frame? childFrame = null;
            if (child.ChildAnchor is { } ca && g.GroupCoordinates is { } cs && frame is { } f) childFrame = new Frame(ChildBox(ca, cs, f.Box), 0);
            else if (child.Anchor is not null) childFrame = ToFrame(ctx, child.Anchor);
            Convert(ctx, child, childFrame, members);
        }
        if (g.FlipH || g.FlipV) ctx.Note(g, ImportIssueKind.Approximated, "flipped group: members are placed mirrored but drawn unflipped", "group");
        foreach (var e in members) into.Add(frame is { } gf ? Turn(ctx, e, gf, g.FlipH, g.FlipV) : e);
    }

    /// <summary>
    /// Moves a member element with its group: its centre is mirrored (flips), then rotated clockwise about the
    /// group's centre, and its own rotation follows (a mirror reverses it). Placeholder identity is carried over.
    /// </summary>
    private static Element Turn(Ctx ctx, Element e, Frame group, bool flipH, bool flipV)
    {
        if (group.Rotation == 0 && !flipH && !flipV) return e;
        double gx = group.Box.CenterX, gy = group.Box.CenterY;
        double dx = e.Bounds.CenterX - gx, dy = e.Bounds.CenterY - gy;
        var rotation = e.Rotation;
        if (flipH) { dx = -dx; rotation = -rotation; }
        if (flipV) { dy = -dy; rotation = -rotation; }
        var rad = group.Rotation * Math.PI / 180;
        double x = gx + dx * Math.Cos(rad) - dy * Math.Sin(rad), y = gy + dx * Math.Sin(rad) + dy * Math.Cos(rad);
        var turned = e with { Bounds = e.Bounds with { X = x - e.Bounds.Width / 2, Y = y - e.Bounds.Height / 2 }, Rotation = Normalize(rotation + group.Rotation) };
        if (ctx.Placeholders.Remove(e)) ctx.Placeholders.Add(turned);
        return turned;
    }

    private static void Styled(Ctx ctx, EscherShape s, Frame f, List<Element> into, ShapeKind kind, bool isTextBox)
    {
        var what = isTextBox ? "text box" : kind == ShapeKind.Ellipse ? "oval" : "rectangle";
        var filled = s.Flag(EscherShape.PropFillFlags, 4, true);
        var fill = filled ? FillColor(ctx, s, what) : (Rgba?)null;
        var stroke = LineOf(ctx, s, what);
        if (s.FlipH || s.FlipV) ctx.Note(s, ImportIssueKind.Approximated, $"{what}: flip ignored", what);
        if (fill is null && stroke is null && isTextBox) return;   // plain text box: the text element is the whole shape (and carries the shadow)
        into.Add(new ShapeElement { Bounds = f.Box, Rotation = f.Rotation, Kind = kind, Fill = fill, Stroke = stroke, Shadow = ShadowOf(ctx, s, what) });
    }

    /// <summary>
    /// OfficeArt offset shadow (MS-ODRAW shadow properties): on when 0x023F has use-bit 17 and fShadow bit 1.
    /// Defaults: grey (0x808080), fully opaque, 2 pt right and down (25,400 EMU). Only the plain offset type is
    /// drawn exactly; other types (double, perspective, emboss...) are reported.
    /// </summary>
    private static Shadow? ShadowOf(Ctx ctx, EscherShape s, string what)
    {
        if (!s.Flag(EscherShape.PropShadowFlags, 1, false)) return null;
        var type = s.Get(0x0200) ?? 0;
        if (type != 0) ctx.Note(s, ImportIssueKind.Approximated, $"{what}: shadow type {type} drawn as a plain offset shadow", what);
        var c = s.Get(0x0201) is { } raw ? ctx.Colors.Resolve(raw) : new RgbColor(128, 128, 128);
        var opacity = s.Get(0x0204) is { } op ? Math.Clamp(op / 65536.0, 0, 1) : 1.0;
        double Offset(ushort id) => (s.Get(id) is { } v ? (int)v : 25400) / Emu;
        return new Shadow(new Rgba(c.R, c.G, c.B, (byte)Math.Round(opacity * 255)), Offset(0x0205), Offset(0x0206));
    }

    /// <summary>MSOSPT WordArt text shapes (136-175) to warps; null = no equivalent yet (drawn straight, reported).</summary>
    internal static WarpKind? WarpFor(ushort spt) => spt switch
    {
        136 => WarpKind.None,
        138 => WarpKind.Triangle, 139 => WarpKind.TriangleInverted, 140 => WarpKind.Chevron, 141 => WarpKind.ChevronInverted,
        144 => WarpKind.ArchUp, 145 => WarpKind.ArchDown, 146 => WarpKind.Circle, 147 => WarpKind.Button,
        152 => WarpKind.CurveUp, 153 => WarpKind.CurveDown, 156 => WarpKind.Wave1, 157 => WarpKind.Wave2,
        160 => WarpKind.Inflate, 161 => WarpKind.Deflate, 162 => WarpKind.InflateBottom, 163 => WarpKind.DeflateBottom,
        164 => WarpKind.InflateTop, 165 => WarpKind.DeflateTop,
        172 => WarpKind.SlantUp, 173 => WarpKind.SlantDown, 174 => WarpKind.CanUp, 175 => WarpKind.CanDown,
        _ => null,
    };

    private static Rgba FillColor(Ctx ctx, EscherShape s, string what)
    {
        var type = s.Get(0x0180) ?? 0;
        if (type != 0) ctx.Note(s, ImportIssueKind.Approximated, $"{what}: gradient/pattern fill (type {type}) drawn as its first colour", what);
        var c = s.Get(EscherShape.PropFillColor) is { } raw ? ctx.Colors.Resolve(raw) : new RgbColor(255, 255, 255);
        if (c.Approximate) ctx.Note(s, ImportIssueKind.Approximated, $"{what}: fill colour reference not resolved exactly", what);
        var alpha = s.Get(EscherShape.PropFillOpacity) is { } op ? Math.Clamp(op / 65536.0, 0, 1) : 1.0;
        return new Rgba(c.R, c.G, c.B, (byte)Math.Round(alpha * 255));
    }

    /// <summary>
    /// Whether the shape's line/border is shown. Files from current Publisher say so in the primary line flags
    /// (use-bit 19 + fLine bit 3: 0x80000 off, 0x80008 on). Some older files leave those unset and keep the border
    /// in Publisher's per-side border flags (TertiaryFOPT 0x057F): bit 19 set = border shown (0x280020 on a
    /// bordered picture vs 0x200020 on unbordered ones in the same file).
    /// </summary>
    internal static bool LineVisible(EscherShape s)
    {
        if (s.Props.TryGetValue(EscherShape.PropLineFlags, out var v) && (v & (1u << 19)) != 0) return (v & (1u << 3)) != 0;
        if (s.Tertiary.TryGetValue(0x057F, out var side)) return (side & (1u << 19)) != 0;
        return true;
    }

    private static Stroke? LineOf(Ctx ctx, EscherShape s, string what)
    {
        if (!LineVisible(s)) return null;
        var width = (s.Get(EscherShape.PropLineWidth) ?? 9525) / Emu;
        if (width <= 0) return null;
        if ((s.Get(EscherShape.PropLineDashing) ?? 0) != 0) ctx.Note(s, ImportIssueKind.Approximated, $"{what}: dashed line drawn solid", what);
        var c = s.Get(EscherShape.PropLineColor) is { } raw ? ctx.Colors.Resolve(raw) : RgbColor.Black;
        var alpha = s.Get(EscherShape.PropLineOpacity) is { } op ? Math.Clamp(op / 65536.0, 0, 1) : 1.0;
        return new Stroke(new Rgba(c.R, c.G, c.B, (byte)Math.Round(alpha * 255)), width);
    }

    private static void Placeholder(Ctx ctx, EscherShape s, Frame f, List<Element> into, string detail, string what)
    {
        ctx.Note(s, ImportIssueKind.Placeholder, detail, what);
        var placeholder = new ShapeElement { Bounds = f.Box, Rotation = f.Rotation, Stroke = PlaceholderStroke };
        ctx.Placeholders.Add(placeholder);
        into.Add(placeholder);
    }

    private static void Line(Ctx ctx, EscherShape s, Frame f, List<Element> into)
    {
        // Endpoints are frame corners; flips choose the other diagonal; the shape's rotation turns the line about
        // the frame's centre. Drawn as a thin rotated rectangle.
        var box = f.Box;
        double x1 = box.X, y1 = box.Y, x2 = box.X + box.Width, y2 = box.Y + box.Height;
        if (s.FlipH) (x1, x2) = (x2, x1);
        if (s.FlipV) (y1, y2) = (y2, y1);
        var len = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
        var angle = Normalize(Math.Atan2(y2 - y1, x2 - x1) * 180 / Math.PI + f.Rotation);
        var (cx, cy) = (box.CenterX, box.CenterY);
        if (!LineVisible(s))
        {
            // A line set to "no line": present, but invisible.
            into.Add(new ShapeElement { Bounds = new Box(cx - len / 2, cy, len, 0), Rotation = angle });
            return;
        }
        var stroke = LineOf(ctx, s, "line") ?? new Stroke(new Rgba(0, 0, 0), 0.75);   // zero width: Publisher's hairline
        ctx.Note(s, ImportIssueKind.Approximated, "line drawn as a thin rectangle (arrowheads and caps not drawn)", "line");
        into.Add(new ShapeElement { Bounds = new Box(cx - len / 2, cy - stroke.Width / 2, len, stroke.Width), Rotation = angle, Fill = stroke.Color, Shadow = ShadowOf(ctx, s, "line") });
    }

    /// <summary>Crop fractions per axis may remove at most this much of the picture (the model needs some left).</summary>
    private const double MaxCropPerAxis = 0.99;

    private static void Picture(Ctx ctx, EscherShape s, Frame f, ShapeInfo? info, List<Element> into)
    {
        var index = s.PictureIndex!.Value;
        var blip = index - 1 < ctx.Blips.Count ? ctx.Blips[index - 1] : null;
        if (blip is null || !ctx.AssetPaths.TryGetValue(blip, out var rel))
        {
            var data = blip is null ? null : blip.Extension switch
            {
                "png" or "jpg" => blip.Data,
                "dib" => DibToBmp(blip.Data),
                _ => null,
            };
            if (data is null)
            {
                var why = blip is null ? "picture data missing or damaged" : blip.Extension == "dib" ? "picture data damaged (bitmap header)" : $"picture format {blip.Extension} not supported yet";
                Placeholder(ctx, s, f, into, why, "picture");
                return;
            }
            rel = $"{ctx.AssetDir}/img{index}.{(blip!.Extension == "dib" ? "bmp" : blip.Extension)}";
            ctx.Assets[rel] = data;
            ctx.AssetPaths[blip] = rel;
        }
        ctx.PlaceText(rel.Length);   // the path is written again with every picture placed, and it carries the file's name

        double Frac(ushort id) => s.Get(id) is { } v ? (int)v / 65536.0 : 0;
        double t = Frac(EscherShape.PropCropTop), b = Frac(EscherShape.PropCropBottom), l = Frac(EscherShape.PropCropLeft), r = Frac(EscherShape.PropCropRight);
        Crop? crop = null;
        if (t != 0 || b != 0 || l != 0 || r != 0)
        {
            // Negative crops (Publisher's outset) and crops that leave nothing cannot be drawn: clamp and say so.
            static (double, double) Fit(double a, double z)
            {
                (a, z) = (Math.Max(0, a), Math.Max(0, z));
                return a + z <= MaxCropPerAxis ? (a, z) : (a * MaxCropPerAxis / (a + z), z * MaxCropPerAxis / (a + z));
            }
            var (cl, cr) = Fit(l, r);
            var (ct, cb) = Fit(t, b);
            if ((cl, ct, cr, cb) != (l, t, r, b)) ctx.Note(s, ImportIssueKind.Approximated, "picture: negative or extreme crop clamped", "picture");
            crop = new Crop(cl, ct, cr, cb);
        }
        ShapeKind? maskKind = null;
        if (info?.CropShapeType is { } mask && mask != 1)
        {
            if (mask == 3) maskKind = ShapeKind.Ellipse;   // MSOSPT 3 = ellipse
            else ctx.Note(s, ImportIssueKind.Approximated, $"picture: crop-to-shape {mask} drawn as a rectangle", "picture");
        }
        if (s.FlipH || s.FlipV) ctx.Note(s, ImportIssueKind.Approximated, "picture: flip ignored", "picture");
        into.Add(new ImageElement { Bounds = f.Box, Rotation = f.Rotation, Source = rel, Crop = crop, Stroke = LineOf(ctx, s, "picture"), Mask = maskKind, Shadow = ShadowOf(ctx, s, "picture") });
    }

    /// <summary>
    /// A DIB (BITMAPINFOHEADER or later) as a .bmp file: a BITMAPFILEHEADER is prepended, pointing past the info
    /// header and palette. Null when the header is truncated or inconsistent with the data.
    /// </summary>
    internal static byte[]? DibToBmp(byte[] dib)
    {
        if (dib.Length < 40) return null;
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        if (headerSize < 40 || headerSize > dib.Length) return null;
        var bpp = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14));
        var used = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(32));
        if (used < 0) return null;
        long palette = used > 0 ? used : bpp <= 8 ? 1 << bpp : 0;
        var pixelOffset = 14 + headerSize + palette * 4;
        if (pixelOffset > 14L + dib.Length) return null;
        var bmp = new byte[14 + dib.Length];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), (int)pixelOffset);
        dib.CopyTo(bmp, 14);
        return bmp;
    }

    private static void WordArt(Ctx ctx, EscherShape s, Frame f, List<Element> into)
    {
        if (!ctx.WordArtStrings.TryGetValue(s, out var strings))
        {
            var name = s.ComplexString(EscherShape.PropGtextFont) ?? "Arial";
            strings = (s.ComplexString(EscherShape.PropGtextUnicode) ?? "", name.Length > MaxFontName ? name[..MaxFontName] : name, name.Length > MaxFontName);
            ctx.WordArtStrings[s] = strings;
        }
        var (text, font, cut) = strings;
        ctx.Place(1 + text.Length / 1024);
        ctx.PlaceText(text.Length + Math.Max(0, font.Length - FreeFontNameChars));
        if (cut) ctx.Note(s, ImportIssueKind.Approximated, $"WordArt: font name longer than {MaxFontName} characters cut", "WordArt");
        // Unfilled WordArt shows only its outline.
        var fill = s.Flag(EscherShape.PropFillFlags, 4, true) ? FillColor(ctx, s, "WordArt") : new Rgba(0, 0, 0, 0);
        // WordArt stretches its text to fill the frame: Fit = Stretch reproduces that; the outline is its line.
        var warp = WarpFor(s.ShapeType);
        if (warp is null) ctx.Note(s, ImportIssueKind.Approximated, $"WordArt warp (shape {s.ShapeType}) drawn straight", "WordArt");
        if (warp == WarpKind.Button) ctx.Note(s, ImportIssueKind.Approximated, "WordArt: button warp drawn as an arch (its middle and lower lines are not)", "WordArt");
        if (s.Get(0x0147) is not null) ctx.Note(s, ImportIssueKind.Approximated, "WordArt: custom warp amount drawn with the preset default", "WordArt");
        var style = new TextStyle { Family = font, Size = 36, Bold = s.Flag(PropGtextFlags, 5, false), Italic = s.Flag(PropGtextFlags, 4, false), Color = fill };
        into.Add(new TextElement
        {
            Bounds = f.Box, Rotation = f.Rotation, Insets = new Insets(0, 0, 0, 0), Fit = TextFit.Stretch, Outline = LineOf(ctx, s, "WordArt"),
            Warp = warp is { } w && w != WarpKind.None ? new TextWarp(w) : null, Shadow = ShadowOf(ctx, s, "WordArt"),
            Paragraphs = [new Paragraph { Alignment = TextAlignment.Center, SpaceAfter = 0, LineSpacing = 1, Runs = [new TextRun(text, style)] }],
        });
    }

    private static void Text(Ctx ctx, EscherShape s, Frame f, ShapeInfo? info, List<Element> into)
    {
        double In(ushort id) => (s.Get(id) ?? 36576) / Emu;
        var insets = new Insets(In(EscherShape.PropTextLeft), In(EscherShape.PropTextTop), In(EscherShape.PropTextRight), In(EscherShape.PropTextBottom));
        var valign = info?.VerticalAlign switch { 1 => TextVerticalAlign.Middle, 2 => TextVerticalAlign.Bottom, _ => TextVerticalAlign.Top };
        var paragraphs = new List<Paragraph>();
        if (info?.TextId is { } linked && ctx.Stories.ContainsKey(linked) && ctx.StoryPlacedBy.TryGetValue(linked, out var owner) && owner != s.Seqnum)
        {
            // Linked text boxes share one story. Text does not flow from box to box yet, so the story is placed in the
            // first box that shows it and the rest of the chain stays empty. (A master's box repeated on many pages is
            // the same shape, not a linked box: it shows its story on every page.)
            ctx.Note(s, ImportIssueKind.Approximated, "linked text box: its story is placed in the first box that shows it (text flow between linked boxes is not reproduced yet)", "text box");
        }
        else if (info?.TextId is { } id && ctx.Stories.TryGetValue(id, out var story))
        {
            ctx.StoryPlacedBy.TryAdd(id, s.Seqnum);
            ctx.Place(story.Paragraphs.Sum(p => 1 + p.Runs.Count + p.Runs.Sum(r => r.Text.Length) / 1024));
            ctx.PlaceText(story.Paragraphs.Sum(p => p.Runs.Sum(r => (long)r.Text.Length + Math.Max(0, Math.Min(r.Format.Font.Length, MaxFontName) - FreeFontNameChars))));
            foreach (var p in story.Paragraphs)
            {
                var pf = p.Format;
                if (pf.LeftIndentPt != 0 || pf.RightIndentPt != 0 || pf.FirstIndentPt != 0)
                    ctx.Note(s, ImportIssueKind.Approximated, "text: paragraph indents not applied", "text box");
                paragraphs.Add(new Paragraph
                {
                    Alignment = pf.Alignment switch { ParagraphAlign.Center => TextAlignment.Center, ParagraphAlign.Right => TextAlignment.Right, ParagraphAlign.Justify => TextAlignment.Justify, _ => TextAlignment.Left },
                    LineSpacing = pf.LineSpacing.Kind == LineSpacingKind.Multiple ? pf.LineSpacing.Value : 1,
                    ExactLineSpacing = pf.LineSpacing.Kind == LineSpacingKind.Exact ? pf.LineSpacing.Value : null,
                    SpaceBefore = pf.SpaceBeforePt,
                    SpaceAfter = pf.SpaceAfterPt,
                    Runs = p.Runs.Select(r => ToRun(ctx, s, r)).ToList(),
                });
            }
        }
        else if (info?.TextId is not null)
            ctx.Note(s, ImportIssueKind.Approximated, "text box: its text story was not found", "text box");
        // A text box drawn as a shape carries the shadow there; a bare text box casts it from its glyphs.
        var bare = !s.Flag(EscherShape.PropFillFlags, 4, true) && !LineVisible(s);
        into.Add(new TextElement { Bounds = f.Box, Rotation = f.Rotation, Insets = insets, VerticalAlign = valign, Paragraphs = paragraphs, Shadow = bare ? ShadowOf(ctx, s, "text box") : null });
    }

    private static TextRun ToRun(Ctx ctx, EscherShape s, StoryRun r)
    {
        var fm = r.Format;
        var family = fm.Font;
        if (family.Length > MaxFontName)
        {
            family = family[..MaxFontName];
            ctx.Note(s, ImportIssueKind.Approximated, $"text: font name longer than {MaxFontName} characters cut", "text box");
        }
        if (r.Text.Length > 0)   // an empty paragraph's run only sets its height: nothing visible is approximated
        {
            if (fm.Underline != 0) ctx.Note(s, ImportIssueKind.Approximated, "text: underline not drawn", "text box");
            if (fm.SmallCaps) ctx.Note(s, ImportIssueKind.Approximated, "text: small caps drawn as normal case", "text box");
            if (fm.SuperSub != 0) ctx.Note(s, ImportIssueKind.Approximated, "text: superscript/subscript drawn on the baseline", "text box");
            if (fm.Color.Approximate) ctx.Note(s, ImportIssueKind.Approximated, "text: colour reference not resolved exactly", "text box");
        }
        return new TextRun(r.Text, new TextStyle { Family = family, Size = fm.SizePt, Bold = fm.Bold, Italic = fm.Italic, AllCaps = fm.AllCaps, Color = new Rgba(fm.Color.R, fm.Color.G, fm.Color.B) });
    }
}
