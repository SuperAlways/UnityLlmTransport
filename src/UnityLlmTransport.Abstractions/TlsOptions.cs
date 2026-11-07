using System.Collections.Generic;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace UnityLlmTransport
{
    public sealed record TlsOptions
    {
        public bool ValidateCertificate { get; init; } = true;
        public X509Certificate2? ClientCertificate { get; init; }
        public IReadOnlyList<X509Certificate2>? CustomRoots { get; init; }
        public SslProtocols? Protocols { get; init; }
    }
}
