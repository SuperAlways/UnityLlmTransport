using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    public interface ITransportRequestFinalizer
    {
        ValueTask<TransportRequest> FinalizeAsync(TransportRequest request, CancellationToken ct);
    }

    /// <summary>管线最内层：紧贴具体 transport，按序执行 finalizer 列表。</summary>
    public sealed class FinalizerTransport : IStreamingHttpTransport
    {
        private readonly IStreamingHttpTransport _inner;
        private readonly IReadOnlyList<ITransportRequestFinalizer> _finalizers;

        public FinalizerTransport(
            IStreamingHttpTransport inner,
            IEnumerable<ITransportRequestFinalizer> finalizers)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            if (finalizers == null)
            {
                throw new ArgumentNullException(nameof(finalizers));
            }
            _finalizers = new List<ITransportRequestFinalizer>(finalizers);
        }

        public TransportCapabilities Capabilities => _inner.Capabilities;

        public async Task<ITransportSession> OpenAsync(
            TransportRequest request,
            TransportOptions options,
            CancellationToken cancellationToken)
        {
            var current = request;
            for (int i = 0; i < _finalizers.Count; i++)
            {
                current = await _finalizers[i].FinalizeAsync(current, cancellationToken).ConfigureAwait(false);
            }
            return await _inner.OpenAsync(current, options, cancellationToken).ConfigureAwait(false);
        }
    }
}
