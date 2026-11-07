namespace UnityLlmTransport
{
    public sealed record UnityWebRequestTransportOptions
    {
        public int MaxBufferedBytes { get; init; } = 4 * 1024 * 1024;
        public int DownloadBufferSize { get; init; } = 16 * 1024;
    }
}
