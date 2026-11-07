using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    public sealed record TransportRequest(
        string Method,
        string Url,
        TransportHeaders Headers,
        ReadOnlyMemory<byte> Body)
    {
        /// <summary>P2：大请求用工厂重建流；必须是可重放的。</summary>
        public Func<CancellationToken, Task<Stream>>? BodyFactory { get; init; }
    }
}
