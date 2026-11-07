using System;
using System.Collections.Generic;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    [Collection("ws-server")]
    public class WebSocketTlsAndOptionsTests
    {
        private readonly WsServerFixture _fixture;

        public WebSocketTlsAndOptionsTests(WsServerFixture fixture)
        {
            _fixture = fixture;
        }

        private WebSocketRequest Request(string basePath = "/ws/echo", string? baseUrl = null)
        {
            return new WebSocketRequest((baseUrl ?? _fixture.Server.HttpBase) + basePath, new TransportHeaders());
        }

        [Fact]
        public async Task Invalid_Scheme_Is_Protocol()
        {
            var transport = new ClientWebSocketTransport();
            var error = await Assert.ThrowsAsync<TransportException>(() => transport.ConnectAsync(
                new WebSocketRequest("ftp://127.0.0.1/x", new TransportHeaders()),
                new WebSocketOptions(),
                CancellationToken.None));
            Assert.Equal(TransportErrorKind.Protocol, error.Kind);
        }

        [Fact]
        public async Task Explicit_Proxy_Is_Unsupported()
        {
            var transport = new ClientWebSocketTransport();
            var error = await Assert.ThrowsAsync<TransportException>(() => transport.ConnectAsync(
                Request(),
                new WebSocketOptions
                {
                    Proxy = new ProxyOptions { Mode = ProxyMode.Explicit, Url = "http://127.0.0.1:7897" }
                },
                CancellationToken.None));
            Assert.Equal(TransportErrorKind.Unsupported, error.Kind);
        }

        [Fact]
        public async Task Proxy_None_Is_Unsupported()
        {
            var transport = new ClientWebSocketTransport();
            var error = await Assert.ThrowsAsync<TransportException>(() => transport.ConnectAsync(
                Request(),
                new WebSocketOptions { Proxy = new ProxyOptions { Mode = ProxyMode.None } },
                CancellationToken.None));
            Assert.Equal(TransportErrorKind.Unsupported, error.Kind);
        }

        [Fact]
        public async Task Redirect_Follow_Is_Unsupported()
        {
            var transport = new ClientWebSocketTransport();
            var error = await Assert.ThrowsAsync<TransportException>(() => transport.ConnectAsync(
                Request(),
                new WebSocketOptions { Redirects = RedirectPolicy.Follow },
                CancellationToken.None));
            Assert.Equal(TransportErrorKind.Unsupported, error.Kind);
        }

        [Fact]
        public async Task Custom_Tls_Is_Unsupported()
        {
            var transport = new ClientWebSocketTransport();
            var error = await Assert.ThrowsAsync<TransportException>(() => transport.ConnectAsync(
                Request(),
                new WebSocketOptions
                {
                    Tls = new TlsOptions { CustomRoots = Array.Empty<X509Certificate2>() }
                },
                CancellationToken.None));
            Assert.Equal(TransportErrorKind.Unsupported, error.Kind);
        }

        [Fact]
        public async Task SelfSigned_Default_Is_Tls()
        {
            var transport = new ClientWebSocketTransport();
            var error = await Assert.ThrowsAsync<TransportException>(() => transport.ConnectAsync(
                Request("/ws/echo", _fixture.Server.HttpsBase),
                new WebSocketOptions(),
                CancellationToken.None));
            Assert.Equal(TransportErrorKind.Tls, error.Kind);
        }

        [Fact]
        public async Task SelfSigned_AcceptAll_Connects()
        {
            var transport = new ClientWebSocketTransport();
            var session = await transport.ConnectAsync(
                Request("/ws/echo", _fixture.Server.HttpsBase),
                new WebSocketOptions { Tls = new TlsOptions { ValidateCertificate = false } },
                CancellationToken.None);
            await session.SendAsync(
                new WebSocketMessage(WebSocketMessageType.Text, System.Text.Encoding.UTF8.GetBytes("tls"), true),
                CancellationToken.None);

            await foreach (var message in session.ReceiveAsync(CancellationToken.None))
            {
                Assert.Equal("echo:tls", System.Text.Encoding.UTF8.GetString(message.Payload.ToArray()));
                break;
            }
            await session.DisposeAsync();
        }

        [Fact]
        public void Capabilities_Are_Exact()
        {
            var transport = new ClientWebSocketTransport();
            Assert.True(transport.Capabilities.HasFlag(TransportCapabilities.WebSocket));
            Assert.True(transport.Capabilities.HasFlag(TransportCapabilities.WebSocketOverTls));
            Assert.True(transport.Capabilities.HasFlag(TransportCapabilities.SystemProxy));
            Assert.True(transport.Capabilities.HasFlag(TransportCapabilities.TlsCertificateHandler));
            Assert.False(transport.Capabilities.HasFlag(TransportCapabilities.StreamingBody));
            Assert.False(transport.Capabilities.HasFlag(TransportCapabilities.ExplicitProxy));
            Assert.False(transport.Capabilities.HasFlag(TransportCapabilities.Http2));
            Assert.False(transport.Capabilities.HasFlag(TransportCapabilities.RequestFinalizer));
        }

        [Fact]
        public void Selector_Selects_WebSocket_And_Rejects_Cached()
        {
            var transport = new ClientWebSocketTransport();
            var selector = new DefaultTransportSelector(
                new Dictionary<TransportKind, IStreamingHttpTransport>(),
                new Dictionary<TransportKind, IWebSocketTransport>
                {
                    [TransportKind.WebSocket] = transport
                });

            var selection = selector.Select(
                new TransportSelectionContext("chat", "test", "http://localhost", TransportKind.WebSocket));
            Assert.Same(transport, selection.WebSocket);

            Assert.Throws<TransportException>(() => selector.Select(
                new TransportSelectionContext("chat", "test", "http://localhost", TransportKind.WebSocketCached)));
        }
    }
}
