using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class RetryTransportTests
    {
        private static TransportRequest Request()
        {
            return new TransportRequest("GET", "http://localhost/retry", new TransportHeaders(), ReadOnlyMemory<byte>.Empty);
        }

        [Fact]
        public async Task Retries_On_Retryable_Open_Error()
        {
            int calls = 0;
            var transport = new RetryTransport(
                new MockTransport(_ =>
                {
                    calls++;
                    return calls == 1
                        ? new MockResponse { Error = new TransportException(TransportErrorKind.Connect, "down", isRetryable: true) }
                        : new MockResponse();
                }),
                new RetryOptions { MaxRetries = 2 },
                new ZeroDelayPolicy());

            var session = await transport.OpenAsync(Request(), new TransportOptions(), CancellationToken.None);

            Assert.Equal(2, calls);
            Assert.Equal(200, session.StatusCode);
        }

        [Fact]
        public async Task Does_Not_Retry_After_Session_Opened()
        {
            var inner = new FailingBodyTransport();
            var transport = new RetryTransport(inner, new RetryOptions { MaxRetries = 3 }, new ZeroDelayPolicy());

            var session = await transport.OpenAsync(Request(), new TransportOptions(), CancellationToken.None);
            var buffer = new byte[16];
            await Assert.ThrowsAsync<IOException>(
                () => session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None).AsTask());

            Assert.Equal(1, inner.OpenCalls);
        }

        [Fact]
        public async Task Stops_At_MaxRetries()
        {
            int calls = 0;
            var transport = new RetryTransport(
                new MockTransport(_ =>
                {
                    calls++;
                    return new MockResponse { StatusCode = 503 };
                }),
                new RetryOptions { MaxRetries = 1 },
                new ZeroDelayPolicy());

            await Assert.ThrowsAsync<TransportException>(
                () => transport.OpenAsync(Request(), new TransportOptions(), CancellationToken.None));

            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task Refresh_401_Once_Then_Retry()
        {
            int calls = 0;
            var refresh = new CountingRefreshHandler(returnValue: true);
            var transport = new RetryTransport(
                new MockTransport(_ =>
                {
                    calls++;
                    return calls == 1 ? new MockResponse { StatusCode = 401 } : new MockResponse();
                }),
                new RetryOptions { MaxRetries = 2 },
                new ZeroDelayPolicy(),
                refresh);

            var session = await transport.OpenAsync(Request(), new TransportOptions(), CancellationToken.None);

            Assert.Equal(2, calls);
            Assert.Equal(1, refresh.Calls);
            Assert.Equal(200, session.StatusCode);
        }

        [Fact]
        public async Task Refresh_401_Only_Once()
        {
            int calls = 0;
            var refresh = new CountingRefreshHandler(returnValue: true);
            var transport = new RetryTransport(
                new MockTransport(_ =>
                {
                    calls++;
                    return new MockResponse { StatusCode = 401 };
                }),
                new RetryOptions { MaxRetries = 3 },
                new ZeroDelayPolicy(),
                refresh);

            await Assert.ThrowsAsync<TransportException>(
                () => transport.OpenAsync(Request(), new TransportOptions(), CancellationToken.None));

            Assert.Equal(2, calls);
            Assert.Equal(1, refresh.Calls);
        }

        [Fact]
        public async Task Refresh_False_Surfaces_401()
        {
            var refresh = new CountingRefreshHandler(returnValue: false);
            var transport = new RetryTransport(
                new MockTransport(_ => new MockResponse { StatusCode = 401 }),
                new RetryOptions { MaxRetries = 2 },
                new ZeroDelayPolicy(),
                refresh);

            await Assert.ThrowsAsync<TransportException>(
                () => transport.OpenAsync(Request(), new TransportOptions(), CancellationToken.None));

            Assert.Equal(1, refresh.Calls);
        }

        [Fact]
        public async Task Retry_After_Above_Cap_Does_Not_Retry()
        {
            int calls = 0;
            var headers = new TransportHeaders { ["Retry-After"] = "5" };
            var transport = new RetryTransport(
                new MockTransport(_ =>
                {
                    calls++;
                    return new MockResponse { StatusCode = 503, Headers = headers };
                }),
                new RetryOptions { MaxRetries = 2, MaxRetryDelay = TimeSpan.FromSeconds(1) });

            await Assert.ThrowsAsync<TransportException>(
                () => transport.OpenAsync(Request(), new TransportOptions(), CancellationToken.None));

            Assert.Equal(1, calls);
        }

        [Fact]
        public async Task X_Should_Retry_False_Wins()
        {
            int calls = 0;
            var headers = new TransportHeaders { ["x-should-retry"] = "false" };
            var transport = new RetryTransport(
                new MockTransport(_ =>
                {
                    calls++;
                    return new MockResponse { StatusCode = 503, Headers = headers };
                }),
                new RetryOptions { MaxRetries = 2 },
                new ZeroDelayPolicy());

            await Assert.ThrowsAsync<TransportException>(
                () => transport.OpenAsync(Request(), new TransportOptions(), CancellationToken.None));

            Assert.Equal(1, calls);
        }

        [Fact]
        public async Task Respects_Pre_Cancelled_Token()
        {
            var inner = new MockTransport(_ => new MockResponse());
            var transport = new RetryTransport(inner, new RetryOptions(), new ZeroDelayPolicy());
            var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => transport.OpenAsync(Request(), new TransportOptions(), cts.Token));

            Assert.Empty(inner.Requests);
        }

        private sealed class ZeroDelayPolicy : IRetryPolicy
        {
            public bool ShouldRetry(TransportException error, int attempt)
            {
                if (!error.IsRetryable)
                {
                    return false;
                }
                if (error.Headers != null && error.Headers.TryGetValue("x-should-retry", out var value))
                {
                    return value == null || !value.Equals("false", StringComparison.OrdinalIgnoreCase);
                }
                return true;
            }

            public TimeSpan GetDelay(TransportException error, int attempt)
            {
                return TimeSpan.Zero;
            }
        }

        private sealed class CountingRefreshHandler : IAuthRefreshHandler
        {
            private readonly bool _returnValue;

            public CountingRefreshHandler(bool returnValue)
            {
                _returnValue = returnValue;
            }

            public int Calls { get; private set; }

            public Task<bool> RefreshAsync(CancellationToken ct)
            {
                Calls++;
                return Task.FromResult(_returnValue);
            }
        }

        private sealed class FailingBodyTransport : IStreamingHttpTransport
        {
            public int OpenCalls { get; private set; }
            public TransportCapabilities Capabilities => TransportCapabilities.StreamingBody;

            public Task<ITransportSession> OpenAsync(
                TransportRequest request,
                TransportOptions options,
                CancellationToken cancellationToken)
            {
                OpenCalls++;
                ITransportSession session = new FailingSession();
                return Task.FromResult(session);
            }

            private sealed class FailingSession : ITransportSession
            {
                public int StatusCode => 200;
                public TransportHeaders Headers { get; } = new TransportHeaders();
                public Stream Body { get; } = new ThrowingStream();
                public void Abort() { }
                public ValueTask DisposeAsync() => default;
            }
        }

        private sealed class ThrowingStream : Stream
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

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                throw new IOException("stream failed before any byte");
            }
        }
    }
}
