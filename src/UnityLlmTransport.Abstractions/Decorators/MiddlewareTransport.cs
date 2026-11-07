using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    public interface IRequestMiddleware
    {
        ValueTask<TransportRequest> InvokeAsync(TransportRequest request, CancellationToken ct);
    }

    public sealed class MiddlewareTransport : IStreamingHttpTransport
    {
        private readonly IStreamingHttpTransport _inner;
        private readonly IReadOnlyList<IRequestMiddleware> _middlewares;

        public MiddlewareTransport(IStreamingHttpTransport inner, IEnumerable<IRequestMiddleware> middlewares)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            if (middlewares == null)
            {
                throw new ArgumentNullException(nameof(middlewares));
            }
            _middlewares = new List<IRequestMiddleware>(middlewares);
        }

        public TransportCapabilities Capabilities => _inner.Capabilities;

        public async Task<ITransportSession> OpenAsync(
            TransportRequest request,
            TransportOptions options,
            CancellationToken cancellationToken)
        {
            var current = request;
            for (int i = 0; i < _middlewares.Count; i++)
            {
                current = await _middlewares[i].InvokeAsync(current, cancellationToken).ConfigureAwait(false);
            }
            return await _inner.OpenAsync(current, options, cancellationToken).ConfigureAwait(false);
        }
    }
}
