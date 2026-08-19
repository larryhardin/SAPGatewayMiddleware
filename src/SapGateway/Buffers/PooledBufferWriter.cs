using System.Buffers;

namespace SapGateway.Buffers;

/// <summary>
/// An <see cref="IBufferWriter{T}"/> backed by <see cref="ArrayPool{T}.Shared"/>.
/// The payload it holds exists exactly once, in a rented array that is returned
/// to the pool on <see cref="Dispose"/> — no LOH churn, no duplicate copies.
/// </summary>
public sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer;
    private int _index;

    public PooledBufferWriter(int initialCapacity = 4096)
        => _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);

    public int WrittenCount => _index;
    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _index);
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _index);

    public void Clear() => _index = 0;

    public void Advance(int count)
    {
        if (count < 0 || _index + count > _buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(count));
        _index += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_index);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_index);
    }

    private void EnsureCapacity(int sizeHint)
    {
        if (sizeHint < 1) sizeHint = 1;
        if (_buffer.Length - _index >= sizeHint) return;

        int newSize = Math.Max(_buffer.Length * 2, _index + sizeHint);
        byte[] next = ArrayPool<byte>.Shared.Rent(newSize);
        _buffer.AsSpan(0, _index).CopyTo(next);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = next;
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = Array.Empty<byte>();
        _index = 0;
    }
}
