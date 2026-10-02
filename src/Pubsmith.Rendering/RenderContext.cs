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

    /// <summary>Limits other than the defaults, for tests.</summary>
    internal RenderContext(string? baseDirectory, long maxPicturePixels, long cacheBudget, FontResolver? fonts = null) : this(baseDirectory, fonts)
        => Pictures = new PictureCache(maxPicturePixels, cacheBudget);

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
