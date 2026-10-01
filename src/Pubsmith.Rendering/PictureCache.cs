using SkiaSharp;

namespace Pubsmith.Rendering;

/// <summary>What opening a picture found.</summary>
internal enum PictureState { Missing, Unreadable, TooLarge, Ok }

/// <summary>
/// The pictures one <see cref="RenderContext"/> has opened, by resolved full path. Each file is read at its first lookup,
/// and what was found (the picture, or that it is missing, undecodable or too large) is kept for every later lookup, so
/// later changes to the file are not seen by this context; only a file that could not be read (an I/O error, such as
/// another program's lock) is read again at its next lookup. Its header is read before anything is decoded: a picture declaring more than
/// <see cref="MaxPixels"/> is never decoded or embedded. An accepted picture keeps its encoded image (drawn into PDFs, so each is embedded at most once per
/// distinct crop and a JPEG passes through) and, for bitmaps, a decoded copy made on first use; decoded copies are kept within
/// <see cref="Budget"/> pixels, least recently used dropped before the next decode, and a picture larger than the
/// budget is drawn from its encoded image each time, as before. Not thread-safe (one context, one thread).
/// </summary>
internal sealed class PictureCache(long maxPixels, long budget) : IDisposable
{
    internal sealed class Entry
    {
        public required PictureState State { get; init; }
        /// <summary>Declared pixels (64-bit: a declared size can overflow an int).</summary>
        public long Pixels { get; init; }
        public SKImage? Encoded { get; init; }
        public SKImage? Decoded { get; set; }
        public bool DecodeFailed { get; set; }
        /// <summary>The file could not be read (an I/O error): not remembered, so the next lookup tries again.</summary>
        public bool ReadFailed { get; init; }
        public LinkedListNode<Entry>? Node { get; set; }
    }

    // Paths compare as RenderContext.ResolvePath compares them: ignoring case on Windows only.
    private readonly Dictionary<string, Entry> _entries = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly LinkedList<Entry> _decoded = new();   // least recently used first

    public long MaxPixels => maxPixels;
    public long Budget => budget;
    /// <summary>Lookups that went to the file: one per distinct path, plus one per later lookup of a file that could not be read.</summary>
    public int Reads { get; private set; }
    /// <summary>Decoded copies made.</summary>
    public int Decodes { get; private set; }
    public long DecodedPixels { get; private set; }
    public long PeakDecodedPixels { get; private set; }
    public bool IsDisposed { get; private set; }

    public Entry Get(string fullPath)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_entries.TryGetValue(fullPath, out var entry)) return entry;
        Reads++;
        entry = Open(fullPath);
        if (!entry.ReadFailed) _entries[fullPath] = entry;
        return entry;
    }

    private Entry Open(string path)
    {
        if (!File.Exists(path)) return new Entry { State = PictureState.Missing };
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new Entry { State = PictureState.Unreadable, ReadFailed = true }; }
        using var data = SKData.CreateCopy(bytes);
        using (var codec = SKCodec.Create(data))
        {
            if (codec is null) return new Entry { State = PictureState.Unreadable };
            var declared = (long)codec.Info.Width * codec.Info.Height;
            if (declared > maxPixels) return new Entry { State = PictureState.TooLarge, Pixels = declared };
        }
        var image = SKImage.FromEncodedData(data);
        return image is null
            ? new Entry { State = PictureState.Unreadable }
            : new Entry { State = PictureState.Ok, Encoded = image, Pixels = (long)image.Width * image.Height };
    }

    /// <summary>
    /// The picture decoded for drawing into a bitmap, or null when it is larger than the budget, its pixels cannot be
    /// read back (remembered), or there is no memory for the copy now (not remembered: tried again at the next use):
    /// the caller then draws the encoded image, as before the cache. <c>ReadPixels</c> applies the EXIF orientation,
    /// as drawing does.
    /// </summary>
    public SKImage? Decoded(Entry entry)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (entry.Decoded is not null)
        {
            _decoded.Remove(entry.Node!);
            _decoded.AddLast(entry.Node!);
            return entry.Decoded;
        }
        if (entry.Encoded is null || entry.DecodeFailed || entry.Pixels > budget) return null;
        while (DecodedPixels + entry.Pixels > budget && _decoded.First is { } oldest) Drop(oldest.Value);   // room first
        var info = new SKImageInfo(entry.Encoded.Width, entry.Encoded.Height, SKImageInfo.PlatformColorType, SKAlphaType.Premul);
        using (var bitmap = new SKBitmap())
        {
            if (!bitmap.TryAllocPixels(info)) return null;   // no memory for the copy now
            if (!entry.Encoded.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0))
            {
                entry.DecodeFailed = true;
                return null;
            }
            bitmap.SetImmutable();
            entry.Decoded = SKImage.FromBitmap(bitmap);
        }
        Decodes++;
        DecodedPixels += entry.Pixels;
        PeakDecodedPixels = Math.Max(PeakDecodedPixels, DecodedPixels);
        entry.Node = _decoded.AddLast(entry);
        return entry.Decoded;
    }

    private void Drop(Entry entry)
    {
        _decoded.Remove(entry.Node!);
        entry.Node = null;
        entry.Decoded!.Dispose();
        entry.Decoded = null;
        DecodedPixels -= entry.Pixels;
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        foreach (var entry in _entries.Values)
        {
            entry.Decoded?.Dispose();
            entry.Encoded?.Dispose();
        }
        _entries.Clear();
        _decoded.Clear();
        DecodedPixels = 0;
    }
}
