using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class SelectorTests
    {
        private static TransportSelectionContext Context(TransportKind kind)
        {
            return new TransportSelectionContext("chat", "test", "http://localhost", kind);
        }

        [Fact]
        public void Explicit_Sse_Selects_Http_Transport()
        {
            var http = new MockTransport(_ => new MockResponse());
            var selector = new DefaultTransportSelector(
                new Dictionary<TransportKind, IStreamingHttpTransport> { [TransportKind.Sse] = http });

            var selection = selector.Select(Context(TransportKind.Sse));

            Assert.Same(http, selection.Http);
            Assert.Null(selection.WebSocket);
        }

        [Fact]
        public void Missing_Transport_Throws_Unsupported()
        {
            var selector = new DefaultTransportSelector(
                new Dictionary<TransportKind, IStreamingHttpTransport>());

            var error = Assert.Throws<TransportException>(() => selector.Select(Context(TransportKind.Sse)));

            Assert.Equal(TransportErrorKind.Unsupported, error.Kind);
        }

        [Fact]
        public void Http_Without_StreamingBody_Throws_Unsupported()
        {
            var http = new CapabilitylessTransport();
            var selector = new DefaultTransportSelector(
                new Dictionary<TransportKind, IStreamingHttpTransport> { [TransportKind.Sse] = http });

            Assert.Throws<TransportException>(() => selector.Select(Context(TransportKind.Sse)));
        }

        [Fact]
        public void Auto_Uses_Fallback_Order()
        {
            var candidate = new MockTransport(_ => new MockResponse());
            var webSocket = new MockWebSocketTransport();
            var selector = new DefaultTransportSelector(
                new Dictionary<TransportKind, IStreamingHttpTransport> { [TransportKind.Sse] = candidate },
                new Dictionary<TransportKind, IWebSocketTransport> { [TransportKind.WebSocket] = webSocket },
                new TransportFallbackPolicy(new[] { TransportKind.WebSocket, TransportKind.Sse }));

            var selection = selector.Select(Context(TransportKind.Auto));

            Assert.Same(webSocket, selection.WebSocket);
            Assert.Null(selection.Http);
        }

        [Fact]
        public void WebSocket_Without_Capability_Throws_Unsupported()
        {
            var webSocket = new FakeWebSocketTransport(TransportCapabilities.None);
            var selector = new DefaultTransportSelector(
                new Dictionary<TransportKind, IStreamingHttpTransport>(),
                new Dictionary<TransportKind, IWebSocketTransport> { [TransportKind.WebSocket] = webSocket });

            Assert.Throws<TransportException>(() => selector.Select(Context(TransportKind.WebSocket)));
        }

        private sealed class CapabilitylessTransport : IStreamingHttpTransport
        {
            public TransportCapabilities Capabilities => TransportCapabilities.SystemProxy;

            public Task<ITransportSession> OpenAsync(
                TransportRequest request,
                TransportOptions options,
                CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class FakeWebSocketTransport : IWebSocketTransport
        {
            public FakeWebSocketTransport(TransportCapabilities capabilities)
            {
                Capabilities = capabilities;
            }

            public TransportCapabilities Capabilities { get; }

            public Task<IWebSocketSession> ConnectAsync(
                WebSocketRequest request,
                WebSocketOptions options,
                CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }
        }
    }
}
