namespace PoPreyClip.Audio;

/// <summary>
/// Thread-safe byte ring buffer between a capture thread (writer) and the pacer (reader).
/// When full, the oldest audio is dropped so latency stays bounded.
/// </summary>
public sealed class PcmFifo(int capacityBytes)
{
    private readonly byte[] _buffer = new byte[capacityBytes];
    private readonly object _gate = new();
    private int _start;
    private int _count;

    public int Capacity => _buffer.Length;

    public int Available
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (data.Length >= _buffer.Length)
            {
                data[^_buffer.Length..].CopyTo(_buffer);
                _start = 0;
                _count = _buffer.Length;
                return;
            }

            var overflow = _count + data.Length - _buffer.Length;
            if (overflow > 0)
            {
                DropLocked(overflow);
            }

            var end = (_start + _count) % _buffer.Length;
            var first = Math.Min(data.Length, _buffer.Length - end);
            data[..first].CopyTo(_buffer.AsSpan(end));
            data[first..].CopyTo(_buffer);
            _count += data.Length;
        }
    }

    public void WriteSilence(int byteCount)
    {
        Span<byte> zeros = stackalloc byte[4096];
        zeros.Clear();
        while (byteCount > 0)
        {
            var chunk = Math.Min(byteCount, zeros.Length);
            Write(zeros[..chunk]);
            byteCount -= chunk;
        }
    }

    /// <summary>Reads up to destination.Length bytes and returns how many were read.</summary>
    public int Read(Span<byte> destination)
    {
        lock (_gate)
        {
            var toRead = Math.Min(destination.Length, _count);
            var first = Math.Min(toRead, _buffer.Length - _start);
            _buffer.AsSpan(_start, first).CopyTo(destination);
            _buffer.AsSpan(0, toRead - first).CopyTo(destination[first..]);
            _start = (_start + toRead) % _buffer.Length;
            _count -= toRead;
            return toRead;
        }
    }

    public void Drop(int byteCount)
    {
        lock (_gate)
        {
            DropLocked(Math.Min(byteCount, _count));
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _start = 0;
            _count = 0;
        }
    }

    private void DropLocked(int byteCount)
    {
        _start = (_start + byteCount) % _buffer.Length;
        _count -= byteCount;
    }
}
