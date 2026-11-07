using System;

namespace UnityLlmTransport
{
    [Flags]
    public enum TransportCapabilities
    {
        None = 0,
        StreamingBody = 1 << 0,
        WebSocket = 1 << 1,
        ExplicitProxy = 1 << 2,
        Http2 = 1 << 3,
        TlsCertificateHandler = 1 << 4,
        Compression = 1 << 5,
        RequestFinalizer = 1 << 6,
        SystemProxy = 1 << 7,
        ProxyAuthentication = 1 << 8,
        WebSocketOverTls = 1 << 9
    }
}
