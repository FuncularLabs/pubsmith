namespace Pubsmith.Rendering;

/// <summary>Something the renderer had to work around. Rendering continues; the caller decides what to do.</summary>
public sealed record RenderWarning(string Code, string Message)
{
    public const string FontSubstituted = "font-substituted";
    public const string ImageMissing = "image-missing";
    public const string ImageUnreadable = "image-unreadable";
    /// <summary>The image path is outside the document's folder, a network or device path, or not a valid path; it was not opened.</summary>
    public const string ImageRefused = "image-refused";
}

/// <summary>Per-render settings and the warnings collected while rendering.</summary>
public sealed class RenderContext(string? baseDirectory = null, FontResolver? fonts = null)
{
    private readonly List<RenderWarning> _warnings = [];
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
