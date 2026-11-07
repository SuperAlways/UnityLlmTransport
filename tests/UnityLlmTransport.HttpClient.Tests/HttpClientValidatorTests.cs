using System;
using System.Threading;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class HttpClientValidatorTests
    {
        private static TransportOptions Options()
        {
            return new TransportOptions();
        }

        [Fact]
        public void Injected_Client_Allows_Defaults()
        {
            HttpClientRequestValidator.Validate(Options(), true);
        }

        [Fact]
        public void Injected_Client_Rejects_Explicit_Proxy()
        {
            var options = new TransportOptions
            {
                Proxy = new ProxyOptions { Mode = ProxyMode.Explicit, Url = "http://127.0.0.1:7897" }
            };

            var error = Assert.Throws<TransportException>(() => HttpClientRequestValidator.Validate(options, true));
            Assert.Equal(TransportErrorKind.Unsupported, error.Kind);
        }

        [Fact]
        public void Injected_Client_Rejects_Tls_Override()
        {
            var options = new TransportOptions
            {
                Tls = new TlsOptions { ValidateCertificate = false }
            };

            var error = Assert.Throws<TransportException>(() => HttpClientRequestValidator.Validate(options, true));
            Assert.Equal(TransportErrorKind.Unsupported, error.Kind);
        }

        [Fact]
        public void Injected_Client_Rejects_Redirect_Error()
        {
            var options = new TransportOptions { Redirects = RedirectPolicy.Error };

            var error = Assert.Throws<TransportException>(() => HttpClientRequestValidator.Validate(options, true));
            Assert.Equal(TransportErrorKind.Unsupported, error.Kind);
        }

        [Fact]
        public void Injected_Client_Rejects_Decompress_False()
        {
            var options = new TransportOptions { Decompress = false };

            var error = Assert.Throws<TransportException>(() => HttpClientRequestValidator.Validate(options, true));
            Assert.Equal(TransportErrorKind.Unsupported, error.Kind);
        }

        [Fact]
        public void Default_Transport_Allows_All_Options()
        {
            var options = new TransportOptions
            {
                Proxy = new ProxyOptions { Mode = ProxyMode.Explicit, Url = "http://127.0.0.1:7897" },
                Tls = new TlsOptions { ValidateCertificate = false },
                Redirects = RedirectPolicy.Error,
                Decompress = false
            };

            HttpClientRequestValidator.Validate(options, false);
        }
    }
}
