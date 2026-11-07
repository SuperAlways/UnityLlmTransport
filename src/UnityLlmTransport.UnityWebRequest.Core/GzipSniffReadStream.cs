using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    /// <summary>
    /// 按内容嗅探 gzip 的同步读流：首读先取 2 字节，命中 gzip magic(1f 8b) 才包 GZipStream，否则原样透传。
    /// 动机：UnityWebRequest(2021.3) 会自动解压 gzip 响应体，但响应头仍带 Content-Encoding: gzip——
    /// 只看头部会二次解压明文报 Corrupted data；嗅探对"已解压/未解压"两种后端行为都正确。
    /// 内层须为同步读流（如 SyncQueueReadStream），置于 AsyncOffloadStream 之下使用。
    /// </summary>
    public sealed class GzipSniffReadStream : Stream
    {
        private readonly Stream _inner;
        private byte[]? _pending;
        private int _pendingLength;
        private GZipStream? _gzip;
        private bool _sniffed;
        private bool _disposed;

        public GzipSniffReadStream(Stream inner)
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

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(GzipSniffReadStream));
            }
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            if (count == 0)
            {
                return 0;
            }

            if (!_sniffed)
            {
                Sniff();
            }

            if (_gzip != null)
            {
                return _gzip.Read(buffer, offset, count);
            }

            if (_pendingLength > 0 && _pending != null)
            {
                int copied = Math.Min(count, _pendingLength);
                Array.Copy(_pending, 0, buffer, offset, copied);
                Array.Copy(_pending, copied, _pending, 0, _pendingLength - copied);
                _pendingLength -= copied;
                return copied;
            }

            return _inner.Read(buffer, offset, count);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("GzipSniffReadStream supports sync reads only (background threads).");
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
                if (_gzip != null)
                {
                    _gzip.Dispose();
                    _gzip = null;
                }
                _inner.Dispose();
                _pending = null;
            }
            base.Dispose(disposing);
        }

        private void Sniff()
        {
            _sniffed = true;
            _pending = new byte[2];
            int sniffed = 0;
            while (sniffed < 2)
            {
                int read = _inner.Read(_pending, sniffed, 2 - sniffed);
                if (read == 0)
                {
                    break;
                }
                sniffed += read;
            }
            _pendingLength = sniffed;

            if (sniffed == 2 && _pending != null && _pending[0] == 0x1f && _pending[1] == 0x8b)
            {
                _gzip = new GZipStream(
                    new ReplayStream(_inner, _pending),
                    CompressionMode.Decompress,
                    leaveOpen: false);
                _pending = null;
                _pendingLength = 0;
            }
        }

        /// <summary>先把嗅探掉的首字节回放给 gzip 流，再透传内层。</summary>
        private sealed class ReplayStream : Stream
        {
            private readonly Stream _inner;
            private readonly byte[] _prefix;
            private int _prefixOffset;

            public ReplayStream(Stream inner, byte[] prefix)
            {
                _inner = inner;
                _prefix = prefix;
                _prefixOffset = 0;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (count == 0)
                {
                    return 0;
                }
                if (_prefixOffset < _prefix.Length)
                {
                    int copied = Math.Min(count, _prefix.Length - _prefixOffset);
                    Array.Copy(_prefix, _prefixOffset, buffer, offset, copied);
                    _prefixOffset += copied;
                    return copied;
                }
                return _inner.Read(buffer, offset, count);
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}
