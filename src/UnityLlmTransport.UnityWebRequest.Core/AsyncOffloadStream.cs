using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    /// <summary>
    /// 把"只能同步读"的内层流适配成"只能异步读"的外层流：ReadAsync 把同步读 offload 到线程池。
    /// 动机：Mono(2021.3) 的 GZipStream 内部只走同步 Read，而队列流只许异步读（主线程安全）。
    /// offload 后调用线程永不阻塞；同步 Read 仍被明确禁止（主线程卡帧/死锁防护不变）。
    /// </summary>
    public sealed class AsyncOffloadStream : Stream
    {
        private readonly Stream _inner;
        private bool _disposed;

        public AsyncOffloadStream(Stream inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public override bool CanRead => !_disposed && _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException("AsyncOffloadStream supports async reads only.");
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AsyncOffloadStream));
            }
            if (buffer.Length == 0)
            {
                return 0;
            }

            ReadOnlyMemory<byte> readOnly = buffer;
            if (!MemoryMarshal.TryGetArray(readOnly, out var segment))
            {
                var temp = new byte[buffer.Length];
                int copied = await Task.Run(
                    () => _inner.Read(temp, 0, temp.Length),
                    cancellationToken).ConfigureAwait(false);
                if (copied > 0)
                {
                    temp.AsMemory(0, copied).CopyTo(buffer);
                }
                return copied;
            }

            return await Task.Run(
                () => _inner.Read(segment.Array, segment.Offset, segment.Count),
                cancellationToken).ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _disposed = true;
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
