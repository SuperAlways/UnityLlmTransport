using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    /// <summary>
    /// 队列→Stream 的同步读视图：同步 Read 阻塞等待队列数据。
    /// 只许后台线程使用（Mono 的 GZipStream 内部只走同步 Read）；调用方在主线程上同步读会卡死，勿用。
    /// </summary>
    public sealed class SyncQueueReadStream : Stream
    {
        private readonly ChunkQueue _queue;
        private ReadOnlyMemory<byte> _current;
        private bool _disposed;

        public SyncQueueReadStream(ChunkQueue queue)
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
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SyncQueueReadStream));
            }
            ValidateArguments(buffer, offset, count);
            if (count == 0)
            {
                return 0;
            }

            while (true)
            {
                if (!_current.IsEmpty)
                {
                    return CopyCurrent(buffer, offset, count);
                }

                bool available = _queue.WaitToReadAsync(CancellationToken.None).GetAwaiter().GetResult();
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

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("SyncQueueReadStream supports sync reads only (background threads).");
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

        private static void ValidateArguments(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }
            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
            if (count < 0 || offset + count > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
        }

        private int CopyCurrent(byte[] buffer, int offset, int count)
        {
            int toCopy = Math.Min(count, _current.Length);
            _current.Slice(0, toCopy).CopyTo(buffer.AsMemory(offset, toCopy));
            _current = _current.Slice(toCopy);
            return toCopy;
        }
    }
}
