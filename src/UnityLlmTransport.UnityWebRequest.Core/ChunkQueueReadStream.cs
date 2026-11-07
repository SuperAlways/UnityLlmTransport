using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    /// <summary>队列→Stream；仅支持异步读（同步读在主线程会卡帧/死锁，明确禁止）。</summary>
    public sealed class ChunkQueueReadStream : Stream
    {
        private readonly ChunkQueue _queue;
        private ReadOnlyMemory<byte> _current;
        private bool _disposed;

        public ChunkQueueReadStream(ChunkQueue queue)
        {
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        }

        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException("ChunkQueueReadStream supports async reads only.");
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ChunkQueueReadStream));
            }
            if (buffer.Length == 0)
            {
                return 0;
            }

            while (true)
            {
                if (!_current.IsEmpty)
                {
                    int toCopy = Math.Min(buffer.Length, _current.Length);
                    _current.Slice(0, toCopy).CopyTo(buffer);
                    _current = _current.Slice(toCopy);
                    return toCopy;
                }

                bool available = await _queue.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
                if (!available)
                {
                    var error = _queue.CompletionError;
                    if (error != null)
                    {
                        throw error;
                    }
                    return 0;
                }

                if (!_queue.TryRead(out _current))
                {
                    // 不伪造 EOF：回到 WaitToReadAsync 重新等（单消费者下正常不会走到）。
                }
            }
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _disposed = true;
                _current = default;
            }
            base.Dispose(disposing);
        }
    }
}
