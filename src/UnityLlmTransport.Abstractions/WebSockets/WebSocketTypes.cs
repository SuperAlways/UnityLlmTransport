using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    public interface IWebSocketTransport
    {
        TransportCapabilities Capabilities { get; }
        Task<IWebSocketSession> ConnectAsync(
            WebSocketRequest request,
            WebSocketOptions options,
            CancellationToken cancellationToken);
    }

    public sealed record WebSocketRequest(string Url, TransportHeaders Headers);

    public sealed record WebSocketOptions
    {
        public TimeSpan? ConnectTimeout { get; init; }
        public TimeSpan? IdleTimeout { get; init; }
        public TimeSpan? KeepAliveInterval { get; init; }
        /// <summary>
        /// 单条消息累计上限（字节）。null 时由具体 WebSocket 传输施加安全默认上限
        /// （当前 ClientWebSocketTransport = 16 MiB）；显式值优先。
        /// 如需无上限，显式设为 int.MaxValue（注意累计计数在 &gt;2GiB 时有 int 溢出风险）。
        /// </summary>
        public int? MaxMessageBytes { get; init; }
        public ProxyOptions? Proxy { get; init; }
        public TlsOptions? Tls { get; init; }
        public string? SubProtocol { get; init; }
        public string? Origin { get; init; }
        public RedirectPolicy Redirects { get; init; } = RedirectPolicy.Error;
    }

    public enum WebSocketMessageType { Text, Binary, Close }

    public sealed record WebSocketMessage(
        WebSocketMessageType Type,
        ReadOnlyMemory<byte> Payload,
        bool EndOfMessage,
        WebSocketCloseStatus? CloseStatus = null);

    public interface IWebSocketSession : IAsyncDisposable
    {
        WebSocketState State { get; }
        Task SendAsync(WebSocketMessage message, CancellationToken ct);
        IAsyncEnumerable<WebSocketMessage> ReceiveAsync(CancellationToken ct);
        Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct);
        void Abort();
    }
}
