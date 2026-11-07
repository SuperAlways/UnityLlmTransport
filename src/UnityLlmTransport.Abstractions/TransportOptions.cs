using System;
using System.Collections.Generic;

namespace UnityLlmTransport
{
    public sealed record TransportOptions
    {
        public TimeSpan? ConnectTimeout { get; init; }
        public TimeSpan? FirstByteTimeout { get; init; }
        public TimeSpan? IdleTimeout { get; init; }
        public int? MaxResponseBytes { get; init; }
        public ProxyOptions? Proxy { get; init; }
        public TlsOptions? Tls { get; init; }
        public TransportKind Kind { get; init; } = TransportKind.Auto;
        public TimeSpan? WebSocketConnectTimeout { get; init; }
        // IdentityOptions 后置 Phase 4（认证）
        public IReadOnlyDictionary<string, string>? Env { get; init; }
        public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
        public TransportCacheRetention? CacheRetention { get; init; }
        public string? SessionId { get; init; }
        public RedirectPolicy Redirects { get; init; } = RedirectPolicy.Follow;
        public bool Decompress { get; init; } = true;
    }
}
