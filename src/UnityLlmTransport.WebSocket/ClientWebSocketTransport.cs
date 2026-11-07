using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    public sealed class ClientWebSocketTransport : IWebSocketTransport
    {
        private static readonly TransportCapabilities DefaultCapabilities =
            TransportCapabilities.WebSocket |
            TransportCapabilities.WebSocketOverTls |
            TransportCapabilities.SystemProxy |
            TransportCapabilities.TlsCertificateHandler;

        // 说明（2026-11-08，Phase 6 稳定性加固）：MaxMessageBytes==null（无限额）对客户端是
        // 不设防的默认值——恶意/异常服务端一条消息即可撑爆内存。故 null 落到 16MiB 安全默认，
        // 真要不限额请显式传 int.MaxValue。
        public const int DefaultMaxMessageBytes = 16 * 1024 * 1024;

        public TransportCapabilities Capabilities => DefaultCapabilities;

        public async Task<IWebSocketSession> ConnectAsync(
            WebSocketRequest request,
            WebSocketOptions options,
            CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            Uri uri;
            try
            {
                uri = new Uri(request.Url, UriKind.Absolute);
            }
            catch (Exception ex)
            {
                throw new TransportException(
                    TransportErrorKind.Protocol,
                    "Invalid WebSocket URL: " + request.Url,
                    ex,
                    null,
                    null,
                    null,
                    false);
            }
            if (uri.Scheme != "ws" && uri.Scheme != "wss")
            {
                throw new TransportException(
                    TransportErrorKind.Protocol,
                    "WebSocket URL must use ws:// or wss://; got: " + uri.Scheme);
            }

            var socket = new ClientWebSocket();
            try
            {
                ConfigureSocket(socket.Options, request, options);
            }
            catch (TransportException)
            {
                socket.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                // BCL 的 AddSubProtocol/SetRequestHeader 对非法 token/控制字符同步抛裸 ArgumentException；
                // 按 spec §5.1/§5.5 归一 TransportException（非法请求配置 → Protocol），不许逃逸原生异常。
                socket.Dispose();
                throw WebSocketErrorClassifier.ClassifyConnect(ex, cancellationToken.IsCancellationRequested, false);
            }

            CancellationTokenSource? timeoutCts = null;
            CancellationTokenSource? linkedCts = null;
            try
            {
                CancellationToken token = cancellationToken;
                if (options.ConnectTimeout.HasValue && options.ConnectTimeout.Value > TimeSpan.Zero)
                {
                    timeoutCts = new CancellationTokenSource(options.ConnectTimeout.Value);
                    linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                    token = linkedCts.Token;
                }

                try
                {
                    await socket.ConnectAsync(uri, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    bool callerCancelled = cancellationToken.IsCancellationRequested;
                    bool connectTimeout = timeoutCts != null &&
                                          timeoutCts.IsCancellationRequested &&
                                          !callerCancelled;
                    socket.Dispose();
                    throw WebSocketErrorClassifier.ClassifyConnect(ex, callerCancelled, connectTimeout);
                }

                return new ClientWebSocketSession(socket, options, cancellationToken);
            }
            finally
            {
                linkedCts?.Dispose();
                timeoutCts?.Dispose();
            }
        }

        private static void ConfigureSocket(
            ClientWebSocketOptions socketOptions,
            WebSocketRequest request,
            WebSocketOptions options)
        {
            if (options.Proxy != null && options.Proxy.Mode != ProxyMode.System)
            {
                throw new TransportException(
                    TransportErrorKind.Unsupported,
                    "ClientWebSocket supports only the system proxy.");
            }

            if (options.Redirects != RedirectPolicy.Error)
            {
                throw new TransportException(
                    TransportErrorKind.Unsupported,
                    "ClientWebSocket does not support configurable handshake redirects.");
            }

            if (options.Tls != null)
            {
                if (options.Tls.CustomRoots != null || options.Tls.Protocols != null)
                {
                    throw new TransportException(
                        TransportErrorKind.Unsupported,
                        "ClientWebSocket supports default validation, accept-all, and client certificates only.");
                }
                if (!options.Tls.ValidateCertificate)
                {
                    socketOptions.RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => true;
                }
                if (options.Tls.ClientCertificate != null)
                {
                    socketOptions.ClientCertificates.Add(options.Tls.ClientCertificate);
                }
            }

            if (!string.IsNullOrEmpty(options.SubProtocol))
            {
                socketOptions.AddSubProtocol(options.SubProtocol);
            }

            if (!string.IsNullOrEmpty(options.Origin) &&
                !request.Headers.ContainsKey("Origin"))
            {
                socketOptions.SetRequestHeader("Origin", options.Origin);
            }

            foreach (var pair in request.Headers)
            {
                if (pair.Value != null)
                {
                    socketOptions.SetRequestHeader(pair.Key, pair.Value);
                }
            }

            if (options.KeepAliveInterval.HasValue && options.KeepAliveInterval.Value > TimeSpan.Zero)
            {
                socketOptions.KeepAliveInterval = options.KeepAliveInterval.Value;
            }
        }
    }
}
