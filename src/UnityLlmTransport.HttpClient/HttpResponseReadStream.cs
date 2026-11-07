using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    internal sealed class HttpResponseReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly HttpResponseMessage _response;
        private TransportException? _terminalError;
        private bool _disposed;

        public HttpResponseReadStream(Stream inner, HttpResponseMessage response)
        {
            _inner = inner;
            _response = response;
        }

        public void Abort(TransportErrorKind kind)
        {
            var error = new TransportException(kind, "Response aborted (" + kind + ").");
            if (Interlocked.CompareExchange(ref _terminalError, error, null) == null)
            {
                try
                {
                    _response.Dispose();
                }
                catch (Exception)
                {
                }
            }
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
            ThrowIfTerminal();
            try
            {
                return _inner.Read(buffer, offset, count);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ThrowIfTerminal();
                throw new TransportException(
                    TransportErrorKind.Protocol,
                    "Response stream failed.",
                    ex,
                    null,
                    null,
                    null,
                    false);
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ThrowIfTerminal();
            try
            {
                return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ThrowIfTerminal();
                throw new TransportException(
                    TransportErrorKind.Protocol,
                    "Response stream failed.",
                    ex,
                    null,
                    null,
                    null,
                    false);
            }
        }

        private void ThrowIfTerminal()
        {
            var error = Volatile.Read(ref _terminalError);
            if (error != null)
            {
                throw error;
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
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
