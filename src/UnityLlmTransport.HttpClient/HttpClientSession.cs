using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    internal sealed class HttpClientSession : ITransportSession
    {
        private readonly HttpResponseMessage _response;
        private readonly HttpResponseReadStream _body;
        private readonly HttpClient _client;
        private readonly bool _ownsClient;
        private readonly CancellationTokenRegistration _registration;
        private int _disposed;

        public HttpClientSession(
            HttpResponseMessage response,
            HttpResponseReadStream body,
            int statusCode,
            TransportHeaders headers,
            HttpClient client,
            bool ownsClient,
            CancellationToken lifetimeToken)
        {
            _response = response;
            _body = body;
            _client = client;
            _ownsClient = ownsClient;
            StatusCode = statusCode;
            Headers = headers;
            _registration = lifetimeToken.Register(OnLifetimeCancelled);
        }

        public int StatusCode { get; }
        public TransportHeaders Headers { get; }
        public Stream Body => _body;

        public void Abort()
        {
            AbortWith(TransportErrorKind.Aborted);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                AbortWith(TransportErrorKind.Aborted);
                _body.Dispose();
                _registration.Dispose();
                if (_ownsClient)
                {
                    _client.Dispose();
                }
            }
            return default;
        }

        private void OnLifetimeCancelled()
        {
            AbortWith(TransportErrorKind.Cancelled);
        }

        private void AbortWith(TransportErrorKind kind)
        {
            _body.Abort(kind);
            try
            {
                _response.Dispose();
            }
            catch (Exception)
            {
            }
        }
    }
}
