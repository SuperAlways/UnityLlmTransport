using System.Threading;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    public interface IStreamingHttpTransport
    {
        TransportCapabilities Capabilities { get; }

        Task<ITransportSession> OpenAsync(
            TransportRequest request,
            TransportOptions options,
            CancellationToken cancellationToken);
    }
}
