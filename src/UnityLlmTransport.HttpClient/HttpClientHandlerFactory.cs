using System;
using System.Net;
using System.Net.Http;

namespace UnityLlmTransport
{
    internal static class HttpClientHandlerFactory
    {
        public static HttpClientHandler Create(TransportOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var handler = new HttpClientHandler();
            ConfigureProxy(handler, options.Proxy);
            ConfigureTls(handler, options.Tls);

            handler.AllowAutoRedirect = options.Redirects == RedirectPolicy.Follow;
            handler.AutomaticDecompression = options.Decompress
                ? (DecompressionMethods.GZip | DecompressionMethods.Deflate)
                : DecompressionMethods.None;
            return handler;
        }

        private static void ConfigureProxy(HttpClientHandler handler, ProxyOptions? proxy)
        {
            if (proxy == null || proxy.Mode == ProxyMode.System)
            {
                handler.UseProxy = true;
                handler.Proxy = null;
                return;
            }
            if (proxy.Mode == ProxyMode.None)
            {
                handler.UseProxy = false;
                return;
            }

            if (string.IsNullOrEmpty(proxy.Url))
            {
                throw new TransportException(
                    TransportErrorKind.Protocol,
                    "ProxyOptions.Url is required for ProxyMode.Explicit.");
            }

            var webProxy = new WebProxy(proxy.Url);
            if (proxy.Credentials != null)
            {
                webProxy.Credentials = proxy.Credentials;
            }
            handler.UseProxy = true;
            handler.Proxy = webProxy;
        }

        private static void ConfigureTls(HttpClientHandler handler, TlsOptions? tls)
        {
            if (tls == null)
            {
                return;
            }
            if (tls.CustomRoots != null || tls.Protocols != null)
            {
                throw new TransportException(
                    TransportErrorKind.Unsupported,
                    "HttpClient transport supports default TLS validation, accept-all, and client certificates only.");
            }
            if (!tls.ValidateCertificate)
            {
                handler.ServerCertificateCustomValidationCallback = (message, certificate, chain, errors) => true;
            }
            if (tls.ClientCertificate != null)
            {
                handler.ClientCertificates.Add(tls.ClientCertificate);
            }
        }
    }
}
