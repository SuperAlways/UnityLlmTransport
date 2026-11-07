using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using UnityLlmTransport.Sse;
using Xunit;

namespace UnityLlmTransport.Tests
{
    [Collection("phase3-server")]
    public class HttpClientTransportTests
    {
        private readonly ServerFixture _fixture;

        public HttpClientTransportTests(ServerFixture fixture)
        {
            _fixture = fixture;
        }

        private HttpClientTransport CreateTransport()
        {
            return new HttpClientTransport();
        }

        private TransportRequest Request(string url, TransportHeaders? headers = null, string body = "{}")
        {
            return new TransportRequest(
                "POST",
                url,
                headers ?? new TransportHeaders(),
                new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(body)));
        }

        private static async Task<List<string>> ParseAsync(Stream stream)
        {
            var items = new List<string>();
            await foreach (var item in SseParser.Create(stream).EnumerateAsync())
            {
                items.Add(item.Data);
            }
            return items;
        }

        [Fact]
        public async Task Streaming_Opens_Before_Body_Completes()
        {
            var transport = CreateTransport();
            var sw = Stopwatch.StartNew();
            var session = await transport.OpenAsync(
                Request(_fixture.Server.HttpBase + "/sse"),
                new TransportOptions(),
                CancellationToken.None);
            long openMs = sw.ElapsedMilliseconds;

            var items = await ParseAsync(session.Body);
            await session.DisposeAsync();

            Assert.Equal(6, items.Count);
            Assert.True(openMs < sw.ElapsedMilliseconds);
        }

        [Fact]
        public async Task Non2xx_Returns_Session_With_Error_Body()
        {
            var transport = CreateTransport();
            var session = await transport.OpenAsync(
                Request(_fixture.Server.HttpBase + "/sse-error"),
                new TransportOptions(),
                CancellationToken.None);

            var text = await ReadAllTextAsync(session.Body, CancellationToken.None);
            await session.DisposeAsync();

            Assert.Equal(500, session.StatusCode);
            Assert.Contains("boom", text, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Captures_Status_And_Headers()
        {
            var transport = CreateTransport();
            var session = await transport.OpenAsync(
                Request(_fixture.Server.HttpBase + "/echo"),
                new TransportOptions(),
                CancellationToken.None);
            await session.DisposeAsync();

            Assert.Equal(200, session.StatusCode);
            Assert.True(session.Headers.TryGetValue("content-type", out var contentType));
            Assert.Contains("text/plain", contentType, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Gzip_Is_Decompressed_By_Default()
        {
            var transport = CreateTransport();
            var session = await transport.OpenAsync(
                Request(_fixture.Server.HttpBase + "/gzip"),
                new TransportOptions(),
                CancellationToken.None);
            var items = await ParseAsync(session.Body);
            await session.DisposeAsync();

            Assert.Equal(6, items.Count);
        }

        [Fact]
        public async Task Redirect_Follow_Works_And_Error_Throws()
        {
            var transport = CreateTransport();
            var follow = await transport.OpenAsync(
                Request(_fixture.Server.HttpBase + "/redirect"),
                new TransportOptions(),
                CancellationToken.None);
            var items = await ParseAsync(follow.Body);
            await follow.DisposeAsync();
            Assert.Equal(6, items.Count);

            var error = await Assert.ThrowsAsync<TransportException>(() => transport.OpenAsync(
                Request(_fixture.Server.HttpBase + "/redirect"),
                new TransportOptions { Redirects = RedirectPolicy.Error },
                CancellationToken.None));
            Assert.Equal(TransportErrorKind.Protocol, error.Kind);
        }

        [Fact]
        public async Task Null_Header_Is_Not_Sent()
        {
            var headers = new TransportHeaders
            {
                ["X-Skip"] = null,
                ["X-Test"] = "yes"
            };
            var transport = CreateTransport();
            var session = await transport.OpenAsync(
                Request(_fixture.Server.HttpBase + "/echo-headers", headers),
                new TransportOptions(),
                CancellationToken.None);
            var text = await ReadAllTextAsync(session.Body, CancellationToken.None);
            await session.DisposeAsync();

            using var json = JsonDocument.Parse(text);
            Assert.False(json.RootElement.TryGetProperty("X-Skip", out _));
            Assert.True(json.RootElement.TryGetProperty("X-Test", out var received));
            Assert.Equal("yes", received.GetString());
        }

        [Fact]
        public async Task Gzip_False_Leaves_Compressed_Bytes()
        {
            var transport = CreateTransport();
            var session = await transport.OpenAsync(
                Request(_fixture.Server.HttpBase + "/gzip"),
                new TransportOptions { Decompress = false },
                CancellationToken.None);

            var buffer = new byte[2];
            var read = await session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None);
            await session.DisposeAsync();

            Assert.True(read >= 2);
            Assert.Equal(0x1f, buffer[0]);
            Assert.Equal(0x8b, buffer[1]);
        }

        [Fact]
        public void Capabilities_Are_Exact()
        {
            var transport = CreateTransport();
            Assert.True(transport.Capabilities.HasFlag(TransportCapabilities.StreamingBody));
            Assert.True(transport.Capabilities.HasFlag(TransportCapabilities.SystemProxy));
            Assert.True(transport.Capabilities.HasFlag(TransportCapabilities.ExplicitProxy));
            Assert.True(transport.Capabilities.HasFlag(TransportCapabilities.ProxyAuthentication));
            Assert.True(transport.Capabilities.HasFlag(TransportCapabilities.TlsCertificateHandler));
            Assert.True(transport.Capabilities.HasFlag(TransportCapabilities.Compression));
            Assert.True(transport.Capabilities.HasFlag(TransportCapabilities.RequestFinalizer));
            Assert.False(transport.Capabilities.HasFlag(TransportCapabilities.WebSocket));
            Assert.False(transport.Capabilities.HasFlag(TransportCapabilities.Http2));
        }

        [Fact]
        public async Task Abort_Makes_Reads_Throw_Aborted()
        {
            var transport = CreateTransport();
            var session = await transport.OpenAsync(
                Request(_fixture.Server.HttpBase + "/sse"),
                new TransportOptions(),
                CancellationToken.None);
            var buffer = new byte[64];
            await session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None);
            session.Abort();

            var error = await Assert.ThrowsAsync<TransportException>(
                () => session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None).AsTask());
            Assert.Equal(TransportErrorKind.Aborted, error.Kind);
            await session.DisposeAsync();
        }

        private static async Task<string> ReadAllTextAsync(Stream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[256];
            var builder = new StringBuilder();
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
            return builder.ToString();
        }
    }
}
