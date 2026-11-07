using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    internal sealed class TeeStream : Stream
    {
        private readonly Stream _inner;
        private readonly ITransportObserver _observer;

        public TeeStream(Stream inner, ITransportObserver observer)
        {
            _inner = inner;
            _observer = observer;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                await _observer.OnBodyChunkAsync(buffer.Slice(0, read).ToArray(), cancellationToken).ConfigureAwait(false);
            }
            return read;
        }
    }
}
