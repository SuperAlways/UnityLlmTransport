using System;

namespace UnityLlmTransport
{
    internal static class HttpClientRequestValidator
    {
        public static void Validate(TransportOptions options, bool injectedClient)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }
            if (!injectedClient)
            {
                return;
            }

            if (options.Proxy != null && options.Proxy.Mode != ProxyMode.System)
            {
                throw new TransportException(
                    TransportErrorKind.Unsupported,
                    "Injected HttpClient has a fixed proxy; configure the client instead of TransportOptions.Proxy.");
            }

            if (options.Tls != null &&
                (!options.Tls.ValidateCertificate ||
                 options.Tls.ClientCertificate != null ||
                 options.Tls.CustomRoots != null ||
                 options.Tls.Protocols != null))
            {
                throw new TransportException(
                    TransportErrorKind.Unsupported,
                    "Injected HttpClient has fixed TLS settings; configure the client handler instead.");
            }

            if (options.Redirects != RedirectPolicy.Follow)
            {
                throw new TransportException(
                    TransportErrorKind.Unsupported,
                    "Injected HttpClient has fixed redirect settings; configure the client handler instead.");
            }

            if (!options.Decompress)
            {
                throw new TransportException(
                    TransportErrorKind.Unsupported,
                    "Injected HttpClient has fixed decompression settings; configure the client handler instead.");
            }
        }
    }
}
