using Pubsmith.Core;
using SkiaSharp;

namespace Pubsmith.Rendering;

/// <summary>Something the renderer had to work around. Rendering continues; the caller decides what to do.</summary>
public sealed record RenderWarning(string Code, string Message)
{
    public const string FontSubstituted = "font-substituted";
    public const string ImageMissing = "image-missing";
    /// <summary>The image file could not be read (an I/O error), or it could not be decoded; the message says which.</summary>
    public const string ImageUnreadable = "image-unreadable";
    /// <summary>The image path is outside the document's folder, a network or device path, or not a valid path; it was not opened.</summary>
    public const string ImageRefused = "image-refused";
    /// <summary>The image declares more pixels than <see cref="RenderContext.MaxPicturePixels"/>; it was read but not decoded.</summary>
    public const string ImageTooLarge = "image-too-large";
}

/// <summary>The kind of canvas being drawn: a caller's own (through <see cref="PageRenderer.Render"/>), or the exporters' bitmap or PDF.</summary>
internal enum RenderTarget { Canvas, Bitmap, Pdf }

/// <summary>
/// Per-render settings, the warnings collected while rendering, and the pictures opened, with decoded copies for
/// bitmaps (kept within <see cref="PictureCacheBudget"/>).
/// A context keeps what it first found for each picture file: it does not see the file change, or appear after it was
/// missing; only a file it could not read (an I/O error, such as another program's lock) is tried again. So use a new
/// context after pictures change. One context per command, used on one thread; dispose it to release its
/// pictures (a context never disposed releases them when it is collected). After <see cref="Dispose"/>, rendering
/// with it throws <see cref="ObjectDisposedException"/>.
/// </summary>
public sealed class RenderContext(string? baseDirectory = null, FontResolver? fonts = null) : IDisposable
{
    /// <summary>A picture declaring more pixels than this is drawn as a placeholder, never decoded (no real photo comes near).</summary>
    public const long MaxPicturePixels = 250_000_000;

    /// <summary>Decoded pictures kept for bitmap output, in pixels (4 bytes each); a larger picture is drawn uncached.</summary>
    public const long PictureCacheBudget = 100_000_000;

    /// <summary>A context with its own <see cref="RenderLimits"/>, and the clock its drawing time is read from.</summary>
    public RenderContext(string? baseDirectory, RenderLimits limits, TimeProvider? timeProvider = null, FontResolver? fonts = null) : this(baseDirectory, fonts)
    {
        ArgumentNullException.ThrowIfNull(limits);
        Limits = limits;
        Time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Limits other than the defaults, for tests.</summary>
    internal RenderContext(string? baseDirectory, long maxPicturePixels, long cacheBudget, FontResolver? fonts = null) : this(baseDirectory, fonts)
        => Pictures = new PictureCache(maxPicturePixels, cacheBudget);

    /// <summary>What this context may spend.</summary>
    public RenderLimits Limits { get; } = RenderLimits.Default;

    /// <summary>The clock drawing time is read from.</summary>
    internal TimeProvider Time { get; } = TimeProvider.System;

    // What this context has counted (WI-006); counters only grow.
    private readonly HashSet<string> _fontNames = new(StringComparer.Ordinal);
    private RenderLimitException? _refusal;

    /// <summary>Pages written, PDF and PNG together (or refused only after this count included them).</summary>
    internal long PagesWritten { get; private set; }

    internal long PngPixelsWritten { get; private set; }

    internal long PngBytesWritten { get; private set; }

    /// <summary>After any <see cref="RenderLimitException"/>, every later render with this context throws it again, before it writes.</summary>
    internal void ThrowIfRefused()
    {
        if (_refusal is { } r) throw r.Again();
    }

    /// <summary>A refusal to throw; the first one is remembered, so the context stays refused.</summary>
    internal RenderLimitException Refuse(string limit, long value, long maximum, int? page = null, int? element = null)
    {
        var ex = new RenderLimitException(limit, value, maximum, page, element);
        _refusal ??= ex;
        return ex;
    }

    /// <summary>Resolves a font, counting its name (cut to 256 characters, ignoring case) against <see cref="RenderLimits.MaxFontNames"/> first.</summary>
    internal ResolvedFont ResolveFont(string family, bool bold, bool italic)
    {
        family = RenderLimits.CutFontName(family);
        if (_fontNames.Add(family.ToUpperInvariant()) && _fontNames.Count > Limits.MaxFontNames)
            throw Refuse(RenderLimitException.FontNames, _fontNames.Count, Limits.MaxFontNames);
        return Fonts.Resolve(family, bold, italic);
    }

    /// <summary>Refuses a document whose names, with those already counted, pass the limit; stops at the limit plus one, resolves nothing, counts nothing.</summary>
    internal void CheckFontNames(PubsmithDocument document)
    {
        var added = new HashSet<string>(StringComparer.Ordinal);
        foreach (var family in document.Pages.SelectMany(p => p.Elements).OfType<TextElement>().SelectMany(RenderLimits.FontNamesOf))
        {
            var name = family.ToUpperInvariant();
            if (!_fontNames.Contains(name) && added.Add(name) && _fontNames.Count + added.Count > Limits.MaxFontNames)
                throw new RenderLimitException(RenderLimitException.FontNames, _fontNames.Count + added.Count, Limits.MaxFontNames);
        }
    }

    /// <summary>
    /// A text element's characters, runs, WordArt glyphs (Unicode scalar values, as the glyphs are made) and font
    /// names, checked before it is laid out or built; its names are counted against <see cref="RenderLimits.MaxFontNames"/>.
    /// A paragraph without runs counts as one run: it is laid out as an empty run and resolves a font as one. The runs
    /// are refused as they are counted, so this walk and every walk after it (the glyphs, the names, and a WordArt's
    /// walk to its first run) is at most the limit plus one paragraph long: an element of millions of empty paragraphs,
    /// which no other count sees, is refused at once.
    /// </summary>
    internal void CheckText(TextElement t, int? page, int element)
    {
        long chars = 0, runs = 0;
        foreach (var p in t.Paragraphs)
        {
            ParagraphsChecked++;
            runs += Math.Max(1, p.Runs.Count);
            if (runs > Limits.MaxTextBoxRuns) throw Refuse(RenderLimitException.TextBoxRuns, runs, Limits.MaxTextBoxRuns, page, element);
            foreach (var r in p.Runs) chars += r.Text.Length;
        }
        if (chars > Limits.MaxTextBoxCharacters) throw Refuse(RenderLimitException.TextBoxCharacters, chars, Limits.MaxTextBoxCharacters, page, element);
        if (t.Fit == TextFit.Stretch)
        {
            long glyphs = 0;
            foreach (var r in t.Paragraphs.SelectMany(p => p.Runs))
                foreach (var _ in r.Text.EnumerateRunes()) glyphs++;
            if (glyphs > Limits.MaxWordArtGlyphs) throw Refuse(RenderLimitException.WordArtGlyphs, glyphs, Limits.MaxWordArtGlyphs, page, element);
        }
        foreach (var family in RenderLimits.FontNamesOf(t))
            if (_fontNames.Add(family.ToUpperInvariant()) && _fontNames.Count > Limits.MaxFontNames)
                throw Refuse(RenderLimitException.FontNames, _fontNames.Count, Limits.MaxFontNames, page, element);
    }

    /// <summary>Runs laid out by text boxes (diagnostics and tests).</summary>
    internal int RunsLaidOut { get; set; }

    // Diagnostics (tests): the steps begun, each after its checkpoint.
    internal int ParagraphsLaidOut { get; set; }
    internal int LinesDrawn { get; set; }
    internal int GlyphRunsDrawn { get; set; }
    internal int WordArtRunsBuilt { get; set; }
    internal int WordArtGlyphsBuilt { get; set; }
    internal int WordArtGlyphsPainted { get; set; }
    internal int PicturesDrawn { get; set; }
    internal int CharactersLaidOut { get; set; }
    internal int LinesLaidOut { get; set; }
    internal int WordArtGlyphsPlaced { get; set; }
    internal int WordsWrapped { get; set; }
    internal int PieceChunksMeasured { get; set; }
    internal int GlyphChunksPositioned { get; set; }
    internal int GlyphAdvancesMeasured { get; set; }
    internal int GlyphlessPiecesSkipped { get; set; }
    internal int ParagraphsChecked { get; set; }

    private GlyphAdvances? _advances;

    /// <summary>The table of a long piece's distinct glyphs' advances, one per context, reused from piece to piece.</summary>
    internal GlyphAdvances Advances => _advances ??= new GlyphAdvances();
    internal int WordArtChunksMeasured { get; set; }
    internal int WordArtGlyphsLaid { get; set; }

    // ---------- the drawing-time meter (WI-006 AC3) ----------
    private int _drawing;          // PageRenderer.Render calls in progress: the outermost starts and stops the meter
    private long _lastReading;     // the clock's timestamp at the last start or checkpoint
    private long _drawTicks;       // drawing time so far, in the clock's timestamp units
    private long _longestTicks;    // the longest time between two readings
    private int? _page, _element;  // where drawing is, for a stop's message

    /// <summary>The time spent drawing with this context so far.</summary>
    internal TimeSpan DrawTime => TimeSpan.FromSeconds(_drawTicks / (double)Time.TimestampFrequency);

    /// <summary>The longest time between two readings of the clock while drawing: the longest step (diagnostics and calibration).</summary>
    internal TimeSpan LongestStep => TimeSpan.FromSeconds(_longestTicks / (double)Time.TimestampFrequency);

    /// <summary>Starts the meter (the outermost render only).</summary>
    internal void StartDrawing(int? page)
    {
        if (_drawing == 0)
        {
            _lastReading = Time.GetTimestamp();
            _page = page;
            _element = null;
        }
        _drawing++;
    }

    /// <summary>Stops the meter, adding the time since the last reading (the outermost render only; called in a finally).</summary>
    internal void StopDrawing()
    {
        if (--_drawing == 0) Add(Time.GetTimestamp());
    }

    private void Add(long now)
    {
        var step = now - _lastReading;
        _drawTicks += step;
        _longestTicks = Math.Max(_longestTicks, step);
        _lastReading = now;
    }

    /// <summary>The element (from 1) now being drawn, named by a stop.</summary>
    internal void AtElement(int element) => _element = element;

    /// <summary>Adds the time since the last reading; past <see cref="RenderLimits.MaxDrawTime"/>, stops the render.</summary>
    internal void Checkpoint()
    {
        Add(Time.GetTimestamp());
        var spent = DrawTime;
        if (spent > Limits.MaxDrawTime)
            throw Refuse(RenderLimitException.DrawTime, (long)spent.TotalMilliseconds, (long)Limits.MaxDrawTime.TotalMilliseconds, _page, _element);
        if (PdfOutput is { Count: var bytes } && bytes > Limits.MaxPdfBytes)
            throw Refuse(RenderLimitException.PdfBytes, bytes, Limits.MaxPdfBytes, _page, _element);
    }

    /// <summary>A PDF's pages and typefaces, checked before anything is written; its pages are then counted.</summary>
    internal void CountPdf(PubsmithDocument document)
    {
        var pages = PagesWritten + document.Pages.Count;
        if (pages > Limits.MaxPages) throw Refuse(RenderLimitException.Pages, pages, Limits.MaxPages);
        var faces = RenderLimits.PdfTypefaces(document, this, count: true);
        if (faces > Limits.MaxPdfTypefaces) throw Refuse(RenderLimitException.PdfTypefaces, faces, Limits.MaxPdfTypefaces);
        PagesWritten = pages;
    }

    /// <summary>A bitmap page, checked before it is allocated, then counted.</summary>
    internal void CountBitmapPage(long pixels)
    {
        if (PagesWritten + 1 > Limits.MaxPages) throw Refuse(RenderLimitException.Pages, PagesWritten + 1, Limits.MaxPages);
        if (PngPixelsWritten + pixels > Limits.MaxPngPixels) throw Refuse(RenderLimitException.PngPixels, PngPixelsWritten + pixels, Limits.MaxPngPixels);
        PagesWritten++;
        PngPixelsWritten += pixels;
    }

    /// <summary>An encoded PNG page, checked before it is written, then counted.</summary>
    internal void CountPngBytes(long bytes)
    {
        if (PngBytesWritten + bytes > Limits.MaxPngBytes) throw Refuse(RenderLimitException.PngBytes, PngBytesWritten + bytes, Limits.MaxPngBytes);
        PngBytesWritten += bytes;
    }

    private readonly List<RenderWarning> _warnings = [];
    private bool _disposed;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <summary>
    /// The document's folder. Relative image paths resolve against it, and when it is set, only images inside it
    /// are opened: a document from someone else cannot make the renderer read other files or reach another host
    /// (opening a network path can send the user's Windows credentials to it). Without it, relative paths resolve
    /// against the working directory and local absolute paths are allowed (programmatic use), but network and
    /// device paths (anything starting <c>\\</c> or <c>\??\</c>) are refused.
    /// </summary>
    public string? BaseDirectory { get; } = baseDirectory is null ? null : Path.GetFullPath(baseDirectory);

    public FontResolver Fonts { get; } = fonts ?? new FontResolver();

    public IReadOnlyList<RenderWarning> Warnings => _warnings;

    internal PictureCache Pictures { get; private set; } = new(MaxPicturePixels, PictureCacheBudget);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Pictures.Dispose();
    }

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>How many times WordArt glyph paths were built (diagnostics and tests).</summary>
    internal int GlyphBuilds { get; set; }

    /// <summary>
    /// A shadow's layer in a PDF, at 1 pixel per point, as SkPDF allocates it: its device bounds clipped to the device
    /// clip and rounded out. Refused past <see cref="RenderLimits.MaxPdfLayerPixels"/>, or when the bounds are not finite.
    /// </summary>
    internal void CheckPdfLayer(SKRect device, SKRectI clip)
    {
        if (!(float.IsFinite(device.Left) && float.IsFinite(device.Top) && float.IsFinite(device.Right) && float.IsFinite(device.Bottom)))
            throw Refuse(RenderLimitException.PdfLayerPixels, long.MaxValue, Limits.MaxPdfLayerPixels, _page, _element);
        double l = Math.Max(device.Left, clip.Left), t = Math.Max(device.Top, clip.Top);
        double r = Math.Min(device.Right, clip.Right), b = Math.Min(device.Bottom, clip.Bottom);
        if (r <= l || b <= t) return;   // nothing of it on the page
        var pixels = (Math.Ceiling(r) - Math.Floor(l)) * (Math.Ceiling(b) - Math.Floor(t));
        if (pixels > Limits.MaxPdfLayerPixels)
            throw Refuse(RenderLimitException.PdfLayerPixels, (long)Math.Min(pixels, long.MaxValue), Limits.MaxPdfLayerPixels, _page, _element);
    }

    /// <summary>The PDF being written while <see cref="Exporter.ToPdf"/> runs; its bytes are checked at each checkpoint.</summary>
    internal CountingStream? PdfOutput { get; set; }

    /// <summary>The PDF's bytes after each page's end, the last of them before the close (diagnostics and tests).</summary>
    internal List<long> PdfBytesAtChecks { get; } = [];

    /// <summary>After a page's end, which is also before the close: past <see cref="RenderLimits.MaxPdfBytes"/>, the PDF is refused.</summary>
    internal void CheckPdfBytes(int page)
    {
        var bytes = PdfOutput!.Count;
        PdfBytesAtChecks.Add(bytes);
        if (bytes > Limits.MaxPdfBytes) throw Refuse(RenderLimitException.PdfBytes, bytes, Limits.MaxPdfBytes, page);
    }

    /// <summary>Whether the element is being drawn into its shadow's layer (a PDF decodes a picture drawn there).</summary>
    internal bool InShadowLayer { get; set; }

    /// <summary>What the canvas being drawn is, as <see cref="Exporter"/> sets it while it renders; otherwise a caller's own canvas.</summary>
    internal RenderTarget Target { get; set; }

    /// <summary>Records a warning once; the same code and message repeated (e.g. per page) is kept once.</summary>
    internal void Warn(string code, string message)
    {
        if (_seen.Add(code + "\n" + message)) _warnings.Add(new RenderWarning(code, message));
    }

    /// <summary>The full path of an image, or null with the reason <see cref="BaseDirectory"/>'s rules refuse it.</summary>
    internal string? ResolvePath(string source, out string refusal)
    {
        refusal = "";
        string full;
        try { full = Path.GetFullPath(Path.Combine(BaseDirectory ?? Environment.CurrentDirectory, source)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            refusal = "it is not a valid path";
            return null;
        }
        if (BaseDirectory is not null)
        {
            // Inside the folder, textually, after normalisation. Device spellings (\\?\, \??\, GLOBALROOT) are not
            // rewritten by GetFullPath, so they never look inside a folder and are refused here too.
            var root = Path.EndsInDirectorySeparator(BaseDirectory) ? BaseDirectory : BaseDirectory + Path.DirectorySeparatorChar;
            if (full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return full;
            refusal = "it is outside the document's folder";
            return null;
        }
        if (IsNetworkOrDevicePath(source) || IsNetworkOrDevicePath(full))
        {
            refusal = "it is a network or device path";
            return null;
        }
        return full;
    }

    // UNC (\\server\share, //server/share) and every device-namespace form (\\?\..., \\.\..., \??\...), some of which
    // reach the network (\\?\UNC, \??\UNC, GLOBALROOT\Device\Mup). A local drive path never starts like this.
    private static bool IsNetworkOrDevicePath(string path)
    {
        var p = path.Replace('/', '\\');
        return p.StartsWith(@"\\", StringComparison.Ordinal) || p.StartsWith(@"\??\", StringComparison.Ordinal);
    }
}
