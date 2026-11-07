using System.Net.Http;

namespace UnityLlmTransport
{
    public sealed record HttpClientTransportOptions
    {
        /// <summary>注入共享 HttpClient（复用连接池）。非 null 时 per-request Proxy/Tls/Redirects/Decompress 只允许默认值。</summary>
        public HttpClient? Client { get; init; }

        /// <summary>非 null 时覆盖传输默认能力声明（注入自定义 client 的高级用法）。</summary>
        public TransportCapabilities? CapabilitiesOverride { get; init; }
    }
}
