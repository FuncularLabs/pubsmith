using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pubsmith.Core;

/// <summary>Thrown when a document cannot be read: missing file, bad JSON, unknown schema, or invalid values.</summary>
public sealed class DocumentFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Thrown when a document's JSON would be larger than the limit it is saved with; nothing is saved.</summary>
public sealed class DocumentTooLargeException(long limit) : IOException($"The document would be more than {limit} bytes of JSON, so it was not saved.")
{
    public long Limit { get; } = limit;
}

/// <summary>Reads and writes the Pubsmith JSON document format.</summary>
public static class DocumentJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        RespectNullableAnnotations = true,
        // A value the model's constructors require (a run's style, a page's size...) must be present: a missing
        // one is an invalid document, not a null or zero for the renderer to trip over.
        RespectRequiredConstructorParameters = true,
    };

    public static string Serialize(PubsmithDocument document) => JsonSerializer.Serialize(document, Options);

    public static PubsmithDocument Deserialize(string json)
    {
        // Check the schema version before binding, so a future format fails with a clear message
        // instead of a confusing property error.
        int version;
        try
        {
            using var probe = JsonDocument.Parse(json);
            if (probe.RootElement.ValueKind != JsonValueKind.Object)
                throw new DocumentFormatException("A document must be a JSON object.");
            if (!probe.RootElement.TryGetProperty("schemaVersion", out var v) || !v.TryGetInt32(out version))
                throw new DocumentFormatException("The document has no integer schemaVersion.");
        }
        catch (JsonException ex) { throw new DocumentFormatException($"The document is not valid JSON: {ex.Message}", ex); }

        if (version != PubsmithDocument.CurrentSchemaVersion)
            throw new DocumentFormatException($"Unsupported schemaVersion {version}; this build reads version {PubsmithDocument.CurrentSchemaVersion}.");

        try
        {
            return JsonSerializer.Deserialize<PubsmithDocument>(json, Options)
                   ?? throw new DocumentFormatException("The document is empty.");
        }
        catch (JsonException ex) { throw new DocumentFormatException($"The document is invalid: {ex.Message}", ex); }
        catch (ArgumentException ex) { throw new DocumentFormatException($"The document has an invalid value: {ex.Message}", ex); }
        catch (NotSupportedException ex) { throw new DocumentFormatException($"The document is invalid: {ex.Message}", ex); }
    }

    public static PubsmithDocument Load(string path)
    {
        string json;
        try { json = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DocumentFormatException($"Cannot read document '{path}': {ex.Message}", ex);
        }
        return Deserialize(json);
    }

    public static void Save(PubsmithDocument document, string path) => Save(document, path, long.MaxValue);

    /// <summary>
    /// Saves the document, or throws <see cref="DocumentTooLargeException"/> once its JSON passes
    /// <paramref name="maxBytes"/>. Either way, a file already at <paramref name="path"/> is only ever replaced by a
    /// complete document.
    /// </summary>
    public static void Save(PubsmithDocument document, string path, long maxBytes)
    {
        using var staged = Stage(document, path, maxBytes);
        staged.Commit();
    }

    /// <summary>
    /// Writes the document beside <paramref name="path"/>, within <paramref name="maxBytes"/>, without touching
    /// <paramref name="path"/> yet: <see cref="StagedDocument.Commit"/> moves it into place, and disposing it
    /// uncommitted removes it. A document that is refused or cannot be written leaves nothing behind.
    /// </summary>
    public static StagedDocument Stage(PubsmithDocument document, string path, long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        // A uniquely named temporary sibling, renamed into place on commit, so a failure part-way never leaves a
        // truncated document. The JSON is streamed to it (the same bytes as Serialize, in UTF-8), so a large
        // document is never one string in memory.
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new LimitedStream(File.Create(temp), maxBytes))
                JsonSerializer.Serialize(stream, document, Options);
            return new StagedDocument(temp, path);
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }
    }
}

/// <summary>A document written beside its path (<see cref="DocumentJson.Stage"/>) and not yet moved into place.</summary>
public sealed class StagedDocument : IDisposable
{
    private readonly string _temp, _path;
    private bool _done;

    internal StagedDocument(string temp, string path) => (_temp, _path) = (temp, path);

    /// <summary>Moves the document into place, replacing any file there. Once only.</summary>
    public void Commit()
    {
        ObjectDisposedException.ThrowIf(_done, this);
        File.Move(_temp, _path, overwrite: true);
        _done = true;
    }

    /// <summary>Removes the document if it was not committed; the path stays as it was.</summary>
    public void Dispose()
    {
        if (!_done && File.Exists(_temp)) File.Delete(_temp);
        _done = true;
    }
}

/// <summary>A write-only stream that refuses any write that would take it past <paramref name="limit"/> bytes.</summary>
internal sealed class LimitedStream(Stream inner, long limit) : Stream
{
    private long written;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (written + buffer.Length > limit) throw new DocumentTooLargeException(limit);
        written += buffer.Length;
        inner.Write(buffer);
    }

    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
