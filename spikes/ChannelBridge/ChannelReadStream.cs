using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

public sealed class ChannelReadStream : Stream
{
    private readonly ChannelReader<byte[]> _reader;
    private ReadOnlyMemory<byte> _current;

    public ChannelReadStream(ChannelReader<byte[]> reader) => _reader = reader;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_current.IsEmpty)
        {
            if (!await _reader.WaitToReadAsync(ct).ConfigureAwait(false)) return 0;
            if (!_reader.TryRead(out var chunk)) return 0;
            _current = chunk;
        }
        var n = Math.Min(buffer.Length, _current.Length);
        _current.Slice(0, n).CopyTo(buffer);
        _current = _current.Slice(n);
        return n;
    }

    protected override void Dispose(bool disposing) { base.Dispose(disposing); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
