using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class HttpClientHandlerFactoryTests
    {
        private static TransportOptions Options()
        {
            return new TransportOptions();
        }

        [Fact]
        public void Default_Is_System_Proxy_Follow_Decompress()
        {
            using var handler = HttpClientHandlerFactory.Create(Options());

            Assert.True(handler.UseProxy);
            Assert.Null(handler.Proxy);
            Assert.True(handler.AllowAutoRedirect);
            Assert.Equal(
                DecompressionMethods.GZip | DecompressionMethods.Deflate,
                handler.AutomaticDecompression);
        }

        [Fact]
        public void Proxy_None_Disables_System_Proxy()
        {
            var options = new TransportOptions { Proxy = new ProxyOptions { Mode = ProxyMode.None } };
            using var handler = HttpClientHandlerFactory.Create(options);

            Assert.False(handler.UseProxy);
        }

        [Fact]
        public void Proxy_Explicit_Sets_WebProxy()
        {
            var options = new TransportOptions
            {
                Proxy = new ProxyOptions
                {
                    Mode = ProxyMode.Explicit,
                    Url = "http://127.0.0.1:7897",
                    Credentials = new NetworkCredential("user", "pass")
                }
            };
            using var handler = HttpClientHandlerFactory.Create(options);

            Assert.True(handler.UseProxy);
            var proxy = Assert.IsType<WebProxy>(handler.Proxy);
            Assert.Equal(new Uri("http://127.0.0.1:7897"), proxy.Address);
            Assert.NotNull(proxy.Credentials);
        }

        [Fact]
        public void Redirect_Error_Disables_Auto_Redirect()
        {
            var options = new TransportOptions { Redirects = RedirectPolicy.Error };
            using var handler = HttpClientHandlerFactory.Create(options);

            Assert.False(handler.AllowAutoRedirect);
        }

        [Fact]
        public void Decompress_False_Disables_Automatic_Decompression()
        {
            var options = new TransportOptions { Decompress = false };
            using var handler = HttpClientHandlerFactory.Create(options);

            Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        }

        [Fact]
        public void Accept_All_Installs_Validation_Callback()
        {
            var options = new TransportOptions { Tls = new TlsOptions { ValidateCertificate = false } };
            using var handler = HttpClientHandlerFactory.Create(options);

            Assert.NotNull(handler.ServerCertificateCustomValidationCallback);
        }

        [Fact]
        public void Client_Certificate_Is_Added()
        {
            using var rsa = RSA.Create(2048);
            var certificateRequest = new CertificateRequest(
                "CN=phase3-test-client",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            using var certificate = certificateRequest.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddMinutes(5));

            var options = new TransportOptions
            {
                Tls = new TlsOptions { ClientCertificate = certificate }
            };
            using var handler = HttpClientHandlerFactory.Create(options);

            Assert.Single(handler.ClientCertificates.Cast<X509Certificate2>());
        }

        [Fact]
        public void Custom_Roots_Are_Unsupported()
        {
            var options = new TransportOptions
            {
                Tls = new TlsOptions { CustomRoots = Array.Empty<X509Certificate2>() }
            };

            var error = Assert.Throws<TransportException>(() => HttpClientHandlerFactory.Create(options));
            Assert.Equal(TransportErrorKind.Unsupported, error.Kind);
        }

        [Fact]
        public void Protocols_Are_Unsupported()
        {
            var options = new TransportOptions
            {
                Tls = new TlsOptions { Protocols = SslProtocols.Tls12 }
            };

            var error = Assert.Throws<TransportException>(() => HttpClientHandlerFactory.Create(options));
            Assert.Equal(TransportErrorKind.Unsupported, error.Kind);
        }
    }
}
