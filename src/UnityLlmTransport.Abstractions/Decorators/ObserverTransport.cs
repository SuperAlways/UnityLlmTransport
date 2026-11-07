using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    public sealed record TransportResponseInfo(int StatusCode, TransportHeaders Headers);

    public interface ITransportObserver
    {
        ValueTask OnResponseAsync(TransportResponseInfo response, CancellationToken ct);
        ValueTask OnBodyChunkAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct);
    }

    public sealed class ObserverTransport : IStreamingHttpTransport
    {
        private readonly IStreamingHttpTransport _inner;
        private readonly ITransportObserver _observer;

        public ObserverTransport(IStreamingHttpTransport inner, ITransportObserver observer)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _observer = observer ?? throw new ArgumentNullException(nameof(observer));
        }

        public TransportCapabilities Capabilities => _inner.Capabilities;

        public async Task<ITransportSession> OpenAsync(
            TransportRequest request,
            TransportOptions options,
            CancellationToken cancellationToken)
        {
            var session = await _inner.OpenAsync(request, options, cancellationToken).ConfigureAwait(false);
            try
            {
                await _observer.OnResponseAsync(
                    new TransportResponseInfo(session.StatusCode, session.Headers),
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            return new ObserverSession(session, _observer);
        }

        private sealed class ObserverSession : ITransportSession
        {
            private readonly ITransportSession _inner;
            private readonly ITransportObserver _observer;
            private TeeStream? _body;

            public ObserverSession(ITransportSession inner, ITransportObserver observer)
            {
                _inner = inner;
                _observer = observer;
            }

            public int StatusCode => _inner.StatusCode;
            public TransportHeaders Headers => _inner.Headers;
            public Stream Body => _body ??= new TeeStream(_inner.Body, _observer);
            public void Abort() => _inner.Abort();
            public ValueTask DisposeAsync() => _inner.DisposeAsync();
        }
    }
}
