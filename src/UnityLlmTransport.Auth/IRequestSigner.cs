using System.Threading;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    public interface IRequestSigner
    {
        /// <summary>消费方实现具体签名算法（SigV4/ADC/Azure AD 等）。</summary>
        ValueTask<TransportRequest> SignAsync(TransportRequest request, CancellationToken ct);
    }
}
