namespace UnityLlmTransport
{
    /// <summary>宿主 runner 判定“首字节已到”的最小契约；由 Unity DownloadHandler 实现。</summary>
    public interface IStreamingDownloadHandler
    {
        bool FirstDataReceived { get; }
    }
}
