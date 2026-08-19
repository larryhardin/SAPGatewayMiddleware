namespace SapGateway.Streams;

/// <summary>
/// A read-only <see cref="Stream"/> decorator that scans every byte as it flows
/// through, in place, with zero copies. The consumer (<see cref="System.Xml.XmlReader"/>)
/// pulls from this stream, so inspection happens in the same single pass as
/// XML validation — the payload is never buffered for a second look.
/// </summary>
public sealed class InspectingStream : Stream
{
    private readonly Stream _inner;
    private readonly XmlInspectionLog _log;
    private long _readOffset;

    public InspectingStream(Stream inner, XmlInspectionLog log)
    {
        _inner = inner;
        _log = log;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            _log.Scan(buffer.Span[..read], _readOffset);
            _readOffset += read;
        }
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

    public override int Read(Span<byte> buffer)
    {
        int read = _inner.Read(buffer);
        if (read > 0)
        {
            _log.Scan(buffer[..read], _readOffset);
            _readOffset += read;
        }
        return read;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _readOffset;
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
