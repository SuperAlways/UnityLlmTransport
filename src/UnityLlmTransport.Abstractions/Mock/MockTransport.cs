using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    public sealed class MockTransport : IStreamingHttpTransport
    {
        private readonly Func<TransportRequest, MockResponse> _handler;
        private readonly List<TransportRequest> _requests = new List<TransportRequest>();

        public MockTransport(Func<TransportRequest, MockResponse> handler)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        public TransportCapabilities Capabilities => TransportCapabilities.StreamingBody;

        public IReadOnlyList<TransportRequest> Requests => _requests;

        public Task<ITransportSession> OpenAsync(
            TransportRequest request,
            TransportOptions options,
            CancellationToken cancellationToken)
        {
            _requests.Add(request);
            var response = _handler(request);
            if (response.Error != null)
            {
                return Task.FromException<ITransportSession>(response.Error);
            }

            var body = new ScriptedResponseStream(response.Chunks, response.ChunkDelay, cancellationToken);
            ITransportSession session = new MockSession(
                response.StatusCode,
                response.Headers ?? new TransportHeaders(),
                body);
            return Task.FromResult(session);
        }

        private sealed class MockSession : ITransportSession
        {
            private readonly ScriptedResponseStream _body;

            public MockSession(int statusCode, TransportHeaders headers, ScriptedResponseStream body)
            {
                StatusCode = statusCode;
                Headers = headers;
                _body = body;
            }

            public int StatusCode { get; }
            public TransportHeaders Headers { get; }
            public System.IO.Stream Body => _body;

            public void Abort()
            {
                _body.Abort();
            }

            public ValueTask DisposeAsync()
            {
                _body.Abort();
                return default;
            }
        }
    }
}
