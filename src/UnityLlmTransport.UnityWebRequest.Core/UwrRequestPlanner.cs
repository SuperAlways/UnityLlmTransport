using System;
using System.Collections.Generic;

namespace UnityLlmTransport
{
    public static class UwrRequestPlanner
    {
        /// <summary>纯决策：能力不支持抛 Unsupported，不静默降级。</summary>
        public static UwrRequestPlan Create(TransportRequest request, TransportOptions options)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (request.BodyFactory != null)
            {
                throw new TransportException(
                    TransportErrorKind.Unsupported,
                    "UWR does not support streaming request bodies (BodyFactory).");
            }

            if (options.Proxy != null && options.Proxy.Mode != ProxyMode.System)
            {
                throw new TransportException(
                    TransportErrorKind.Unsupported,
                    "UWR supports only the system proxy (ProxyMode." + options.Proxy.Mode + " is unsupported).");
            }

            bool acceptAll = false;
            if (options.Tls != null)
            {
                if (options.Tls.ClientCertificate != null ||
                    options.Tls.CustomRoots != null ||
                    options.Tls.Protocols != null)
                {
                    throw new TransportException(
                        TransportErrorKind.Unsupported,
                        "UWR supports only default TLS validation or accept-all; custom certificate/protocols are unsupported.");
                }
                acceptAll = !options.Tls.ValidateCertificate;
            }

            int redirectLimit = options.Redirects == RedirectPolicy.Error ? 0 : 32;

            var headers = new TransportHeaders();
            var suppressed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in request.Headers)
            {
                if (pair.Value == null)
                {
                    suppressed.Add(pair.Key);
                }
                else
                {
                    headers[pair.Key] = pair.Value;
                }
            }

            if (options.Decompress &&
                !headers.ContainsKey("Accept-Encoding") &&
                !suppressed.Contains("Accept-Encoding"))
            {
                headers["Accept-Encoding"] = "identity";
            }

            return new UwrRequestPlan(headers, redirectLimit, acceptAll);
        }
    }
}
