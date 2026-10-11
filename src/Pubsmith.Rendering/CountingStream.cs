namespace Pubsmith.Rendering;

/// <summary>
/// What <see cref="Exporter.ToPdf"/> writes, counted on its way to the caller's stream. Its <see cref="Position"/> is its
/// own count, so the caller's stream need not seek. It does not contain the caller's stream's faults (WI-007).
/// </summary>
internal sealed class CountingStream(Stream inner) : Stream
{
    /// <summary>Bytes written so far.</summary>
    public long Count { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => Count;
    public override long Position { get => Count; set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        Count += buffer.Length;
    }

    public override void WriteByte(byte value)
    {
        inner.WriteByte(value);
        Count++;
    }

    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
