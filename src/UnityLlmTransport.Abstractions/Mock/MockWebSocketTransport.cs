using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    public sealed class MockWebSocketTransport : IWebSocketTransport
    {
        private readonly Queue<WebSocketMessage> _incoming;
        private readonly List<WebSocketMessage> _sent = new List<WebSocketMessage>();
        private readonly List<WebSocketRequest> _requests = new List<WebSocketRequest>();

        public MockWebSocketTransport(IEnumerable<WebSocketMessage>? incoming = null)
        {
            _incoming = incoming == null
                ? new Queue<WebSocketMessage>()
                : new Queue<WebSocketMessage>(incoming);
        }

        public TransportCapabilities Capabilities => TransportCapabilities.WebSocket;

        public IReadOnlyList<WebSocketMessage> Sent => _sent;
        public IReadOnlyList<WebSocketRequest> Requests => _requests;

        public Task<IWebSocketSession> ConnectAsync(
            WebSocketRequest request,
            WebSocketOptions options,
            CancellationToken cancellationToken)
        {
            _requests.Add(request);
            IWebSocketSession session = new MockWebSocketSession(this, _incoming);
            return Task.FromResult(session);
        }

        private sealed class MockWebSocketSession : IWebSocketSession
        {
            private readonly MockWebSocketTransport _owner;
            private readonly Queue<WebSocketMessage> _incoming;
            private WebSocketState _state = WebSocketState.Open;

            public MockWebSocketSession(MockWebSocketTransport owner, Queue<WebSocketMessage> incoming)
            {
                _owner = owner;
                _incoming = incoming;
            }

            public WebSocketState State => _state;

            public Task SendAsync(WebSocketMessage message, CancellationToken ct)
            {
                _owner._sent.Add(message);
                return Task.CompletedTask;
            }

            public async IAsyncEnumerable<WebSocketMessage> ReceiveAsync(
                [EnumeratorCancellation] CancellationToken ct)
            {
                while (_incoming.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return _incoming.Dequeue();
                    await Task.Yield();
                }
            }

            public Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct)
            {
                _state = WebSocketState.Closed;
                return Task.CompletedTask;
            }

            public void Abort()
            {
                _state = WebSocketState.Aborted;
            }

            public ValueTask DisposeAsync()
            {
                Abort();
                return default;
            }
        }
    }
}
