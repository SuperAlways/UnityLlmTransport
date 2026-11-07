using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    public sealed class SignerFinalizer : ITransportRequestFinalizer
    {
        private readonly IRequestSigner _signer;

        public SignerFinalizer(IRequestSigner signer)
        {
            _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        }

        public ValueTask<TransportRequest> FinalizeAsync(TransportRequest request, CancellationToken ct)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            return _signer.SignAsync(request, ct);
        }
    }
}
