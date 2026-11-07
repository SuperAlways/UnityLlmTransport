using System;
using System.Collections.Generic;

namespace UnityLlmTransport
{
    /// <summary>
    /// 按 Kind 精确注册；Auto 按 <see cref="TransportFallbackPolicy"/> 的顺序取第一个已注册的候选，
    /// 不做静默降级——未注册的候选直接跳过，已注册但能力不满足的候选抛 <see cref="TransportException"/>（Unsupported）。
    /// </summary>
    public sealed class DefaultTransportSelector : ITransportSelector
    {
        private readonly IReadOnlyDictionary<TransportKind, IStreamingHttpTransport> _httpTransports;
        private readonly IReadOnlyDictionary<TransportKind, IWebSocketTransport> _webSocketTransports;
        private readonly TransportFallbackPolicy _fallback;

        public DefaultTransportSelector(
            IReadOnlyDictionary<TransportKind, IStreamingHttpTransport> httpTransports,
            IReadOnlyDictionary<TransportKind, IWebSocketTransport>? webSocketTransports = null,
            TransportFallbackPolicy? fallbackPolicy = null)
        {
            _httpTransports = httpTransports ?? throw new ArgumentNullException(nameof(httpTransports));
            _webSocketTransports = webSocketTransports
                ?? new Dictionary<TransportKind, IWebSocketTransport>();
            _fallback = fallbackPolicy
                ?? new TransportFallbackPolicy(new[] { TransportKind.Sse, TransportKind.WebSocket });
        }

        public TransportSelection Select(TransportSelectionContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (context.Kind == TransportKind.WebSocket || context.Kind == TransportKind.WebSocketCached)
            {
                return SelectWebSocket(context.Kind);
            }
            if (context.Kind == TransportKind.Sse)
            {
                return SelectHttp(context.Kind);
            }

            for (int i = 0; i < _fallback.Order.Count; i++)
            {
                var kind = _fallback.Order[i];
                if (kind == TransportKind.WebSocket || kind == TransportKind.WebSocketCached)
                {
                    if (_webSocketTransports.ContainsKey(kind))
                    {
                        return SelectWebSocket(kind);
                    }
                }
                else if (kind == TransportKind.Sse && _httpTransports.ContainsKey(kind))
                {
                    return SelectHttp(kind);
                }
            }

            throw new TransportException(
                TransportErrorKind.Unsupported,
                "No transport registered for auto selection.");
        }

        private TransportSelection SelectHttp(TransportKind kind)
        {
            if (!_httpTransports.TryGetValue(kind, out var transport))
            {
                throw new TransportException(
                    TransportErrorKind.Unsupported,
                    "No HTTP transport registered for " + kind + ".");
            }
            if ((transport.Capabilities & TransportCapabilities.StreamingBody) == 0)
            {
                throw new TransportException(
                    TransportErrorKind.Unsupported,
                    "Registered HTTP transport lacks StreamingBody capability.");
            }
            return new TransportSelection(transport, null);
        }

        private TransportSelection SelectWebSocket(TransportKind kind)
        {
            if (!_webSocketTransports.TryGetValue(kind, out var transport))
            {
                throw new TransportException(
                    TransportErrorKind.Unsupported,
                    "No WebSocket transport registered for " + kind + ".");
            }
            if ((transport.Capabilities & TransportCapabilities.WebSocket) == 0)
            {
                throw new TransportException(
                    TransportErrorKind.Unsupported,
                    "Registered WebSocket transport lacks WebSocket capability.");
            }
            return new TransportSelection(null, transport);
        }
    }
}
