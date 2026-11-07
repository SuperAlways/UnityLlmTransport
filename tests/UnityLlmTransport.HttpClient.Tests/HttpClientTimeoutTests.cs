using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    [Collection("phase3-server")]
    public class HttpClientTimeoutTests
    {
        private readonly ServerFixture _fixture;

        public HttpClientTimeoutTests(ServerFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task ConnectTimeout_Is_Connect_Timeout()
        {
            var transport = new HttpClientTransport();
            var options = new TransportOptions
            {
                ConnectTimeout = TimeSpan.FromMilliseconds(100)
            };

            var error = await Assert.ThrowsAsync<TransportException>(() => transport.OpenAsync(
                HttpClientTestHelpers.Request(_fixture.Server.HttpBase + "/slow-headers"),
                options,
                CancellationToken.None));

            Assert.Equal(TransportErrorKind.Connect, error.Kind);
            Assert.Contains("timeout", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(error.IsRetryable);
        }

        [Fact]
        public async Task FirstByteTimeout_Is_Enforced_By_Core()
        {
            var transport = new TimeoutTransport(new HttpClientTransport());
            var options = new TransportOptions
            {
                FirstByteTimeout = TimeSpan.FromMilliseconds(100)
            };

            var session = await transport.OpenAsync(
                HttpClientTestHelpers.Request(_fixture.Server.HttpBase + "/slow-first"),
                options,
                CancellationToken.None);
            var buffer = new byte[64];
            try
            {
                var error = await Assert.ThrowsAsync<TransportException>(
                    () => session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None).AsTask());
                Assert.Equal(TransportErrorKind.FirstByteTimeout, error.Kind);
            }
            finally
            {
                await session.DisposeAsync();
            }
        }

        [Fact]
        public async Task IdleTimeout_Is_Enforced_By_Core()
        {
            var transport = new TimeoutTransport(new HttpClientTransport());
            var options = new TransportOptions
            {
                IdleTimeout = TimeSpan.FromMilliseconds(150)
            };

            var session = await transport.OpenAsync(
                HttpClientTestHelpers.Request(_fixture.Server.HttpBase + "/slow-idle"),
                options,
                CancellationToken.None);
            var buffer = new byte[64];
            try
            {
                await session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None);
                var error = await Assert.ThrowsAsync<TransportException>(
                    () => session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None).AsTask());
                Assert.Equal(TransportErrorKind.IdleTimeout, error.Kind);
            }
            finally
            {
                await session.DisposeAsync();
            }
        }

        [Fact]
        public async Task MaxResponseBytes_Overflow_Is_Enforced_By_Core()
        {
            var transport = new TimeoutTransport(new HttpClientTransport());
            var options = new TransportOptions
            {
                MaxResponseBytes = 64 * 1024
            };

            var session = await transport.OpenAsync(
                HttpClientTestHelpers.Request(_fixture.Server.HttpBase + "/big"),
                options,
                CancellationToken.None);
            var buffer = new byte[4096];
            try
            {
                var error = await Assert.ThrowsAsync<TransportException>(async () =>
                {
                    while (await session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None) > 0)
                    {
                    }
                });
                Assert.Equal(TransportErrorKind.Overflow, error.Kind);
            }
            finally
            {
                await session.DisposeAsync();
            }
        }

        [Fact]
        public async Task Cancellation_Mid_Stream_Is_Cancelled()
        {
            var transport = new HttpClientTransport();
            using var cts = new CancellationTokenSource();
            var session = await transport.OpenAsync(
                HttpClientTestHelpers.Request(_fixture.Server.HttpBase + "/sse"),
                new TransportOptions(),
                cts.Token);

            var parseTask = Task.Run(() => HttpClientTestHelpers.ParseAsync(session.Body));
            await Task.Delay(150);
            cts.Cancel();

            Exception? error = null;
            try
            {
                await parseTask;
            }
            catch (Exception ex)
            {
                error = ex;
            }
            await session.DisposeAsync();

            var transportError = Assert.IsType<TransportException>(error);
            Assert.True(
                transportError.Kind == TransportErrorKind.Cancelled ||
                transportError.Kind == TransportErrorKind.Aborted);
        }

        [Fact]
        public async Task Stream_Failure_Is_Protocol()
        {
            var transport = new HttpClientTransport();
            var session = await transport.OpenAsync(
                HttpClientTestHelpers.Request(_fixture.Server.HttpBase + "/drop"),
                new TransportOptions(),
                CancellationToken.None);

            var buffer = new byte[256];
            var error = await Assert.ThrowsAsync<TransportException>(async () =>
            {
                while (await session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None) > 0)
                {
                }
            });
            await session.DisposeAsync();

            Assert.Equal(TransportErrorKind.Protocol, error.Kind);
        }

        [Fact]
        public async Task Sync_Read_Is_Supported()
        {
            var transport = new HttpClientTransport();
            var session = await transport.OpenAsync(
                HttpClientTestHelpers.Request(_fixture.Server.HttpBase + "/echo", method: "GET", body: string.Empty),
                new TransportOptions(),
                CancellationToken.None);

            var buffer = new byte[16];
            var read = session.Body.Read(buffer, 0, buffer.Length);
            await session.DisposeAsync();

            Assert.True(read > 0);
            Assert.Equal("ok", System.Text.Encoding.UTF8.GetString(buffer, 0, read));
        }
    }
}
