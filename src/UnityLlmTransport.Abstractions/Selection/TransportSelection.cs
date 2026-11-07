namespace UnityLlmTransport
{
    public sealed record TransportSelectionContext(
        string Api,
        string Provider,
        string BaseUrl,
        TransportKind Kind);

    public sealed record TransportSelection(
        IStreamingHttpTransport? Http,
        IWebSocketTransport? WebSocket);
}
