using System.Globalization;
using Pubsmith.Core;
using SkiaSharp;

namespace Pubsmith.Rendering;

/// <summary>
/// What one <see cref="RenderContext"/> may spend. Counted limits (pages, PNG pixels, a PDF's typefaces, font names)
/// are checked before anything is written; the drawing time is metered while drawing; per-element limits are checked
/// before each element is drawn. A render over a limit throws <see cref="RenderLimitException"/>, and every later
/// render with that context throws too. The defaults sit far above ordinary publications (WI-006 AC5).
/// </summary>
public sealed record RenderLimits
{
    /// <summary>The defaults, which a context uses unless it is given others.</summary>
    public static RenderLimits Default { get; } = new();

    /// <summary>Pages written in one context, PDF and PNG together.</summary>
    public int MaxPages { get; init; } = 20_000;

    /// <summary>PNG output pixels in one context.</summary>
    public long MaxPngPixels { get; init; } = 20_000_000_000;

    /// <summary>Distinct resolved typefaces of one PDF's text boxes (WordArt is drawn as paths and is not counted).</summary>
    public int MaxPdfTypefaces { get; init; } = 36;

    /// <summary>Distinct font names (cut to 256 characters, ignoring case) in one context.</summary>
    public int MaxFontNames { get; init; } = 4_096;

    /// <summary>Characters of one text element, WordArt included.</summary>
    public int MaxTextBoxCharacters { get; init; } = 8_388_608;

    /// <summary>Runs of one text element, WordArt included; a paragraph without runs counts as one.</summary>
    public int MaxTextBoxRuns { get; init; } = 262_144;

    /// <summary>Glyphs of one WordArt.</summary>
    public int MaxWordArtGlyphs { get; init; } = 16_384;

    /// <summary>Wall-clock time spent drawing in one context (not PNG encoding, a PDF page's end or a PDF's close).</summary>
    public TimeSpan MaxDrawTime { get; init; } = TimeSpan.FromSeconds(600);

    /// <summary>Bytes of one PDF.</summary>
    public long MaxPdfBytes { get; init; } = 2L << 30;

    /// <summary>Encoded PNG bytes in one context.</summary>
    public long MaxPngBytes { get; init; } = 16L << 30;

    /// <summary>Pixels of a picture that counts as decoded in a PDF (anything but a JPEG passed through whole); a larger one is a placeholder in that PDF.</summary>
    public long MaxPdfPicturePixels { get; init; } = 64_000_000;

    /// <summary>Pixels of one shadow layer in a PDF, at 1 pixel per point.</summary>
    public long MaxPdfLayerPixels { get; init; } = 64_000_000;

    /// <summary>Bytes of a picture file; a larger one is not read and is a placeholder.</summary>
    public long MaxPictureFileBytes { get; init; } = 256L << 20;

    /// <summary>
    /// Refuses a command over a counted limit before anything is written, given what the context has already
    /// counted: exactly what <see cref="Exporter.ToPdf"/> and <see cref="Exporter.ToPng"/> will add for these
    /// outputs. Charges nothing.
    /// </summary>
    public static void Check(PubsmithDocument document, RenderOutputs outputs, RenderContext context)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(context);
        context.ThrowIfDisposed();
        context.ThrowIfRefused();
        var limits = context.Limits;
        context.CheckFontNames(document);   // before any font is resolved

        long n = document.Pages.Count;
        var pages = context.PagesWritten + (outputs.Pdf ? n : 0) + (outputs.PngDpi is null ? 0 : n);
        if (pages > limits.MaxPages) throw new RenderLimitException(RenderLimitException.Pages, pages, limits.MaxPages);
        if (outputs.PngDpi is { } dpi)
        {
            var pixels = context.PngPixelsWritten;
            foreach (var page in document.Pages)
            {
                var (w, h) = Exporter.PixelSize(page, dpi);
                pixels += (long)w * h;
            }
            if (pixels > limits.MaxPngPixels) throw new RenderLimitException(RenderLimitException.PngPixels, pixels, limits.MaxPngPixels);
        }
        if (outputs.Pdf)
        {
            var faces = PdfTypefaces(document, context, count: false);
            if (faces > limits.MaxPdfTypefaces) throw new RenderLimitException(RenderLimitException.PdfTypefaces, faces, limits.MaxPdfTypefaces);
        }
    }

    /// <summary>A font name as the renderer uses it: cut to 256 characters, as the importer cuts it.</summary>
    internal static string CutFontName(string family) => family.Length > MaxFontNameLength ? family[..MaxFontNameLength] : family;

    internal const int MaxFontNameLength = 256;

    /// <summary>
    /// The font names drawing a text element resolves, cut: its runs' families, and the default style's for a
    /// paragraph without runs (an empty paragraph takes that style's height).
    /// </summary>
    internal static IEnumerable<string> FontNamesOf(TextElement t)
    {
        foreach (var p in t.Paragraphs)
        {
            if (p.Runs.Count == 0) yield return CutFontName(new TextStyle().Family);
            foreach (var r in p.Runs) yield return CutFontName(r.Style.Family);
        }
    }

    /// <summary>
    /// The distinct typefaces a PDF of <paramref name="document"/> embeds for its text boxes (WordArt is drawn as
    /// paths): each (name, bold, italic) resolved once through the context's fonts, compared by family and style.
    /// With <paramref name="count"/>, each name is counted against <see cref="MaxFontNames"/> as it is resolved.
    /// </summary>
    internal static int PdfTypefaces(PubsmithDocument document, RenderContext context, bool count)
    {
        var keys = new HashSet<(string, bool, bool)>();
        var faces = new HashSet<(string, int, int, SKFontStyleSlant)>();
        foreach (var t in document.Pages.SelectMany(p => p.Elements).OfType<TextElement>())
        {
            if (t.Fit == TextFit.Stretch) continue;
            foreach (var style in t.Paragraphs.SelectMany(p => p.Runs).Select(r => r.Style))
            {
                var family = CutFontName(style.Family);
                if (!keys.Add((family.ToUpperInvariant(), style.Bold, style.Italic))) continue;
                var tf = (count ? context.ResolveFont(family, style.Bold, style.Italic) : context.Fonts.Resolve(family, style.Bold, style.Italic)).Typeface;
                faces.Add((tf.FamilyName, tf.FontStyle.Weight, tf.FontStyle.Width, tf.FontStyle.Slant));
            }
        }
        return faces.Count;
    }
}

/// <summary>The outputs of one command: a PDF, and PNG pages at a resolution.</summary>
public sealed record RenderOutputs(bool Pdf, double? PngDpi);

/// <summary>A render refused or stopped by one of its context's <see cref="RenderLimits"/>.</summary>
public sealed class RenderLimitException : Exception
{
    public const string Pages = "pages";
    public const string PngPixels = "png-pixels";
    public const string PdfTypefaces = "pdf-typefaces";
    public const string FontNames = "font-names";
    public const string TextBoxCharacters = "text-box-characters";
    public const string TextBoxRuns = "text-box-runs";
    public const string WordArtGlyphs = "wordart-glyphs";
    public const string DrawTime = "draw-time";
    public const string PdfBytes = "pdf-bytes";
    public const string PngBytes = "png-bytes";
    public const string PdfLayerPixels = "pdf-layer-pixels";

    internal RenderLimitException(string limit, long value, long maximum, int? page = null, int? element = null)
        : this(limit, value, page, element, Describe(limit, value, maximum, page, element))
    {
    }

    private RenderLimitException(string limit, long value, int? page, int? element, string message) : base(message)
    {
        Limit = limit;
        Value = value;
        Page = page;
        Element = element;
    }

    /// <summary>The same refusal, thrown again by a later render with the refused context.</summary>
    internal RenderLimitException Again() => new(Limit, Value, Page, Element, Message);

    /// <summary>Which limit: one of this class's constants.</summary>
    public string Limit { get; }

    /// <summary>The amount that passed the limit (milliseconds for <see cref="DrawTime"/>).</summary>
    public long Value { get; }

    /// <summary>The page (from 1) when known.</summary>
    public int? Page { get; }

    /// <summary>The element on that page (from 1) when known.</summary>
    public int? Element { get; }

    private static string Describe(string limit, long value, long maximum, int? page, int? element)
    {
        var what = limit switch
        {
            Pages => "pages written (PDF and PNG together)",
            PngPixels => "PNG pixels",
            PdfTypefaces => "typefaces in one PDF's text",
            FontNames => "distinct font names",
            TextBoxCharacters => "characters in one text element",
            TextBoxRuns => "runs in one text element",
            WordArtGlyphs => "glyphs in one WordArt",
            DrawTime => "drawing time in milliseconds",
            PdfBytes => "PDF bytes",
            PngBytes => "PNG bytes",
            PdfLayerPixels => "pixels in one PDF shadow layer",
            _ => limit,
        };
        var where = page is null ? "" : element is null ? $" on page {page}" : $" on page {page}, element {element}";
        return string.Create(CultureInfo.InvariantCulture, $"Rendering refused{where}: {value:N0} {what}, over the limit of {maximum:N0} ({limit}).");
    }
}
