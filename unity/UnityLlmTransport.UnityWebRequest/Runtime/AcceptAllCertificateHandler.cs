using UnityEngine.Networking;

namespace UnityLlmTransport
{
    public sealed class AcceptAllCertificateHandler : CertificateHandler
    {
        protected override bool ValidateCertificate(byte[] certificateData)
        {
            return true;
        }
    }
}
