using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class TimeoutTransportTests
    {
        private static TransportRequest Request()
        {
            return new TransportRequest("GET", "http://localhost/timeout", new TransportHeaders(), ReadOnlyMemory<byte>.Empty);
        }

        private static ReadOnlyMemory<byte> Bytes(string text)
        {
            return new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(text));
        }

        [Fact]
        public async Task First_Byte_Timeout_Throws_And_Aborts()
        {
            var inner = new MockTransport(_ => new MockResponse
            {
                Chunks = new[] { Bytes("late") },
                ChunkDelay = TimeSpan.FromMilliseconds(500)
            });
            var transport = new TimeoutTransport(inner);
            var options = new TransportOptions { FirstByteTimeout = TimeSpan.FromMilliseconds(60) };

            var session = await transport.OpenAsync(Request(), options, CancellationToken.None);
            var buffer = new byte[16];
            var error = await Assert.ThrowsAsync<TransportException>(
                () => session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None).AsTask());

            Assert.Equal(TransportErrorKind.FirstByteTimeout, error.Kind);
        }

        [Fact]
        public async Task Idle_Timeout_Throws_After_First_Byte()
        {
            var inner = new MockTransport(_ => new MockResponse
            {
                Chunks = new[] { Bytes("a"), Bytes("b") },
                ChunkDelay = TimeSpan.FromMilliseconds(400)
            });
            var transport = new TimeoutTransport(inner);
            var options = new TransportOptions { IdleTimeout = TimeSpan.FromMilliseconds(60) };

            var session = await transport.OpenAsync(Request(), options, CancellationToken.None);
            var buffer = new byte[16];
            int first = await session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None);
            Assert.Equal(1, first);

            var error = await Assert.ThrowsAsync<TransportException>(
                () => session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None).AsTask());

            Assert.Equal(TransportErrorKind.IdleTimeout, error.Kind);
        }

        [Fact]
        public async Task MaxResponseBytes_Overflow_Throws()
        {
            var inner = new MockTransport(_ => new MockResponse
            {
                Chunks = new[] { Bytes("0123456789"), Bytes("0123456789") }
            });
            var transport = new TimeoutTransport(inner);
            var options = new TransportOptions { MaxResponseBytes = 15 };

            var session = await transport.OpenAsync(Request(), options, CancellationToken.None);
            var buffer = new byte[64];
            int first = await session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None);
            Assert.Equal(10, first);

            var error = await Assert.ThrowsAsync<TransportException>(
                () => session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None).AsTask());

            Assert.Equal(TransportErrorKind.Overflow, error.Kind);
        }

        [Fact]
        public async Task Passes_Through_Status_And_Headers()
        {
            var headers = new TransportHeaders { ["content-type"] = "text/event-stream" };
            var inner = new MockTransport(_ => new MockResponse { StatusCode = 201, Headers = headers });
            var transport = new TimeoutTransport(inner);

            var session = await transport.OpenAsync(Request(), new TransportOptions(), CancellationToken.None);

            Assert.Equal(201, session.StatusCode);
            Assert.Equal("text/event-stream", session.Headers["Content-Type"]);
        }

        [Fact]
        public async Task Timeout_Aborts_Inner_Session()
        {
            var inner = new RecordingTransport(new HangingStream());
            var transport = new TimeoutTransport(inner);
            var options = new TransportOptions { FirstByteTimeout = TimeSpan.FromMilliseconds(50) };

            var session = await transport.OpenAsync(Request(), options, CancellationToken.None);
            var buffer = new byte[16];
            await Assert.ThrowsAsync<TransportException>(
                () => session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None).AsTask());

            Assert.True(inner.Aborted);
        }

        private sealed class RecordingTransport : IStreamingHttpTransport
        {
            private readonly Stream _body;

            public RecordingTransport(Stream body)
            {
                _body = body;
            }

            public bool Aborted { get; private set; }
            public TransportCapabilities Capabilities => TransportCapabilities.StreamingBody;

            public Task<ITransportSession> OpenAsync(
                TransportRequest request,
                TransportOptions options,
                CancellationToken cancellationToken)
            {
                ITransportSession session = new RecordingSession(this, _body);
                return Task.FromResult(session);
            }

            private sealed class RecordingSession : ITransportSession
            {
                private readonly RecordingTransport _owner;

                public RecordingSession(RecordingTransport owner, Stream body)
                {
                    _owner = owner;
                    Body = body;
                }

                public int StatusCode => 200;
                public TransportHeaders Headers { get; } = new TransportHeaders();
                public Stream Body { get; }

                public void Abort()
                {
                    _owner.Aborted = true;
                }

                public ValueTask DisposeAsync()
                {
                    Abort();
                    return default;
                }
            }
        }

        private sealed class HangingStream : Stream
        {
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
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                return 0;
            }
        }
    }
}
