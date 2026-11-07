namespace UnityLlmTransport
{
    public enum TransportKind { Auto, Sse, WebSocket, WebSocketCached }

    public enum RedirectPolicy { Follow, Error }

    public enum TransportCacheRetention { PerRequest, SessionScoped }

    public enum ProxyMode { None, System, Explicit }

    public enum TransportErrorKind
    {
        Connect,
        Tls,
        Proxy,
        HttpStatus,
        Handshake,
        Protocol,
        FirstByteTimeout,
        IdleTimeout,
        Cancelled,
        Aborted,
        Overflow,
        Unsupported
    }
}
